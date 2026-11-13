using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;
using CityTwin.Input;
using CityTwin.UI;

namespace CityTwin.Calibration
{
    /// <summary>
    /// The logic behind the camera tab of F9 calibration: guided per-sensor capture, a verify view and the jitter filter.
    /// Output (projector) and input (camera) calibration are independent data: targets are shown through the current
    /// projector warp. Moving a projector afterwards keeps the fits of the play areas it lights but marks them to be
    /// checked, since the picture no longer lands where the targets were shown; moving a camera needs a new capture.
    /// The selected camera is also the menu's selected play area.
    /// Tiles reach the game only while the menu is closed (<see cref="Hold"/> / <see cref="Release"/>), and an
    /// unfinished capture never touches the saved mapping.
    /// </summary>
    internal sealed class SensorCalibrationTab
    {
        private const int TargetCount = 9;
        private const int CandidateGrid = 24;
        private const float EdgeMargin = 0.05f;
        private const float RadiusStepCm = 0.1f;
        private const float RadiusStepLargeCm = 0.5f;
        private const float SmoothingStep = 0.01f;
        private const float SmoothingStepLarge = 0.05f;
        private const float AutoRadiusFactor = 1.25f;
        private const float AutoRadiusMarginCm = 0.05f;
        private const float MaxAutoJitterCm = 3f;
        private const float PoorFitMaxCm = 3f;
        // A fit this far off on average comes from a bad capture (e.g. one tile stored for two targets): never save it.
        private const float RejectFitRmsCm = 5f;

        private readonly ProjectionOutputRig _rig;
        private readonly Func<Key, string, bool> _confirm;
        private readonly Action<string> _setStatus;
        private readonly List<TileTrackingManager> _sensors = new List<TileTrackingManager>();
        private readonly List<PlayArea> _areas = new List<PlayArea>();
        private readonly List<SensorMarker> _markers = new List<SensorMarker>();
        private readonly SensorJitterMeter _jitter = new SensorJitterMeter();
        private readonly Dictionary<int, SensorInputFilter> _previewFilters = new Dictionary<int, SensorInputFilter>();
        private readonly SensorFilterSettings _unsavedDefaults = new SensorFilterSettings();
        private SensorCalibrationSession _session;
        private bool _capturing;
        private int _selected;
        private int _jitterSession = -1;

        public SensorCalibrationTab(ProjectionOutputRig rig, Func<Key, string, bool> confirm, Action<string> setStatus)
        {
            _rig = rig;
            _confirm = confirm;
            _setStatus = setStatus;
        }

        public bool IsDirty { get; private set; }
        public IReadOnlyList<TileTrackingManager> Sensors => _sensors;
        public IReadOnlyList<PlayArea> Areas => _areas;
        public PlayArea SelectedArea => _selected < _areas.Count ? _areas[_selected] : null;
        public bool HasCaptureInProgress => IsCapturing && _session.CapturedCount > 0;
        public int SelectedIndex => _selected;
        /// <summary>Markers the camera reports right now.</summary>
        public int TileCount => _markers.Count;
        public bool IsCapturing => _capturing && _session != null;
        public SensorCalibrationSession Session => _session;
        public IReadOnlyList<SensorMarker> Markers => _markers;
        public bool HasJitterReading => _jitter.SampleCount >= SensorJitterMeter.MinSamples;
        public float JitterCm => _jitter.PeakCm;

        private TableCalibrationFile File => _rig.Store.Current;
        private TileTrackingManager Sensor => _selected < _sensors.Count ? _sensors[_selected] : null;

        /// <summary>This camera's saved fit and filter, or null before anything was saved for it.</summary>
        public SensorCalibration Saved
        {
            get
            {
                var sensor = Sensor;
                return sensor != null ? File?.FindSensor(sensor.SensorIndex) : null;
            }
        }

        public bool IsFitted => Saved != null && Saved.IsFitted;
        public SensorFilterSettings Filter => Saved != null ? Saved.filter : _unsavedDefaults;

        // ---------- menu open / close ----------

