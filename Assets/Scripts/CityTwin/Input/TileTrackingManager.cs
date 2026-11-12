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
        // Sessions in the most recent TUIO "alive" message.
        private readonly HashSet<int> _lastAlive = new HashSet<int>();
        // A new session this close to a vanished one with the same marker id is the same physical tile.
        private const float HandoverRawDistance = 0.05f;
        private int _handoverCount;
        private int _nextLocalId;
        private int _oscMessageCount;

        /// <summary>TUIO objects currently tracked (live sessions). Debug readout.</summary>
        public int LiveInputCount => _live.Count;
        /// <summary>Total /tuio/2Dobj OSC messages received since start. Climbs whenever TUIO data arrives at all (debug readout).</summary>
        public int OscMessageCount => _oscMessageCount;
        /// <summary>Most recent raw TUIO position (0..1, as sent by the tracker, before any mapping). Debug readout.</summary>
        public Vector2 LastRawPosition { get; private set; }
        /// <summary>The camera mapping used before any camera fit: the camera is assumed to see exactly projector 0's frame. Null without the rig.</summary>
        public SensorMapping DefaultCameraMapping => RigReady ? DefaultMapping() : null;

        // Falls back to the defaults for a sensor that has no saved settings yet.
        private readonly SensorFilterSettings _defaultFilter = new SensorFilterSettings();
        private ProjectionOutputRig _rig;
        private BuildingSpawner _spawner;
        private SensorCalibration _sensor;
        private bool _calibrationResolved;
        // Used until the camera is fitted: assumes the camera sees exactly projector 0's frame.
        private SensorMapping _defaultMapping;
        private Vector2[] _defaultMappingTable, _defaultMappingProjector;
        private bool _defaultMappingLogged;
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
            _defaultMapping = null;
            _defaultMappingTable = _defaultMappingProjector = null;
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

        // The table uv of a mapping only means something while the table renders through the rig.
        private bool RigReady =>
            ProjectionOutputRig.Active == _rig && _rig != null && _spawner != null && _spawner.ContentRoot != null;

        private bool HasFittedSensor => _sensor != null && _sensor.IsFitted;

        private bool IsMapped => RigReady && ActiveMapping != null;

        /// <summary>The fitted camera mapping, else the projector-frame default, else null.</summary>
        private SensorMapping ActiveMapping => HasFittedSensor ? _sensor.mapping : DefaultMapping();

        private Vector2 ToFilterSpace(Vector2 raw) => IsMapped ? ActiveMapping.Map(raw) : raw;

        /// <summary>
        /// Before any camera fit, assume the camera sees exactly projector 0's frame: raw (0,0) = the frame's top-left,
        /// raw (1,1) = its bottom-right (raw y is down, projector uv y is up). Rebuilt whenever the projector geometry
        /// changes (auto fit, projector editor), so pins follow the frame. The camera calibration replaces it.
        /// </summary>
        private SensorMapping DefaultMapping()
        {
            if (_rig == null || _rig.Projectors == null || _rig.Projectors.Count == 0) return null;
            var cal = _rig.Projectors[0];
            if (cal == null || cal.tablePoints == null || cal.projectorPoints == null) return null;
            if (_defaultMapping != null && SamePoints(cal.tablePoints, _defaultMappingTable)
                && SamePoints(cal.projectorPoints, _defaultMappingProjector))
                return _defaultMapping;

            bool rebuilt = _defaultMapping != null;
            _defaultMapping = null;
            _defaultMappingTable = (Vector2[])cal.tablePoints.Clone();
            _defaultMappingProjector = (Vector2[])cal.projectorPoints.Clone();
            if (!cal.TryGetTableToProjector(out Homography tableToProjector) || !tableToProjector.TryInverse(out Homography projectorToTable))
                return null;

            // raw (x, y down) -> projector uv (x, 1 - y), then projector uv -> table uv.
            var rawToProjector = new Homography { m00 = 1, m11 = -1, m12 = 1, m22 = 1 };
            _defaultMapping = new SensorMapping { homography = (projectorToTable * rawToProjector).Normalized(), k1 = 0 };
            if (!_defaultMappingLogged)
            {
                _defaultMappingLogged = true;
                Debug.Log("[TileTracking] No camera fit: using the projector frame as the default camera mapping", this);
            }
            if (rebuilt)
                foreach (var live in _live.Values) live.Filter.Reset(); // old filter state is in the previous frame's space
            return _defaultMapping;
        }

        private static bool SamePoints(Vector2[] a, Vector2[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i]) return false;
            return true;
        }

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
            _oscMessageCount++; // any /tuio/2Dobj message (set/alive/fseq) = TUIO is reaching us
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
                int previous = FindHandover(sessionId, classId, new Vector2(x, y));
                if (previous >= 0)
                {
                    live = Rekey(previous, sessionId);
                }
                else
                {
                    live = new LiveSession { FirstSeen = Time.unscaledTime };
                    live.Filter.Settings = CurrentFilterSettings;
                    _live[sessionId] = live;
                }
            }
            live.Raw = new Vector2(x, y);
            LastRawPosition = live.Raw;
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
            _lastAlive.Clear();
            for (int i = 1; i < msg.Values.Count; i++)
            {
                if (msg.Values[i].Type != OSCValueType.Int) continue;
                _lastSeen[msg.Values[i].IntValue] = now;
                _lastAlive.Add(msg.Values[i].IntValue);
            }
        }

        /// <summary>
        /// reacTIVision sometimes gives a tile that never left the table a new session id (seen every frame on the
        /// pilot camera). Such a session is matched to a tracked one with the same marker id, close by, that the
        /// latest "alive" no longer lists, so the game keeps one tile instead of placing a new one per frame.
        /// </summary>
        private int FindHandover(int sessionId, int classId, Vector2 raw)
        {
            int best = -1;
            float bestDistance = HandoverRawDistance;
            foreach (var kv in _live)
            {
                if (kv.Key == sessionId || kv.Value.ClassId != classId || _lastAlive.Contains(kv.Key)) continue;
                float d = Vector2.Distance(kv.Value.Raw, raw);
                if (d <= bestDistance) { bestDistance = d; best = kv.Key; }
            }
            return best;
        }

        private LiveSession Rekey(int from, int to)
        {
            var live = _live[from];
            _live.Remove(from);
            _live[to] = live;
            if (_sessionToTileId.TryGetValue(from, out string tileId))
            {
                _sessionToTileId.Remove(from);
                _sessionToTileId[to] = tileId;
            }
            if (_pendingAdd.TryGetValue(from, out var pend))
            {
                _pendingAdd.Remove(from);
                _pendingAdd[to] = pend;
            }
            _lastSeen.Remove(from);
            if (_handoverCount++ == 0)
                Debug.Log($"[TileTracking] Game {SensorIndex + 1}: the tracker re-issued a session id for a tile on the table; following it as the same tile.", this);
            return live;
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
