using System;
using System.Collections.Generic;
using UnityEngine;
using extOSC;
using CityTwin.Calibration;
using CityTwin.Core;
using CityTwin.UI;
using extOSC.Core;

namespace CityTwin.Input
{
    /// <summary>Per-instance OSC tile tracking. One receiver per instance; port from GameInstanceRoot. No statics.</summary>
    [RequireComponent(typeof(OSCReceiver))]
    public class TileTrackingManager : MonoBehaviour, ITileSource
    {
        [Tooltip("Optional: map TUIO classId to building id. Empty = use classId as string.")]
        [SerializeField] private List<ClassIdToBuilding> classIdToBuilding = new List<ClassIdToBuilding>();

        [Tooltip("If true, Local Port is set from Game Instance Root's Listen Port each OnEnable. If false, the OSC Receiver's Inspector port is left as-is.")]
        [SerializeField] private bool useInstancePort = true;

        [Header("Local testing")]
        [Tooltip("When enabled, any TUIO classId not in the mapping list is treated as this building (e.g. TUIO simulator classId 33 → garden). Disable for production.")]
        [SerializeField] private bool useLocalTestingFallback = false;
        [Tooltip("Building id to use when Use Local Testing Fallback is on and classId has no mapping (e.g. garden, park).")]
        [SerializeField] private string localTestingBuildingId = "garden";

        [Header("Presence debounce")]
        [Tooltip("Keep a marker for this long after it stops being reported, to ride out tracking flicker (e.g. TUIO dropping a frame). Should comfortably exceed the TUIO frame interval; 0 = remove almost immediately.")]
        [Range(0f, 2f)]
        [SerializeField] private float removeGraceSeconds = 0.25f;
        [Tooltip("Require a new marker to be seen continuously for this long before it is placed, to reject spurious one-frame detections. 0 = place immediately.")]
        [Range(0f, 1f)]
        [SerializeField] private float addConfirmSeconds = 0f;

        public event Action<TilePose> OnTileUpdated;
        public event Action<string> OnTileRemoved;

        [SerializeField] private GameInstanceRoot _instanceRoot;
        [SerializeField] private OSCReceiver _receiver;
        private IOSCBind _bind;
        private readonly Dictionary<int, string> _sessionToTileId = new Dictionary<int, string>();
        private readonly Dictionary<int, LiveSession> _live = new Dictionary<int, LiveSession>();
        private readonly List<int> _sessionScratch = new List<int>();
        private readonly Dictionary<int, float> _lastSeen = new Dictionary<int, float>();
        private readonly Dictionary<int, PendingAdd> _pendingAdd = new Dictionary<int, PendingAdd>();
        private readonly List<int> _promoteScratch = new List<int>();
        private readonly List<int> _dropScratch = new List<int>();
        private int _nextLocalId;

        // Falls back to the defaults for a sensor that has no saved settings yet.
        private readonly SensorFilterSettings _defaultFilter = new SensorFilterSettings();
        private ProjectionOutputRig _rig;
        private BuildingSpawner _spawner;
        private SensorCalibration _sensor;
        private bool _calibrationResolved;
        private bool _suppressGameInput;

        private struct PendingAdd { public float FirstSeen; public Vector2 Pos; public string BuildingId; public int SourceId; }

        private sealed class LiveSession
        {
            public readonly SensorInputFilter Filter = new SensorInputFilter();
            public Vector2 Raw;
            public int ClassId;
            public string BuildingId;
            public int SourceId;
            public float FirstSeen;
        }

        [Serializable]
        public class ClassIdToBuilding
        {
            public int classId;
            public string buildingId;
        }

        private void Awake()
        {
            _instanceRoot = GetComponent<GameInstanceRoot>();
            _receiver = GetComponent<OSCReceiver>();
            _spawner = GetComponentInChildren<BuildingSpawner>(true);
        }