        /// <summary>The menu opened: tiles on the table are observed only, not played.</summary>
        public void Hold()
        {
            _sensors.Clear();
            _sensors.AddRange(UnityEngine.Object.FindObjectsByType<TileTrackingManager>(FindObjectsSortMode.None));
            _sensors.Sort((a, b) => a.SensorIndex.CompareTo(b.SensorIndex));
            _selected = Mathf.Clamp(_selected, 0, Mathf.Max(0, _sensors.Count - 1));
            foreach (var s in _sensors) s.SuppressGameInput = true;
            _areas.Clear();
            foreach (var s in _sensors)
            {
                Vector2 center = AreaCenter(s);
                _areas.Add(new PlayArea { Sensor = s, Center = center, Side = PlayAreaLayout.SideOf(center) });
            }
            IsDirty = false;
        }

        /// <summary>The menu closed: the game gets the table back, and an unfinished capture is dropped.</summary>
        public void Release()
        {
            _session = null;
            _capturing = false;
            foreach (var s in _sensors)
                if (s != null) s.SuppressGameInput = false;
        }

        public void EnterTab()
        {
            if (_session == null) ResetModeForSensor();
        }

        public void MarkSaved() => IsDirty = false;

        /// <summary>The saved calibration was reloaded or restored: drop in-progress work.</summary>
        public void OnReloaded(bool tabActive)
        {
            IsDirty = false;
            _session = null;
            _capturing = false;
            _previewFilters.Clear();
            if (tabActive) ResetModeForSensor();
        }

        /// <summary>Make this the selected play area. Without <paramref name="startCapture"/> an uncalibrated camera waits until its tab opens.</summary>
        public void SelectArea(int index, bool startCapture)
        {
            if (_sensors.Count == 0) return;
            _selected = (index + _sensors.Count) % _sensors.Count;
            ResetModeForSensor(startCapture);
        }

        private void ResetModeForSensor(bool startCapture = true)
        {
            _jitter.Clear();
            _jitterSession = -1;
            _previewFilters.Clear();
            _markers.Clear();
            _session = null;
            _capturing = false;
            if (startCapture && Sensor != null && !IsFitted) StartCapture();
        }

        // ---------- play areas ----------

        public int ProjectorOf(PlayArea area) => PlayAreaLayout.ProjectorFor(area.Center, _rig.Projectors);

        /// <summary>The first play area this projector lights, or -1.</summary>
        public int FirstAreaOn(int projector)
        {
            for (int i = 0; i < _areas.Count; i++)
                if (ProjectorOf(_areas[i]) == projector) return i;
            return -1;
        }

        /// <summary>False when some projector can't be reached by stepping through the play areas.</summary>
        public bool AreasReachProjectors(int projectorCount)
        {
            for (int i = 0; i < projectorCount; i++)
                if (FirstAreaOn(i) < 0) return false;
            return true;
        }

        /// <summary>Says which play areas share this projector, or null when it lights fewer than two.</summary>
        public string SharedText(int projector)
        {
            var names = new List<string>();
            foreach (var area in _areas)
                if (ProjectorOf(area) == projector) names.Add(area.Name);
            if (names.Count < 2) return null;
            return $"Shared by {string.Join(" and ", names)}. Moving it moves {(names.Count == 2 ? "both" : "all of them")}.";
        }

        public bool NeedsCheck(TileTrackingManager sensor)
        {
            var saved = File?.FindSensor(sensor.SensorIndex);
            return saved != null && saved.IsFitted && saved.projectorMovedSinceFit;
        }

        /// <summary>Mark the fits of every play area on these projectors to be checked. Returns their names, or an empty string.</summary>
        public string FlagProjectorsMoved(HashSet<int> projectors)
        {
            var names = new List<string>();
            foreach (var area in _areas)
            {
                if (!projectors.Contains(ProjectorOf(area))) continue;
                var saved = File?.FindSensor(area.Sensor.SensorIndex);
                if (saved == null || !saved.IsFitted) continue;
                saved.projectorMovedSinceFit = true;
                names.Add(area.Name);
            }
            return string.Join(", ", names);
        }

        public void MarkStillAligned()
        {
            if (Saved == null) return;
            Saved.projectorMovedSinceFit = false;
            IsDirty = true;
        }

        // ---------- actions (menu buttons and keyboard) ----------

