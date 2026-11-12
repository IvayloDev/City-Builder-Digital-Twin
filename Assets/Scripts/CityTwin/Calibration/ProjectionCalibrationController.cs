using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

namespace CityTwin.Calibration
{
    /// <summary>
    /// Calibration mode (staff only; the audience never touches a keyboard). Ctrl+Shift+F4 opens the <see cref="CalibrationMenu"/> panel,
    /// whose tabs are 1 Projector (output warp), 2 Camera (input mapping and jitter filter, see <see cref="SensorCalibrationTab"/>)
    /// and Table setup. Keyboard shortcuts do the same as the buttons, for staff standing at the table.
    /// The menu works on one play area at a time (Tab steps to the next): the Camera tab is that area's own camera,
    /// the Projector tab is the projector lighting it, which it shares with the other area on that projector.
    /// While it is open nothing reaches the game: tiles are only observed and game UI and mouse placement are off.
    /// Closing it hands the table straight back. Projector editing goes through <see cref="ProjectionCalibrationEditor"/>;
    /// both tabs save to the same file through the rig/store.
    /// Avoids keys used by the game's debug tools (digits 1-4, F1, F2, backquote).
    /// </summary>
    [RequireComponent(typeof(ProjectionOutputRig))]
    public class ProjectionCalibrationController : MonoBehaviour
    {

        [Tooltip("CityTwin/CalibrationOverlay. Assigned here so the shader is included in builds.")]
        [SerializeField] private Shader overlayShader;

        [Tooltip("Test-pattern tint per projector (T toggles), so each projector's coverage and the overlap are visible. Matches the client deck colours.")]
        [SerializeField] private Color[] projectorTints =
        {
            new Color(1f, 0.35f, 1f), new Color(0.3f, 0.85f, 1f), new Color(1f, 0.9f, 0.3f), new Color(0.4f, 1f, 0.4f),
        };

        [Tooltip("Mouse: how close (pixels) a click must be to grab a handle.")]
        [SerializeField] private float mousePickRadiusPx = 40f;

        private const float RepeatDelay = 0.35f;
        private const float RepeatInterval = 1f / 30f;
        private const float NudgeHoldGap = 0.15f;
        private const float ConfirmWindow = 2.5f;
        private const float StatusSeconds = 4f;
        private const float SeamStep = 1f / 300f;      // 1 cm on the 300 cm table
        private const float SeamStepLarge = 5f / 300f; // 5 cm

        private static readonly TestPattern[] PatternCycle =
            { TestPattern.Overlay, TestPattern.PatternOnly, TestPattern.White, TestPattern.Black, TestPattern.Off };

        internal static IReadOnlyList<TestPattern> Patterns => PatternCycle;