        /// <summary>Sensor / game instance this tracker feeds (0-3).</summary>
        public int SensorIndex => _instanceRoot != null ? _instanceRoot.InstanceId : 0;
        public int ListenPort => _instanceRoot != null ? _instanceRoot.ListenPort : 0;
        public BuildingSpawner Spawner => _spawner;

        /// <summary>
        /// While true (F9 camera tab) tiles on the table are only observed, not played: calibration tiles must not turn
        /// into buildings. Removals still go through. Turning it off applies every tile still on the table to the game.
        /// </summary>
        public bool SuppressGameInput
        {
            get => _suppressGameInput;
            set
            {
                if (_suppressGameInput == value) return;
                _suppressGameInput = value;
                if (value) _pendingAdd.Clear();
                else ReplayLiveSessions();
            }
        }

        /// <summary>Markers the camera reports right now, unfiltered, for calibration.</summary>
        public void GetLiveMarkers(List<SensorMarker> into)
        {
            into.Clear();
            foreach (var kv in _live)
                into.Add(new SensorMarker
                {
                    SessionId = kv.Key,
                    Raw = kv.Value.Raw,
                    FirstSeen = kv.Value.FirstSeen,
                    ClassId = kv.Value.ClassId,
                    BuildingId = kv.Value.BuildingId,
                });
        }

        /// <summary>Pick up a changed mapping or filter, restarting every tile's filter in the new coordinate space.</summary>
        public void RefreshCalibration()
        {
            _calibrationResolved = false;
            ResolveCalibration();
            if (!_suppressGameInput) ReplayLiveSessions();
        }

        private void ResolveCalibration()
        {
            if (_calibrationResolved) return;
            _calibrationResolved = true;
            _sensor = null;
            if (_rig != null)
            {
                var store = _rig.Store;
                if (store.Current == null) store.Load();
                _sensor = store.Current.FindSensor(SensorIndex);
            }
            foreach (var live in _live.Values)
            {
                live.Filter.Reset();
                live.Filter.Settings = CurrentFilterSettings;
            }
        }

        // Without a rig (scenes with no calibration) tiles use the raw camera positions.
        private SensorFilterSettings CurrentFilterSettings =>
            _rig == null ? null : _sensor != null ? _sensor.filter : _defaultFilter;

        // The table uv of a saved mapping only means something while the table renders through the rig.
        private bool IsMapped =>
            _sensor != null && _sensor.IsFitted && ProjectionOutputRig.Active == _rig && _rig != null
            && _spawner != null && _spawner.ContentRoot != null;

        private Vector2 ToFilterSpace(Vector2 raw) => IsMapped ? _sensor.mapping.Map(raw) : raw;

        private Vector2 FilterSpaceToPose(Vector2 position)
        {
            if (!IsMapped || !_rig.TryTableUvToWorld(position, _spawner.ContentRoot, out Vector3 world)) return position;
            return _spawner.LocalToTuioPosition(_spawner.WorldToContentLocal(world));
        }

        private void ReplayLiveSessions()
        {
            ResolveCalibration();
            float now = Time.unscaledTime;
            CopySessionIds();
            foreach (int sessionId in _sessionScratch)
            {
                var live = _live[sessionId];
                live.Filter.Reset();
                Emit(sessionId, live, live.Filter.Push(ToFilterSpace(live.Raw), now));
            }
        }

        private void CopySessionIds()
        {
            _sessionScratch.Clear();
            _sessionScratch.AddRange(_live.Keys);
        }

        private void OnEnable()
        {
            if (useInstancePort)
            {
                int port = _instanceRoot != null ? _instanceRoot.ListenPort : 3333;
                _receiver.LocalPort = Mathf.Clamp(port, 1024, 65535);
            }
            _bind = _receiver.Bind("/tuio/2Dobj", OnTuio2DObj);

            _rig = FindFirstObjectByType<ProjectionOutputRig>(FindObjectsInactive.Include);
            if (_rig != null) _rig.SensorCalibrationChanged += RefreshCalibration;
            _calibrationResolved = false;
        }