        public void StartCapture()
        {
            var sensor = Sensor;
            if (sensor == null) return;
            var targets = BuildTargets(sensor);
            if (targets.Count < 4)
            {
                _session = null;
                _capturing = false;
                _setStatus("No targets can be shown for this camera: calibrate the projector first");
                return;
            }
            _session = new SensorCalibrationSession(targets, Saved?.mapping);
            _capturing = true;
        }

        public void RequestRecapture()
        {
            if (_confirm(Key.R, "Press again to capture this camera from the start")) StartCapture();
        }

        public void ShowVerify() => _capturing = false;

        public void ShowCapture()
        {
            if (_session != null) _capturing = true;
            else StartCapture();
        }

        public void RequestClearFit()
        {
            if (!_confirm(Key.C, "Press again to clear this camera's fit (projector-frame default until recaptured)")) return;
            var sensor = Sensor;
            if (sensor == null) return;
            Saved?.ClearFit();
            IsDirty = true;
            sensor.RefreshCalibration();
            StartCapture();
        }

        public void CaptureNow()
        {
            if (IsCapturing && !_session.CaptureNow()) _setStatus("No tile seen yet");
        }

        public void BackTarget()
        {
            if (IsCapturing && !_session.Back()) _setStatus("Already on the first target");
        }

        public void NextTarget()
        {
            if (IsCapturing && !_session.Next()) _setStatus("Capture this target first");
        }

        /// <summary>A click near a target (main window pixels) makes it the current one: retake it or take a skipped one.</summary>
        public bool TryPickTarget(Vector2 window, float radiusPx)
        {
            if (!IsCapturing || _session.IsClearingTable) return false;
            int best = -1;
            float bestDistance = radiusPx;
            for (int i = 0; i < _session.Targets.Count; i++)
            {
                float d = Vector2.Distance(_rig.TableUvToWindow(_session.Targets[i]), window);
                if (d <= bestDistance) { bestDistance = d; best = i; }
            }
            return best >= 0 && best != _session.CurrentIndex && _session.GoTo(best);
        }

        public void SkipTarget()
        {
            if (IsCapturing) _session.Skip();
        }

        public void SetFilterEnabled(bool on) => EditFilter(s => s.enabled = on);
        public void SetRadius(float cm) => EditFilter(s => s.radiusCm = cm);
        public void SetSmoothing(float seconds) => EditFilter(s => s.smoothingSeconds = seconds);

        public void SetRadiusFromJitter()
        {
            if (!HasJitterReading)
            {
                _setStatus("Put a tile on the table and leave it still for a moment first");
                return;
            }
            float peak = _jitter.PeakCm;
            if (peak > MaxAutoJitterCm)
            {
                _setStatus("The tile looks like it's moving: leave it still and try again");
                return;
            }
            SetRadius(peak * AutoRadiusFactor + AutoRadiusMarginCm);
            _setStatus($"Radius set from measured jitter ({peak:0.00} cm)");
        }

        private void EditFilter(Action<SensorFilterSettings> edit)
        {
            var sensor = Sensor;
            if (sensor == null) return;
            var settings = File.GetOrAddSensor(sensor.SensorIndex).filter;
            edit(settings);
            settings.Sanitize();
            IsDirty = true;
            sensor.RefreshCalibration();
        }

        /// <summary>Spread targets over the part of this game's area that is on the table art and on a projector, away from the edges the camera sees badly.</summary>
        private List<Vector2> BuildTargets(TileTrackingManager sensor)
        {
            var candidates = AreaPoints(sensor);
            candidates.RemoveAll(uv => !_rig.TryTableUvToProjector(uv, out _, out _));
            return SensorTargetLayout.PickSpread(candidates, TargetCount);
        }

        private Vector2 AreaCenter(TileTrackingManager sensor)
        {
            var points = AreaPoints(sensor);
            if (points.Count == 0) return new Vector2(0.5f, 0.5f);
            Vector2 sum = Vector2.zero;
            foreach (var p in points) sum += p;
            return sum / points.Count;
        }