        /// <summary>True while any calibration menu is open: game input must stay out of the way.</summary>
        public static bool AnyOpen { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetAnyOpen() => AnyOpen = false;

        private ProjectionOutputRig _rig;
        private ProjectionCalibrationEditor _editor;
        private readonly List<ProjectorOverlay> _overlays = new List<ProjectorOverlay>();
        private readonly List<Vector2> _handleScratch = new List<Vector2>();
        private Font _font;
        private bool _active;
        private TestPattern _pattern = TestPattern.Overlay;
        private bool _tint;
        private bool _hudVisible = true;
        private bool _hudFlipped;
        private string _status;
        private float _statusUntil;
        private Key _pendingConfirm = Key.None;
        private float _confirmUntil;
        private float _nextRepeat;
        private float _lastNudgeHeld = -1f;
        private bool _dragging;
        private EventSystem _eventSystem;
        private CalibrationMenu _menu;
        private bool _setupPage;
        private int _setupCursor;
        private SensorCalibrationTab _sensorTab;
        private bool _cameraTab;
        private readonly List<ProjectorOverlay.Marker> _markerScratch = new List<ProjectorOverlay.Marker>();
        private readonly List<ProjectorGeometry> _savedGeometry = new List<ProjectorGeometry>();

        public bool IsActive => _active;
        public ProjectionCalibrationEditor Editor => _editor;

        /// <summary>True when either tab has edits not yet saved.</summary>
        public bool HasUnsavedChanges => _active && (_editor.IsDirty || _sensorTab.IsDirty);

        internal bool CameraTabOpen => _cameraTab;
        internal bool SetupTabOpen => _setupPage;
        internal string Status => _status != null && Time.unscaledTime < _statusUntil ? _status : null;

        internal bool TextVisible
        {
            get => _hudVisible;
            set => _hudVisible = value;
        }

        internal TestPattern Pattern
        {
            get => _pattern;
            set { _pattern = value; ApplyPatternAndTint(); }
        }

        internal bool Tint
        {
            get => _tint;
            set { _tint = value; ApplyPatternAndTint(); }
        }

        private void Awake()
        {
            _rig = GetComponent<ProjectionOutputRig>();
            if (overlayShader == null) overlayShader = Shader.Find("CityTwin/CalibrationOverlay");
            _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            _sensorTab = new SensorCalibrationTab(_rig, Confirm, SetStatus);
            _menu = new CalibrationMenu(this, _rig, _sensorTab);
        }

        private void OnDestroy() => _menu?.Dispose();

        private void OnGUI()
        {
            if (!_active) return;
            if (_hudVisible) _menu.Draw();
            else _menu.DrawHidden();
        }

        private void OnEnable()
        {
            _rig.OutputsRebuilt += OnOutputsRebuilt;
            _rig.SensorCalibrationChanged += OnSensorCalibrationChanged;
        }

        private void OnDisable()
        {
            _rig.OutputsRebuilt -= OnOutputsRebuilt;
            _rig.SensorCalibrationChanged -= OnSensorCalibrationChanged;
            if (_active) Close();
        }

        private void OnSensorCalibrationChanged()
        {
            if (_active) _sensorTab.OnReloaded(_cameraTab);
        }

        private void Update()
        {
            var kb = Keyboard.current;
            if (kb == null) return;

            if (_active) _menu.RunPending();
            if (kb.ctrlKey.isPressed && kb.shiftKey.isPressed && kb.f4Key.wasPressedThisFrame)
            {
                if (!_active) Open();
                else if (HasUnsavedChanges) SetStatus("Unsaved changes: Enter = save & close, Esc = discard & close");
                else Close();
            }
            if (!_active) return;

            HandleKeyboard(kb);
            if (!_active) return; // Enter / Esc may have closed calibration
            if (_cameraTab) _sensorTab.Tick();
            if (!_setupPage && !_cameraTab) HandleMouse();
            DrawOverlays();
        }

        // ---------- open / close ----------

        /// <summary>Enter calibration mode (also callable from tools/tests).</summary>
        public void Open()
        {
            if (_active || !_rig.isActiveAndEnabled) return;
            if (overlayShader == null)
            {
                Debug.LogError("[ProjectionCalibrationController] Shader 'CityTwin/CalibrationOverlay' not found.", this);
                return;
            }
            _active = true;
            _editor = new ProjectionCalibrationEditor(_rig.Projectors);
            _editor.GeometryChanged += OnGeometryChanged;
            AnyOpen = true;
            _sensorTab.Hold();
            SyncProjectorToArea();
            RememberGeometry();
            CreateOverlays();
            ApplyPatternAndTint();

            // Clicks and keys must not drive game UI while calibrating.
            if (_reenableRoutine != null) { StopCoroutine(_reenableRoutine); _reenableRoutine = null; }
            _eventSystem = EventSystem.current;
            if (_eventSystem != null) _eventSystem.enabled = false;
            SetStatus($"Calibration open ({_rig.Store.CurrentSource} calibration loaded)");
        }

        /// <summary>Leave calibration mode. Unsaved edits stay live until the next reload; use Esc to discard.</summary>
        public void Close()
        {
            if (!_active) return;
            _sensorTab.Release();
            _cameraTab = false;
            _active = false;
            AnyOpen = false;
            _dragging = false;
            _setupPage = false;
            if (_editor != null) _editor.GeometryChanged -= OnGeometryChanged;
            DestroyOverlays();
            _rig.Pattern = TestPattern.Off;
            foreach (var o in _rig.Outputs)
                if (o != null) o.Tint = Color.white;
            // Not in this frame: the Enter that closed calibration is also the game UI's Submit, and would press
            // whatever game button is selected (the game menu). Wait until it is released, with nothing selected.
            if (_eventSystem != null)
            {
                if (isActiveAndEnabled) _reenableRoutine = StartCoroutine(ReenableEventSystem(_eventSystem));
                else _eventSystem.enabled = true;
            }
            _eventSystem = null;
        }

        private Coroutine _reenableRoutine;

        private System.Collections.IEnumerator ReenableEventSystem(EventSystem eventSystem)
        {
            yield return null;
            var kb = Keyboard.current;
            while (kb != null && (kb.enterKey.isPressed || kb.numpadEnterKey.isPressed || kb.spaceKey.isPressed))
                yield return null;
            if (eventSystem != null)
            {
                eventSystem.SetSelectedGameObject(null);
                eventSystem.enabled = true;
            }
            _reenableRoutine = null;
        }

        private void OnOutputsRebuilt()
        {
            if (!_active) return;
            _editor.Rebind(_rig.Projectors);
            SyncProjectorToArea();
            RememberGeometry();
            CreateOverlays();
            ApplyPatternAndTint();
        }

        private void OnGeometryChanged(int projector)
        {
            if (projector < 0) { _rig.MarkAllDirty(); return; }
            if (projector < _rig.Outputs.Count) _rig.Outputs[projector].MarkDirty();
        }

        // ---------- keyboard ----------

        private void HandleKeyboard(Keyboard kb)
        {
            if (_setupPage) { HandleSetupPage(kb); return; }

            bool shift = kb.shiftKey.isPressed;
            bool fine = kb.altKey.isPressed || kb.ctrlKey.isPressed;

            // During a camera capture N and Enter mean Next target (handled by the camera tab), not switch tab / save.
            bool capturing = _cameraTab && _sensorTab.IsCapturing;
            if (kb.nKey.wasPressedThisFrame && !capturing) SwitchTab();
            if (kb.oKey.wasPressedThisFrame) { OpenSetupPage(); return; }
            if (kb.escapeKey.wasPressedThisFrame) { DiscardAndClose(); return; }
            if ((kb.enterKey.wasPressedThisFrame || kb.numpadEnterKey.wasPressedThisFrame) && !capturing)
            {
                if (Save()) Close();
                return;
            }
            if (kb.sKey.wasPressedThisFrame) Save();

            if (kb.pKey.wasPressedThisFrame) { _pattern = NextPattern(_pattern); ApplyPatternAndTint(); }
            if (kb.tKey.wasPressedThisFrame) { _tint = !_tint; ApplyPatternAndTint(); }
            if (kb.hKey.wasPressedThisFrame) _hudVisible = !_hudVisible;
            if (kb.fKey.wasPressedThisFrame) _hudFlipped = !_hudFlipped;
            if (kb.lKey.wasPressedThisFrame) RequestRestorePrevious();
            if (kb.tabKey.wasPressedThisFrame)
            {
                if (_sensorTab.Areas.Count > 1) StepPlayArea(shift ? -1 : 1);
                else if (!_cameraTab) _editor.NextProjector(shift ? -1 : 1);
            }

            if (_cameraTab) { _sensorTab.HandleKeys(kb); return; }

            if (kb.spaceKey.wasPressedThisFrame) _editor.NextHandle(shift ? -1 : 1);
            if (kb.mKey.wasPressedThisFrame)
            {
                _editor.ToggleMeshMode();
                SetStatus(_editor.Target == CalibrationEditTarget.MeshNodes ? "Mesh mode: fine-tune nodes (M = back to corner points)" : "Corner points");
            }
            if (kb.backspaceKey.wasPressedThisFrame) _editor.ResetSelected();
            if (kb.cKey.wasPressedThisFrame && Confirm(Key.C, "Press C again to flatten this projector's mesh")) FlattenMesh();
            if (kb.zKey.wasPressedThisFrame && !_editor.Undo()) SetStatus("Nothing to undo");

            if (kb.bKey.wasPressedThisFrame) _editor.ToggleSeam();
            float seamStep = shift ? SeamStepLarge : SeamStep;
            if (kb.leftBracketKey.wasPressedThisFrame) _editor.MoveSeam(-seamStep);
            if (kb.rightBracketKey.wasPressedThisFrame) _editor.MoveSeam(seamStep);

            // Arrow nudges with key repeat. Up/right are +y/+x in projector space.
            Vector2 dir = Vector2.zero;
            bool anyPressed = false, anyHeld = false;
            AccumulateArrow(kb.leftArrowKey, Vector2.left, ref dir, ref anyPressed, ref anyHeld);
            AccumulateArrow(kb.rightArrowKey, Vector2.right, ref dir, ref anyPressed, ref anyHeld);
            AccumulateArrow(kb.upArrowKey, Vector2.up, ref dir, ref anyPressed, ref anyHeld);
            AccumulateArrow(kb.downArrowKey, Vector2.down, ref dir, ref anyPressed, ref anyHeld);
            if (anyHeld && dir != Vector2.zero)
            {
                bool fire = anyPressed || Time.unscaledTime >= _nextRepeat;
                if (fire)
                {
                    float step = shift ? 10f : fine ? 0.2f : 1f;
                    _editor.Nudge(dir * step, CurrentOutputPixelSize());
                    _nextRepeat = Time.unscaledTime + (anyPressed ? RepeatDelay : RepeatInterval);
                }
            }
        }

        // ---------- play areas ----------

        /// <summary>Step to another play area, keeping the current tab. Asks first when that would drop captured points.</summary>
        internal void StepPlayArea(int direction)
        {
            if (_sensorTab.Areas.Count < 2) return;
            if (_sensorTab.HasCaptureInProgress && !Confirm(Key.Tab, "Capture unfinished: press again to leave this play area")) return;
            if (_dragging)
            {
                _editor.EndDrag();
                _dragging = false;
            }
            _sensorTab.SelectArea(_sensorTab.SelectedIndex + direction, _cameraTab);
            SyncProjectorToArea();
        }

        private void SyncProjectorToArea()
        {
            var area = _sensorTab.SelectedArea;
            if (area == null) return;
            int projector = _sensorTab.ProjectorOf(area);
            if (projector >= 0) _editor.SelectProjector(projector);
        }

        // Clicking on the other projector's picture also moves the menu to a play area it lights.
        private void SyncAreaToProjector(int projector)
        {
            var area = _sensorTab.SelectedArea;
            if (area == null || _sensorTab.ProjectorOf(area) == projector || _sensorTab.HasCaptureInProgress) return;
            int index = _sensorTab.FirstAreaOn(projector);
            if (index >= 0) _sensorTab.SelectArea(index, false);
        }

        private void RememberGeometry()
        {
            _savedGeometry.Clear();
            foreach (var projector in _rig.Projectors)
                _savedGeometry.Add(new ProjectorGeometry(projector));
        }

        private HashSet<int> ProjectorsMovedSinceSave()
        {
            var moved = new HashSet<int>();
            for (int i = 0; i < _rig.Projectors.Count; i++)
                if (i >= _savedGeometry.Count || _savedGeometry[i].DiffersFrom(_rig.Projectors[i])) moved.Add(i);
            return moved;
        }

        private void SwitchTab()
        {
            if (_cameraTab) ShowProjectorTab();
            else ShowCameraTab();
        }

        internal void ShowProjectorTab()
        {
            _setupPage = false;
            _cameraTab = false;
            _dragging = false;
        }

        internal void ShowCameraTab()
        {
            _setupPage = false;
            _dragging = false;
            if (_cameraTab) return;
            _cameraTab = true;
            _sensorTab.EnterTab();
        }

        /// <summary>Where the target or point being worked on shows in the main window (Input System pixels), if it does.</summary>
        internal bool TryGetFocusWindow(out Vector2 window)
        {
            window = default;
            if (_setupPage) return false;
            if (_cameraTab)
            {
                if (!_sensorTab.TryGetFocus(out Vector2 tableUv)) return false;
                window = _rig.TableUvToWindow(tableUv);
                return true;
            }
            int projector = _editor.SelectedProjector;
            if (projector >= _rig.Outputs.Count) return false;
            var output = _rig.Outputs[projector];
            if (output == null || output.OutputCamera == null || output.OutputCamera.targetDisplay != 0) return false;
            CollectHandles(output, _editor.Target, _handleScratch);
            int handle = _editor.SelectedHandle;
            if (handle < 0 || handle >= _handleScratch.Count) return false;
            Rect r = output.OutputCamera.pixelRect;
            window = r.min + Vector2.Scale(_handleScratch[handle], r.size);
            return true;
        }

        internal void ToggleSetupTab()
        {
            if (_setupPage) _setupPage = false;
            else OpenSetupPage();
        }

        /// <summary>Move the selected point while a menu button is held, at the same rate as a held arrow key.</summary>
        internal void NudgeHeld(Vector2 pixels)
        {
            float now = Time.unscaledTime;
            bool firstOfHold = now - _lastNudgeHeld > NudgeHoldGap;
            _lastNudgeHeld = now;
            if (!firstOfHold && now < _nextRepeat) return;
            _editor.Nudge(pixels, CurrentOutputPixelSize());
            _nextRepeat = now + (firstOfHold ? RepeatDelay : RepeatInterval);
        }

        internal void FlattenMesh()
        {
            SetStatus(_editor.ClearMesh()
                ? "Fine mesh flattened. Undo brings it back; Save keeps it"
                : "This projector has no fine mesh to flatten");
        }

        internal void ResetProjector()
        {
            SetStatus(_editor.ResetProjectorToDefault(_rig.ActiveSetup.CreateDefaultProjectors())
                ? "Projector reset to default. Undo brings it back; Save keeps it"
                : "This projector has no default in this setup");
        }

        internal void ResetAll()
        {
            SetStatus(_editor.ResetAllToDefault(_rig.ActiveSetup.CreateDefaultProjectors())
                ? "All projectors reset to default. Undo brings them back; Save keeps it"
                : "The saved projector count doesn't match this setup's default");
        }

        internal void ChooseSetup(TableSetup chosen)
        {
            if (chosen == _rig.ActiveSetup) { _setupPage = false; return; }
            if (HasUnsavedChanges)
            {
                SetStatus("Unsaved changes: save or discard them before switching");
                return;
            }
            _setupPage = false;
            _rig.SwitchSetup(chosen); // reloads the scene; this controller goes with it
        }

        // ---------- setup page ----------

        private void OpenSetupPage()
        {
            _setupPage = true;
            _dragging = false;
            _setupCursor = 0;
            for (int i = 0; i < TableSetups.All.Count; i++)
                if (TableSetups.All[i] == _rig.ActiveSetup) _setupCursor = i;
        }

        private void HandleSetupPage(Keyboard kb)
        {
            int count = TableSetups.All.Count;
            if (kb.oKey.wasPressedThisFrame || kb.escapeKey.wasPressedThisFrame) { _setupPage = false; return; }
            if (kb.upArrowKey.wasPressedThisFrame) _setupCursor = (_setupCursor + count - 1) % count;
            if (kb.downArrowKey.wasPressedThisFrame) _setupCursor = (_setupCursor + 1) % count;
            if (!kb.enterKey.wasPressedThisFrame && !kb.numpadEnterKey.wasPressedThisFrame) return;

            ChooseSetup(TableSetups.All[_setupCursor]);
        }

        private string BuildSetupPage()
        {
            return $"TABLE SETUP: {TableSetups.All[_setupCursor].DisplayName}   Up/Down choose   Enter switch   O back";
        }

        private static void AccumulateArrow(UnityEngine.InputSystem.Controls.KeyControl key, Vector2 d, ref Vector2 dir, ref bool pressed, ref bool held)
        {
            if (!key.isPressed) return;
            dir += d;
            held = true;
            if (key.wasPressedThisFrame) pressed = true;
        }

        internal bool Confirm(Key key, string prompt)
        {
            if (_pendingConfirm == key && Time.unscaledTime < _confirmUntil)
            {
                _pendingConfirm = Key.None;
                return true;
            }
            _pendingConfirm = key;
            _confirmUntil = Time.unscaledTime + ConfirmWindow;
            SetStatus(prompt);
            return false;
        }

        internal bool Save()
        {
            string toCheck = _sensorTab.FlagProjectorsMoved(ProjectorsMovedSinceSave());
            if (_rig.SaveCalibration())
            {
                _editor.MarkSaved();
                _sensorTab.MarkSaved();
                RememberGeometry();
                SetStatus(toCheck.Length > 0 ? $"Saved. The picture moved, so check the camera on {toCheck}" : "Saved (previous calibration kept as a backup)");
                return true;
            }
            SetStatus("SAVE FAILED - see log");
            return false;
        }

        internal void SaveAndClose()
        {
            if (Save()) Close();
        }

        internal void RequestRestorePrevious()
        {
            if (Confirm(Key.L, "Press again to load the previously saved calibration")) RestorePrevious();
        }

        internal void DiscardAndClose()
        {
            if (HasUnsavedChanges) _rig.ReloadCalibration(); // rebuilds outputs with the saved data
            Close();
        }

        private void RestorePrevious()
        {
            var backups = _rig.Store.ListBackups();
            if (backups.Count == 0) { SetStatus("No previous calibration saved yet"); return; }
            if (_rig.RestoreBackup(backups[0], out string error)) SetStatus("Previous calibration restored");
            else SetStatus("Restore failed: " + error);
        }

        // ---------- mouse ----------

        private void HandleMouse()
        {
            var mouse = Mouse.current;
            if (mouse == null) return;
            Vector2 pos = mouse.position.ReadValue();

            if (mouse.leftButton.wasPressedThisFrame)
            {
                if (_hudVisible && _menu.ContainsWindowPoint(pos)) return;
                int projector = OutputAt(pos);
                if (projector < 0) return;
                _editor.SelectProjector(projector);
                SyncAreaToProjector(projector);
                int handle = NearestHandle(projector, pos, out float distPx);
                if (handle >= 0 && distPx <= mousePickRadiusPx)
                {
                    _editor.SelectHandle(handle);
                    _editor.BeginDrag();
                    _dragging = true;
                }
            }
            else if (_dragging && mouse.leftButton.isPressed)
            {
                var output = _rig.Outputs[_editor.SelectedProjector];
                _editor.DragSelectedTo(ScreenToProjectorUv(output, pos));
            }
            else if (_dragging)
            {
                _editor.EndDrag();
                _dragging = false;
            }
        }

        private int OutputAt(Vector2 screenPos)
        {
            for (int i = 0; i < _rig.Outputs.Count; i++)
            {
                var cam = _rig.Outputs[i].OutputCamera;
                if (cam.targetDisplay == 0 && cam.pixelRect.Contains(screenPos)) return i;
            }
            return -1;
        }

        private int NearestHandle(int projector, Vector2 screenPos, out float distPx)
        {
            var output = _rig.Outputs[projector];
            CollectHandles(output, _editor.Target, _handleScratch);
            Rect r = output.OutputCamera.pixelRect;
            int best = -1;
            distPx = float.MaxValue;
            for (int i = 0; i < _handleScratch.Count; i++)
            {
                Vector2 p = r.min + Vector2.Scale(_handleScratch[i], r.size);
                float d = Vector2.Distance(p, screenPos);
                if (d < distPx) { distPx = d; best = i; }
            }
            return best;
        }

        private static Vector2 ScreenToProjectorUv(ProjectorOutput output, Vector2 screenPos)
        {
            Rect r = output.OutputCamera.pixelRect;
            return new Vector2((screenPos.x - r.x) / Mathf.Max(1f, r.width), (screenPos.y - r.y) / Mathf.Max(1f, r.height));
        }

        // ---------- drawing ----------

        private void CreateOverlays()
        {
            DestroyOverlays();
            foreach (var output in _rig.Outputs)
            {
                var overlay = output.gameObject.AddComponent<ProjectorOverlay>();
                overlay.Init(output, overlayShader, _font);
                _overlays.Add(overlay);
            }
        }

        private void DestroyOverlays()
        {
            foreach (var o in _overlays)
                if (o != null) Destroy(o); // removes its graphics too
            _overlays.Clear();
        }

        private void ApplyPatternAndTint()
        {
            _rig.Pattern = _pattern;
            for (int i = 0; i < _rig.Outputs.Count; i++)
            {
                var o = _rig.Outputs[i];
                if (o == null) continue;
                o.Tint = _tint && projectorTints.Length > 0 ? projectorTints[i % projectorTints.Length] : Color.white;
            }
        }

        private void DrawOverlays()
        {
            // Camera tab: black out the game so only the targets and the tiles the camera sees are projected.
            var pattern = _cameraTab && !_setupPage ? TestPattern.Black : _pattern;
            if (_rig.Pattern != pattern) _rig.Pattern = pattern;
            string hud = _hudVisible ? BuildHud() : null;
            if (_cameraTab) { DrawCameraTabOverlays(hud); return; }
            for (int i = 0; i < _overlays.Count && i < _rig.Outputs.Count; i++)
            {
                var output = _rig.Outputs[i];
                bool selected = i == _editor.SelectedProjector;
                var target = selected ? _editor.Target : CalibrationEditTarget.ControlPoints;
                var handles = new List<Vector2>();
                CollectHandles(output, target, handles);
                _sensorTab.CollectAreaMarkers(i, _markerScratch, true);
                _overlays[i].Draw(new ProjectorOverlay.State
                {
                    IsSelectedProjector = selected,
                    Handles = handles,
                    Markers = _markerScratch,
                    SelectedHandle = _editor.SelectedHandle,
                    MeshMode = target == CalibrationEditTarget.MeshNodes,
                    MeshColumns = output.Calibration.meshColumns,
                    HudText = selected ? hud : null,
                    HudFlipped = _hudFlipped,
                });
            }
        }

        private void DrawCameraTabOverlays(string hud)
        {
            int hudProjector = _sensorTab.HudProjector(_editor.SelectedProjector);
            for (int i = 0; i < _overlays.Count && i < _rig.Outputs.Count; i++)
            {
                _sensorTab.CollectMarkers(i, _markerScratch);
                _overlays[i].Draw(new ProjectorOverlay.State
                {
                    IsSelectedProjector = i == hudProjector,
                    SelectedHandle = -1,
                    Markers = _markerScratch,
                    HudText = i == hudProjector ? hud : null,
                    HudFlipped = _hudFlipped,
                    HudAvoidUv = _sensorTab.HudAvoidUv(i),
                });
            }
        }

        private static void CollectHandles(ProjectorOutput output, CalibrationEditTarget target, List<Vector2> into)
        {
            into.Clear();
            var c = output.Calibration;
            if (target == CalibrationEditTarget.ControlPoints)
            {
                if (c.projectorPoints != null) into.AddRange(c.projectorPoints);
                return;
            }
            if (!c.HasMeshWarp) return;
            for (int i = 0; i < c.meshOffsets.Length; i++)
                into.Add(ProjectionMath.MeshNodeProjectorUv(c, i));
        }

        private string BuildHud()
        {
            if (_setupPage) return BuildSetupPage();
            var sb = new StringBuilder(128);
            if (_sensorTab.Areas.Count > 1) sb.Append(_sensorTab.SelectedArea.Name.ToUpperInvariant()).Append("   ");
            if (_cameraTab)
            {
                sb.Append(_sensorTab.BannerText());
            }
            else
            {
                var c = _editor.Current;
                string what = _editor.Target == CalibrationEditTarget.ControlPoints ? "point" : "node";
                sb.Append($"PROJECTOR: {c.name}, {what} {_editor.SelectedHandle + 1}/{_editor.HandleCount}");
            }
            if (Status != null) sb.Append('\n').Append(Status);
            return sb.ToString();
        }

        private Vector2 CurrentOutputPixelSize()
        {
            int i = _editor.SelectedProjector;
            return i < _rig.Outputs.Count ? _rig.Outputs[i].PixelSize : Vector2.one;
        }

        internal void SetStatus(string message)
        {
            _status = message;
            _statusUntil = Time.unscaledTime + StatusSeconds;
        }

        private static TestPattern NextPattern(TestPattern p)
        {
            int i = System.Array.IndexOf(PatternCycle, p);
            return PatternCycle[(i + 1) % PatternCycle.Length];
        }
    }
}