        private void OnDisable()
        {
            if (_receiver != null && _bind != null)
                _receiver.Unbind(_bind);
            if (_rig != null) _rig.SensorCalibrationChanged -= RefreshCalibration;
        }

        private void OnTuio2DObj(OSCMessage msg)
        {
            if (msg.Values.Count < 1 || msg.Values[0].Type != OSCValueType.String) return;
            string cmd = msg.Values[0].StringValue;
            switch (cmd)
            {
                case "set":
                    HandleSet(msg);
                    break;
                case "alive":
                    HandleAlive(msg);
                    break;
            }
        }

        private void HandleSet(OSCMessage msg)
        {
            // TUIO 2Dobj "set" = "set", sessionId(int), classId(int), x(float), y(float), [angle...].
            // Guard against truncated or wrongly-typed packets - this is untrusted network input and
            // indexing Values[1..4] blindly would throw on a malformed message.
            if (msg.Values.Count < 5) return;
            if (msg.Values[1].Type != OSCValueType.Int || msg.Values[2].Type != OSCValueType.Int) return;

            int sessionId = msg.Values[1].IntValue;
            int classId = msg.Values[2].IntValue;
            float x = msg.Values[3].FloatValue;
            float y = msg.Values[4].FloatValue;

            string buildingId = ResolveBuildingId(classId);
            int sourceId = _instanceRoot != null ? _instanceRoot.InstanceId : 0;

            ResolveCalibration();
            if (!_live.TryGetValue(sessionId, out var live))
            {
                live = new LiveSession { FirstSeen = Time.unscaledTime };
                live.Filter.Settings = CurrentFilterSettings;
                _live[sessionId] = live;
            }
            live.Raw = new Vector2(x, y);
            live.ClassId = classId;
            live.BuildingId = buildingId;
            live.SourceId = sourceId;

            // Mark presence; this also cancels any pending grace removal for an already-placed marker.
            _lastSeen[sessionId] = Time.time;

            if (_suppressGameInput) return;
            Emit(sessionId, live, live.Filter.Push(ToFilterSpace(live.Raw), Time.unscaledTime));
        }

        private void Emit(int sessionId, LiveSession live, Vector2 filtered)
        {
            Vector2 pos = FilterSpaceToPose(filtered);
            if (_sessionToTileId.TryGetValue(sessionId, out string tileId))
            {
                OnTileUpdated?.Invoke(new TilePose(pos, 0f, live.BuildingId, live.SourceId, tileId));
            }
            else if (addConfirmSeconds <= 0f)
            {
                tileId = NewTileId();
                _sessionToTileId[sessionId] = tileId;
                Debug.Log($"[TileTracking] Game {SensorIndex + 1}: tile id {live.ClassId} placed as '{live.BuildingId}'.", this);
                OnTileUpdated?.Invoke(new TilePose(pos, 0f, live.BuildingId, live.SourceId, tileId));
            }
            else
            {
                // Hold as pending; placed in Update once it has been seen for addConfirmSeconds.
                if (!_pendingAdd.TryGetValue(sessionId, out var pend))
                    pend = new PendingAdd { FirstSeen = Time.time };
                pend.Pos = pos;
                pend.BuildingId = live.BuildingId;
                pend.SourceId = live.SourceId;
                _pendingAdd[sessionId] = pend;
            }
        }

        private void HandleAlive(OSCMessage msg)
        {
            // "alive" lists every currently-present session each frame. Refresh their last-seen time;
            // the grace-based removal and add-confirmation are handled in Update().
            float now = Time.time;
            for (int i = 1; i < msg.Values.Count; i++)
            {
                if (msg.Values[i].Type == OSCValueType.Int)
                    _lastSeen[msg.Values[i].IntValue] = now;
            }
        }