        // Grid points (table uv) on this game's table art.
        private List<Vector2> AreaPoints(TileTrackingManager sensor)
        {
            var points = new List<Vector2>();
            var rect = sensor.Spawner != null ? sensor.Spawner.ContentRoot : null;
            if (rect == null) return points;
            var bounds = sensor.GetComponentInChildren<TableBounds>(true);
            if (bounds == null) bounds = sensor.GetComponentInParent<TableBounds>(true);

            var corners = new Vector3[4];
            rect.GetWorldCorners(corners);
            Vector3 right = corners[3] - corners[0], up = corners[1] - corners[0];
            Vector3 marginX = right * EdgeMargin, marginY = up * EdgeMargin;

            for (int y = 1; y < CandidateGrid; y++)
            {
                for (int x = 1; x < CandidateGrid; x++)
                {
                    Vector3 p = corners[0] + right * ((float)x / CandidateGrid) + up * ((float)y / CandidateGrid);
                    if (bounds != null && !(bounds.ContainsWorld(p) && bounds.ContainsWorld(p + marginX) && bounds.ContainsWorld(p - marginX)
                                            && bounds.ContainsWorld(p + marginY) && bounds.ContainsWorld(p - marginY))) continue;
                    if (_rig.TryWorldToTableUv(p, out Vector2 uv)) points.Add(uv);
                }
            }
            return points;
        }

        // ---------- per-frame ----------

        public void HandleKeys(Keyboard kb)
        {
            bool shift = kb.shiftKey.isPressed;
            if (kb.rKey.wasPressedThisFrame) RequestRecapture();
            if (kb.vKey.wasPressedThisFrame)
            {
                if (_capturing) ShowVerify();
                else ShowCapture();
            }
            if (kb.cKey.wasPressedThisFrame) RequestClearFit();
            if (kb.xKey.wasPressedThisFrame) SkipTarget();
            if (kb.backspaceKey.wasPressedThisFrame) BackTarget();
            if (kb.spaceKey.wasPressedThisFrame) CaptureNow();
            if (kb.enterKey.wasPressedThisFrame || kb.numpadEnterKey.wasPressedThisFrame || kb.nKey.wasPressedThisFrame) NextTarget();

            if (kb.leftBracketKey.wasPressedThisFrame) SetRadius(Filter.radiusCm - (shift ? RadiusStepLargeCm : RadiusStepCm));
            if (kb.rightBracketKey.wasPressedThisFrame) SetRadius(Filter.radiusCm + (shift ? RadiusStepLargeCm : RadiusStepCm));
            if (kb.commaKey.wasPressedThisFrame) SetSmoothing(Filter.smoothingSeconds - (shift ? SmoothingStepLarge : SmoothingStep));
            if (kb.periodKey.wasPressedThisFrame) SetSmoothing(Filter.smoothingSeconds + (shift ? SmoothingStepLarge : SmoothingStep));
            if (kb.jKey.wasPressedThisFrame) SetFilterEnabled(!Filter.enabled);
            if (kb.aKey.wasPressedThisFrame) SetRadiusFromJitter();
        }

        public void Tick()
        {
            var sensor = Sensor;
            if (sensor == null) return;
            sensor.GetLiveMarkers(_markers);
            MeasureJitter(sensor);
            if (_capturing && _session != null)
            {
                _session.Feed(_markers, Time.unscaledTime);
                if (_session.IsComplete) FinishCapture(sensor);
            }
        }

        private void FinishCapture(TileTrackingManager sensor)
        {
            var session = _session;
            _session = null;
            _capturing = false;
            if (!session.HasFit)
            {
                _setStatus($"Only {session.CapturedCount} points captured, 4 are needed: start the capture again");
                return;
            }
            double rmsCm = session.Report.RmsError * SensorInputFilter.TableWidthCm;
            if (rmsCm > RejectFitRmsCm)
            {
                Debug.LogWarning($"[SensorCalibration] Fit rejected: average error {rmsCm:0.0} cm over {session.Report.PointCount} points (lens k1 {session.Report.K1:0.000}).");
                _setStatus($"Fit rejected: average error {rmsCm:0.0} cm. Nothing was changed: start the capture again");
                return;
            }
            var saved = File.GetOrAddSensor(sensor.SensorIndex);
            saved.mapping = session.Fit;
            saved.pointCount = session.Report.PointCount;
            saved.rmsError = session.Report.RmsError;
            saved.projectorMovedSinceFit = false;
            IsDirty = true;
            sensor.RefreshCalibration();
            _jitter.Clear();
            _previewFilters.Clear();

            double maxCm = session.Report.MaxError * SensorInputFilter.TableWidthCm;
            _setStatus(maxCm > PoorFitMaxCm
                ? $"Fit done but a point is {maxCm:0.0} cm off: check the tile positions and capture again"
                : "Fit done: slide a tile around, the pink square should sit under it. Then save");
        }

        private void MeasureJitter(TileTrackingManager sensor)
        {
            if (_markers.Count == 0) { _jitter.Clear(); _jitterSession = -1; return; }
            var newest = _markers[0];
            for (int i = 1; i < _markers.Count; i++)
                if (_markers[i].FirstSeen > newest.FirstSeen) newest = _markers[i];
            if (newest.SessionId != _jitterSession)
            {
                _jitter.Clear();
                _jitterSession = newest.SessionId;
            }
            var saved = File.FindSensor(sensor.SensorIndex);
            _jitter.Add(saved != null && saved.IsFitted ? saved.mapping.Map(newest.Raw) : newest.Raw, Time.unscaledTime);
        }

        // ---------- drawing ----------

        /// <summary>The table point staff are working on, if any.</summary>
        public bool TryGetFocus(out Vector2 tableUv)
        {
            tableUv = default;
            if (!IsCapturing || _session.IsComplete || _session.IsClearingTable) return false;
            tableUv = _session.Targets[_session.CurrentIndex];
            return true;
        }

        /// <summary>The projector that should carry the on-table text: the one showing the current target.</summary>
        public int HudProjector(int fallback)
        {
            return TryGetFocus(out Vector2 uv) && _rig.TryTableUvToProjector(uv, out int projector, out _) ? projector : fallback;
        }

        public Vector2? HudAvoidUv(int projector)
        {
            if (TryGetFocus(out Vector2 uv) && _rig.TryTableUvToProjector(uv, out int p, out Vector2 projectorUv) && p == projector)
                return projectorUv;
            return null;
        }

        /// <summary>Marks the selected play area, and any sharing its projector, so staff can see which one they are on.</summary>
        public void CollectAreaMarkers(int projector, List<ProjectorOverlay.Marker> into, bool includeShared)
        {
            into.Clear();
            if (_areas.Count < 2) return;
            var selected = SelectedArea;
            foreach (var area in _areas)
            {
                bool isSelected = area == selected;
                if (!isSelected && !includeShared) continue;
                if (!isSelected && ProjectorOf(area) != ProjectorOf(selected)) continue;
                if (!_rig.TryTableUvToProjector(area.Center, out int p, out Vector2 uv) || p != projector) continue;
                into.Add(new ProjectorOverlay.Marker { Uv = uv, Kind = isSelected ? ProjectorOverlay.MarkerKind.AreaSelected : ProjectorOverlay.MarkerKind.AreaShared });
            }
        }