        private void Update()
        {
            float now = Time.time;

            // Finish glides on the frames TUIO sends nothing, or a tile would stop short of where it was put.
            if (!_suppressGameInput && _live.Count > 0)
            {
                CopySessionIds();
                foreach (int sessionId in _sessionScratch)
                {
                    if (!_live.TryGetValue(sessionId, out var live) || !_sessionToTileId.ContainsKey(sessionId)) continue;
                    if (live.Filter.Advance(Time.unscaledTime)) Emit(sessionId, live, live.Filter.Output);
                }
            }

            // Promote pending adds once they have persisted long enough; drop ghosts that vanished first.
            if (_pendingAdd.Count > 0)
            {
                _promoteScratch.Clear();
                _dropScratch.Clear();
                foreach (var kv in _pendingAdd)
                {
                    float seen = _lastSeen.TryGetValue(kv.Key, out var ls) ? ls : 0f;
                    if (now - seen > removeGraceSeconds) _dropScratch.Add(kv.Key);
                    else if (now - kv.Value.FirstSeen >= addConfirmSeconds) _promoteScratch.Add(kv.Key);
                }
                for (int i = 0; i < _dropScratch.Count; i++)
                {
                    int sid = _dropScratch[i];
                    _pendingAdd.Remove(sid);
                    _live.Remove(sid);
                    _lastSeen.Remove(sid);
                }
                for (int i = 0; i < _promoteScratch.Count; i++)
                {
                    int sid = _promoteScratch[i];
                    var pend = _pendingAdd[sid];
                    _pendingAdd.Remove(sid);
                    string tileId = NewTileId();
                    _sessionToTileId[sid] = tileId;
                    OnTileUpdated?.Invoke(new TilePose(pend.Pos, 0f, pend.BuildingId, pend.SourceId, tileId));
                }
            }

            // Grace removal: drop placed markers whose presence lapsed beyond the grace window.
            if (_sessionToTileId.Count > 0)
            {
                _dropScratch.Clear();
                foreach (var kv in _sessionToTileId)
                {
                    float seen = _lastSeen.TryGetValue(kv.Key, out var ls) ? ls : 0f;
                    if (now - seen > removeGraceSeconds) _dropScratch.Add(kv.Key);
                }
                for (int i = 0; i < _dropScratch.Count; i++)
                {
                    int sid = _dropScratch[i];
                    string tileId = _sessionToTileId[sid];
                    _sessionToTileId.Remove(sid);
                    _live.Remove(sid);
                    _lastSeen.Remove(sid);
                    OnTileRemoved?.Invoke(tileId);
                }
            }

            // Sessions never played (seen during calibration) still have to disappear when lifted.
            if (_live.Count > 0)
            {
                CopySessionIds();
                foreach (int sessionId in _sessionScratch)
                {
                    float seen = _lastSeen.TryGetValue(sessionId, out var ls) ? ls : 0f;
                    if (now - seen <= removeGraceSeconds) continue;
                    _live.Remove(sessionId);
                    _lastSeen.Remove(sessionId);
                }
            }
        }

        private string NewTileId() => $"osc_{(_instanceRoot != null ? _instanceRoot.InstanceId : 0)}_{_nextLocalId++}";

        /// <summary>Forget all tracked TUIO sessions. Used by per-instance restart flows so
        /// physical tiles still on the table are treated as new placements after restart.</summary>
        public void ClearSessions()
        {
            _sessionToTileId.Clear();
            _live.Clear();
            _lastSeen.Clear();
            _pendingAdd.Clear();
        }

        private string ResolveBuildingId(int classId)
        {
            foreach (var m in classIdToBuilding)
                if (m.classId == classId) return m.buildingId ?? classId.ToString();
            if (useLocalTestingFallback && !string.IsNullOrEmpty(localTestingBuildingId))
                return localTestingBuildingId;
            return classId.ToString();
        }
    }
}