        public void CollectMarkers(int projector, List<ProjectorOverlay.Marker> into)
        {
            into.Clear();
            if (IsCapturing)
            {
                // Where the camera sees each tile right now: the fit so far once there is one, else the projector-frame guess.
                var preview = _session.HasFit ? _session.Fit : Sensor != null ? Sensor.DefaultCameraMapping : null;
                AddLiveTiles(projector, preview, into);
                if (_session.IsClearingTable) return; // no targets until the table is empty
                for (int i = 0; i < _session.Targets.Count; i++)
                {
                    if (!_rig.TryTableUvToProjector(_session.Targets[i], out int p, out Vector2 uv) || p != projector) continue;
                    bool current = i == _session.CurrentIndex;
                    // A target revisited after its capture keeps its "calibrated" ring under the current cross.
                    if (current && !_session.AwaitingNext && _session.StateOf(i) == SensorTargetState.Captured)
                        into.Add(new ProjectorOverlay.Marker { Uv = uv, Kind = ProjectorOverlay.MarkerKind.TargetCaptured });
                    into.Add(new ProjectorOverlay.Marker
                    {
                        Uv = uv,
                        Kind = !current ? KindOf(_session.StateOf(i))
                            : _session.AwaitingNext ? ProjectorOverlay.MarkerKind.TargetStored : ProjectorOverlay.MarkerKind.TargetCurrent,
                        Progress = current ? _session.Progress : 0f,
                    });
                }
                return;
            }

            CollectAreaMarkers(projector, into, false);
            if (!IsFitted)
            {
                // Not calibrated yet: still show where each tile is seen, through the projector-frame guess.
                AddLiveTiles(projector, Sensor != null ? Sensor.DefaultCameraMapping : null, into);
                return;
            }
            var saved = Saved;
            float now = Time.unscaledTime;
            foreach (var m in _markers)
            {
                // Through the same filter the game will use, so radius and smoothing changes can be judged here.
                if (!_previewFilters.TryGetValue(m.SessionId, out var filter))
                    _previewFilters[m.SessionId] = filter = new SensorInputFilter();
                filter.Settings = saved.filter;
                filter.Push(saved.mapping.Map(m.Raw), now);
                filter.Advance(now);
                if (_rig.TryTableUvToProjector(filter.Output, out int p, out Vector2 uv) && p == projector)
                    into.Add(new ProjectorOverlay.Marker { Uv = uv, Kind = ProjectorOverlay.MarkerKind.LiveTile });
            }
        }

        private void AddLiveTiles(int projector, SensorMapping mapping, List<ProjectorOverlay.Marker> into)
        {
            if (mapping == null) return;
            foreach (var m in _markers)
                if (_rig.TryTableUvToProjector(mapping.Map(m.Raw), out int p, out Vector2 uv) && p == projector)
                    into.Add(new ProjectorOverlay.Marker { Uv = uv, Kind = ProjectorOverlay.MarkerKind.LiveTile });
        }

        private static ProjectorOverlay.MarkerKind KindOf(SensorTargetState state)
        {
            switch (state)
            {
                case SensorTargetState.Captured: return ProjectorOverlay.MarkerKind.TargetCaptured;
                case SensorTargetState.Skipped: return ProjectorOverlay.MarkerKind.TargetSkipped;
                default: return ProjectorOverlay.MarkerKind.TargetPending;
            }
        }

        public string FitSummary(TileTrackingManager sensor)
        {
            var saved = File?.FindSensor(sensor.SensorIndex);
            if (saved == null || !saved.IsFitted) return "not fitted";
            string fit = $"fitted, avg error {saved.rmsError * SensorInputFilter.TableWidthCm:0.0} cm";
            return saved.projectorMovedSinceFit ? fit + ", check camera" : fit;
        }

        /// <summary>One short line for the projected image, readable from the table.</summary>
        public string BannerText()
        {
            if (Sensor == null) return "CAMERA: no game running";
            if (!IsCapturing) return IsFitted ? "CAMERA: slide a tile, the pink square should sit under it" : "CAMERA: not fitted";
            if (_session.IsClearingTable) return "CAMERA: remove all tiles from the table";
            int shown = Mathf.Min(_session.CurrentIndex + 1, _session.Targets.Count);
            if (_session.AwaitingNext)
                return _session.AutoAdvanceIn >= 0f
                    ? $"CAMERA: stored ({shown}/{_session.Targets.Count})  -  next in {Mathf.CeilToInt(_session.AutoAdvanceIn)} s"
                    : $"CAMERA: stored ({shown}/{_session.Targets.Count})  -  press Next";
            if (_session.WaitingForMove) return $"CAMERA: move the tile to the yellow cross ({shown}/{_session.Targets.Count})";
            string hold = _session.HasMarker ? $"   hold still {_session.Progress * 100f:0}%" : "";
            return $"CAMERA: put a tile on the yellow cross ({shown}/{_session.Targets.Count}){hold}";
        }

        public string TileListText()
        {
            if (_markers.Count == 0) return "No tiles seen";
            var sb = new StringBuilder();
            for (int i = 0; i < _markers.Count; i++)
            {
                if (i > 0) sb.Append('\n');
                sb.Append($"Tile id {_markers[i].ClassId} = {_markers[i].BuildingId}");
            }
            return sb.ToString();
        }
    }
}
