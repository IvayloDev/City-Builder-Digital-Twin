using System;
using System.Collections.Generic;
using UnityEngine;

namespace CityTwin.Calibration
{
    /// <summary>
    /// The F9 panel: tabs, buttons and sliders, drawn on the main window. Every control calls the same action as its
    /// keyboard shortcut, so staff can work from either. Both tabs follow the play area chosen at the top. It starts centred and sized to its content, and slides aside only
    /// while it would cover the target or point being worked on. Staff can drag it by the title bar and resize it from any edge;
    /// after that it stays where they put it, across runs, until they press Fit.
    /// </summary>
    internal sealed class CalibrationMenu
    {
        private const float PanelWidth = 470f;
        private const float MinPanelWidth = 420f;
        private const float MaxPanelWidth = 900f;
        private const float TitleBarHeight = 52f;
        private const float EdgeGrab = 7f;
        private const float CornerGrab = 16f;
        private const string PrefsPrefix = "CityTwin.CalibrationMenu.v2."; // v2: bigger redesign, old placement dropped
        private const float Margin = 14f;
        private const float RowHeight = 46f;
        private const float TabHeight = 50f;
        private const float PanelPaddingX = 20f;
        private const float PanelPaddingY = 16f;
        private const float ScrollbarGutter = 18f;
        private const float RoundingSlack = 2f;
        private const float MinScrollHeight = 80f;
        private const float MinScale = 0.75f;
        private const float MaxScale = 3f;
        private const float ReferenceHeight = 1080f;
        private const float CmPerUv = SensorInputFilter.TableWidthCm;

        private static readonly Color Accent = new Color(0.016f, 0.757f, 0.996f);
        private static readonly Color Panel = new Color(0.055f, 0.067f, 0.106f, 0.97f);
        private static readonly Color Card = new Color(0.098f, 0.118f, 0.169f);
        private static readonly Color Control = new Color(0.157f, 0.184f, 0.247f);
        private static readonly Color ControlHover = new Color(0.216f, 0.251f, 0.329f);
        private static readonly Color Danger = new Color(0.62f, 0.18f, 0.2f);
        private static readonly Color Muted = new Color(0.66f, 0.71f, 0.79f);
        private static readonly Color Warning = new Color(1f, 0.76f, 0.25f);
        private static readonly Color Good = new Color(0.36f, 0.92f, 0.5f);

        private readonly ProjectionCalibrationController _controller;
        private readonly ProjectionOutputRig _rig;
        private readonly SensorCalibrationTab _sensors;
        private readonly List<Texture2D> _textures = new List<Texture2D>();
        private GUIStyle _panel, _card, _title, _section, _label, _labelLine, _areaLabel, _muted, _mutedLine, _tab, _tabActive, _button, _buttonActive, _primary, _danger, _sliderTrack, _sliderThumb;
        private Texture2D _white;
        private Vector2 _scroll;
        private int _dock; // 0 centred, -1 left, +1 right
        private float _topHeight = 70f;
        private float _contentHeight = 360f;
        private float _bottomHeight = 70f;
        private float _sizeFactor = 1f;
        private bool _reserveScrollbar;
        private int _reserveKey;
        private bool _placed;
        private Vector2 _placedPixels; // top-left in window pixels, so A- and A+ don't move the panel
        private float _width = PanelWidth;
        private float _userHeight; // 0 = follow the content
        private bool _pointerActive, _pointerMoved;
        private int _resizeX, _resizeY; // both 0 = moving
        private Vector2 _pressMouse;
        private Rect _pressRect;
                private Action _pending;

        public CalibrationMenu(ProjectionCalibrationController controller, ProjectionOutputRig rig, SensorCalibrationTab sensors)
        {
            _controller = controller;
            _rig = rig;
            _sensors = sensors;
            LoadPlacement();
        }

        /// <summary>
        /// Button actions wait for the next frame. IMGUI lays a window out once per event, so an action that adds or
        /// removes controls part-way through an event makes it throw.
        /// </summary>
        private void Later(Action action) => _pending = action;

        public void RunPending()
        {
            var action = _pending;
            _pending = null;
            action?.Invoke();
        }

        private float Scale => Mathf.Clamp(Screen.height / ReferenceHeight * _sizeFactor, MinScale, MaxScale);

        private Rect PanelRect(float scale, int dock)
        {
            float screenW = Screen.width / scale, screenH = Screen.height / scale;
            float maxHeight = screenH - Margin * 2f;
            float width = Mathf.Min(_width, screenW - Margin * 2f);
            float height = Mathf.Min(_userHeight > 0f ? Mathf.Max(_userHeight, MinPanelHeight) : WantedHeight + RoundingSlack, maxHeight);
            if (_placed)
            {
                return new Rect(
                    Mathf.Clamp(_placedPixels.x / scale, 0f, screenW - width),
                    Mathf.Clamp(_placedPixels.y / scale, 0f, screenH - height),
                    width, height);
            }
            float x = dock < 0 ? Margin : dock > 0 ? screenW - width - Margin : (screenW - width) * 0.5f;
            return new Rect(x, (screenH - height) * 0.5f, width, height);
        }

        // Header, tabs and footer always stay reachable; only the middle scrolls.
        private float MinPanelHeight => _topHeight + _bottomHeight + MinScrollHeight + PanelPaddingY;

        private float WantedHeight => _topHeight + _contentHeight + _bottomHeight + PanelPaddingY;

        // The content's width must not follow the scrollbar's visibility: wrapped text would change height, which resizes
        // the panel, which toggles the scrollbar again, and the panel flickers every frame. Once a page overflows it keeps
        // the narrow width until the page or the window changes.
        private float ContentWidth(Rect panel)
        {
            if (Event.current.type == EventType.Layout)
            {
                int page = _controller.SetupTabOpen ? 2 : _controller.CameraTabOpen ? 1 : 0;
                int key = (page, Screen.height, _sizeFactor, panel.width, _userHeight).GetHashCode();
                // Not while a resize is under way: the key changes every frame and the width would flicker with it.
                if (key != _reserveKey && !_pointerActive)
                {
                    _reserveKey = key;
                    _reserveScrollbar = false;
                }
                else if (WantedHeight > panel.height)
                {
                    _reserveScrollbar = true;
                }
            }
            return panel.width - PanelPaddingX - (_reserveScrollbar ? ScrollbarGutter : 0f);
        }

        /// <summary>True when a mouse position (Input System window pixels) is over the panel, so it doesn't also grab projector handles.</summary>
        public bool ContainsWindowPoint(Vector2 window)
        {
            float scale = Scale;
            return PanelRect(scale, _dock).Contains(new Vector2(window.x, Screen.height - window.y) / scale);
        }

        public void Draw()
        {
            EnsureStyles();
            float scale = Scale;
            var previousMatrix = GUI.matrix;
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f));

            // The menu is drawn on the projected image: during a camera capture keep it to one small bar.
            if (!_controller.SetupTabOpen && _controller.CameraTabOpen && _sensors.IsCapturing)
            {
                DrawCompactCapture();
                GUI.matrix = previousMatrix;
                return;
            }

            AvoidFocus(scale);
            Rect panel = PanelRect(scale, _dock);
            int pointerControl = GUIUtility.GetControlID(FocusType.Passive);
            HandlePointer(panel, scale, pointerControl);
            GUILayout.BeginArea(panel, _panel);

            GUILayout.BeginVertical();
            DrawHeader();
            DrawTabs();
            GUILayout.EndVertical();
            if (Event.current.type == EventType.Repaint) _topHeight = GUILayoutUtility.GetLastRect().height;

            float viewHeight = Mathf.Max(MinScrollHeight, panel.height - PanelPaddingY - _topHeight - _bottomHeight);
            _scroll = GUILayout.BeginScrollView(_scroll, false, false, GUIStyle.none, GUI.skin.verticalScrollbar, GUILayout.Height(viewHeight));
            GUILayout.BeginVertical(GUILayout.Width(ContentWidth(panel)));
            if (_controller.SetupTabOpen) DrawSetupTab();
            else if (_controller.CameraTabOpen) DrawCameraTab();
            else DrawProjectorTab();
            GUILayout.EndVertical();
            if (Event.current.type == EventType.Repaint) _contentHeight = GUILayoutUtility.GetLastRect().height;
            GUILayout.EndScrollView();

            GUILayout.BeginVertical();
            DrawFooter();
            GUILayout.EndVertical();
            if (Event.current.type == EventType.Repaint) _bottomHeight = GUILayoutUtility.GetLastRect().height;

            GUILayout.EndArea();

            // After the controls: a press the header's buttons took never reaches this.
            Event e = Event.current;
            if (!_pointerActive && e.type == EventType.MouseDown && e.button == 0
                && new Rect(panel.x, panel.y, panel.width, TitleBarHeight).Contains(e.mousePosition))
                BeginPointer(0, 0, panel, pointerControl);
            DrawGrips(panel);

            GUI.matrix = previousMatrix;
        }

        private const float CompactWidth = 700f;
        private const float CompactButtonHeight = 56f;
        // Kept well inside the window: a projector can crop the outer edge of its image.
        private const float CompactInset = 0.07f;

        // One row of big buttons, centred at the bottom (the black band under a letterboxed frame); the on-table text says what to do.
        private void DrawCompactCapture()
        {
            var session = _sensors.Session;
            float screenW = Screen.width / Scale, screenH = Screen.height / Scale;
            float width = Mathf.Min(CompactWidth, screenW - Margin * 2f);
            float height = CompactButtonHeight + 20f;
            var area = new Rect((screenW - width) * 0.5f, screenH * (1f - CompactInset) - height, width, height);
            GUILayout.BeginArea(area, _panel);
            GUILayout.BeginHorizontal();
            var tall = GUILayout.Height(CompactButtonHeight);
            if (session.IsClearingTable)
            {
                GUILayout.Label("CLEAR THE TABLE", _label, tall);
            }
            else
            {
                int shown = Mathf.Min(session.CurrentIndex + 1, session.Targets.Count);
                GUILayout.Label($"{shown}/{session.Targets.Count}", _label, GUILayout.Width(64f), tall);
                if (GUILayout.Button("BACK", _button, tall)) Later(() => _sensors.BackTarget());
                if (GUILayout.Button("NEXT", session.AwaitingNext ? _primary : _button, tall)) Later(() => _sensors.NextTarget());
                if (GUILayout.Button("SKIP", _button, tall)) Later(() => _sensors.SkipTarget());
            }
            if (GUILayout.Button("RESTART", _button, tall)) Later(() => _sensors.RequestRecapture());
            if (GUILayout.Button("MENU", _button, tall)) Later(() => _sensors.ShowVerify());
            GUILayout.EndHorizontal();
            GUILayout.EndArea();
        }

        /// <summary>A small way back when the panel is hidden to see the whole table.</summary>
        public void DrawHidden()
        {
            EnsureStyles();
            float scale = Scale;
            var previousMatrix = GUI.matrix;
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f));
            if (GUI.Button(new Rect(Margin, Margin, 200f, RowHeight), "MENU  (H)", _button)) _controller.TextVisible = true;
            GUI.matrix = previousMatrix;
        }

        private void AvoidFocus(float scale)
        {
            _dock = 0;
            if (_placed) return; // staff chose this spot, so it doesn't jump away under the mouse
            if (!_controller.TryGetFocusWindow(out Vector2 window)) return;
            Vector2 point = new Vector2(window.x, Screen.height - window.y) / scale;
            if (PanelRect(scale, 0).Contains(point))
                _dock = point.x > Screen.width / scale * 0.5f ? -1 : 1;
        }

        // ---------- move and resize ----------

        private void HandlePointer(Rect panel, float scale, int control)
        {
            Event e = Event.current;
            if (_pointerActive)
            {
                // MouseMove only fires with no button held, so it also recovers a release that was missed outside the window.
                if (e.rawType == EventType.MouseUp || e.type == EventType.MouseMove)
                {
                    EndPointer(scale, control);
                    if (e.type == EventType.MouseUp) e.Use();
                }
                else if (e.type == EventType.MouseDrag)
                {
                    ApplyPointer(e.mousePosition, scale);
                    e.Use();
                }
                return;
            }
            if (e.type != EventType.MouseDown || e.button != 0) return;
            EdgeAt(panel, e.mousePosition, out int x, out int y);
            if (x != 0 || y != 0) BeginPointer(x, y, panel, control);
        }

        private void BeginPointer(int resizeX, int resizeY, Rect panel, int control)
        {
            _pointerActive = true;
            _pointerMoved = false;
            _resizeX = resizeX;
            _resizeY = resizeY;
            _pressMouse = Event.current.mousePosition;
            _pressRect = panel;
            GUIUtility.hotControl = control;
            Event.current.Use();
        }

        private void ApplyPointer(Vector2 mouse, float scale)
        {
            Vector2 delta = mouse - _pressMouse;
            Rect r = _pressRect;
            float screenW = Screen.width / scale, screenH = Screen.height / scale;

            if (_resizeX == 0 && _resizeY == 0)
            {
                r.position += delta;
            }
            if (_resizeX > 0)
            {
                r.width = Limit(r.width + delta.x, MinPanelWidth, Mathf.Min(MaxPanelWidth, screenW - r.x));
            }
            else if (_resizeX < 0)
            {
                float right = r.xMax;
                r.width = Limit(r.width - delta.x, MinPanelWidth, Mathf.Min(MaxPanelWidth, right));
                r.x = right - r.width;
            }
            if (_resizeY > 0)
            {
                r.height = Limit(r.height + delta.y, MinPanelHeight, screenH - r.y);
            }
            else if (_resizeY < 0)
            {
                float bottom = r.yMax;
                r.height = Limit(r.height - delta.y, MinPanelHeight, bottom);
                r.y = bottom - r.height;
            }

            _placed = true;
            _pointerMoved = true;
            _placedPixels = r.position * scale;
            if (_resizeX != 0) _width = r.width;
            if (_resizeY != 0) _userHeight = r.height;
        }

        private static float Limit(float value, float min, float max) => Mathf.Clamp(value, min, Mathf.Max(min, max));

        private void EndPointer(float scale, int control)
        {
            _pointerActive = false;
            if (GUIUtility.hotControl == control) GUIUtility.hotControl = 0;
            if (!_pointerMoved) return;
            _placedPixels = PanelRect(scale, 0).position * scale;
            SavePlacement();
        }

        private static void EdgeAt(Rect panel, Vector2 mouse, out int x, out int y)
        {
            x = y = 0;
            if (!panel.Contains(mouse)) return;
            bool left = mouse.x < panel.xMin + EdgeGrab, right = mouse.x > panel.xMax - EdgeGrab;
            bool top = mouse.y < panel.yMin + EdgeGrab, bottom = mouse.y > panel.yMax - EdgeGrab;
            bool nearLeft = mouse.x < panel.xMin + CornerGrab, nearRight = mouse.x > panel.xMax - CornerGrab;
            bool nearTop = mouse.y < panel.yMin + CornerGrab, nearBottom = mouse.y > panel.yMax - CornerGrab;

            if (nearRight && nearBottom) { x = 1; y = 1; return; } // the visible grip
            if (left || right || top || bottom)
            {
                x = left || (nearLeft && (top || bottom)) ? -1 : right || (nearRight && (top || bottom)) ? 1 : 0;
                y = top || (nearTop && (left || right)) ? -1 : bottom || (nearBottom && (left || right)) ? 1 : 0;
            }
        }

        private void DrawGrips(Rect panel)
        {
            if (Event.current.type != EventType.Repaint) return;

            // Title bar: a block of dots that reads as "hold here".
            for (int column = 0; column < 2; column++)
                for (int row = 0; row < 3; row++)
                    DrawRect(new Rect(panel.x + 12f + column * 5f, panel.y + TitleBarHeight * 0.5f - 6f + row * 5f, 3f, 3f), Muted);

            // Resize corner: a triangle of dots.
            for (int row = 0; row < 3; row++)
                for (int column = 0; column < 3 - row; column++)
                    DrawRect(new Rect(panel.xMax - 4f - column * 4f, panel.yMax - 4f - row * 4f, 2f, 2f), Muted);

            int x = _resizeX, y = _resizeY;
            if (!_pointerActive) EdgeAt(panel, Event.current.mousePosition, out x, out y);
            if (x < 0) DrawRect(new Rect(panel.xMin, panel.yMin, 2f, panel.height), Accent);
            if (x > 0) DrawRect(new Rect(panel.xMax - 2f, panel.yMin, 2f, panel.height), Accent);
            if (y < 0) DrawRect(new Rect(panel.xMin, panel.yMin, panel.width, 2f), Accent);
            if (y > 0) DrawRect(new Rect(panel.xMin, panel.yMax - 2f, panel.width, 2f), Accent);
        }

        private void LoadPlacement()
        {
            if (PlayerPrefs.GetInt(PrefsPrefix + "Placed", 0) != 1) return;
            _placed = true;
            _placedPixels = new Vector2(PlayerPrefs.GetFloat(PrefsPrefix + "X", 0f), PlayerPrefs.GetFloat(PrefsPrefix + "Y", 0f));
            _width = Mathf.Clamp(PlayerPrefs.GetFloat(PrefsPrefix + "Width", PanelWidth), MinPanelWidth, MaxPanelWidth);
            _userHeight = Mathf.Max(0f, PlayerPrefs.GetFloat(PrefsPrefix + "Height", 0f));
        }

        private void SavePlacement()
        {
            PlayerPrefs.SetInt(PrefsPrefix + "Placed", _placed ? 1 : 0);
            PlayerPrefs.SetFloat(PrefsPrefix + "X", _placedPixels.x);
            PlayerPrefs.SetFloat(PrefsPrefix + "Y", _placedPixels.y);
            PlayerPrefs.SetFloat(PrefsPrefix + "Width", _width);
            PlayerPrefs.SetFloat(PrefsPrefix + "Height", _userHeight);
            PlayerPrefs.Save();
        }

        // ---------- sections ----------
        // Minimal, all caps, big type: readable on a 860x1080 window and on the projected image.

        private static string Caps(string text) => string.IsNullOrEmpty(text) ? text : text.ToUpperInvariant();

        private void DrawHeader()
        {
            GUILayout.BeginHorizontal(GUILayout.Height(TitleBarHeight));
            GUILayout.Space(16f); // the drag dots
            GUILayout.Label("CALIBRATION", _title, GUILayout.Height(TitleBarHeight));
            GUILayout.Space(10f);
            var previous = GUI.contentColor;
            GUI.contentColor = _controller.HasUnsavedChanges ? Warning : Good;
            GUILayout.Label(_controller.HasUnsavedChanges ? "UNSAVED" : "SAVED", _mutedLine, GUILayout.Height(TitleBarHeight));
            GUI.contentColor = previous;
            GUILayout.FlexibleSpace();
            var small = GUILayout.Height(TitleBarHeight - 10f);
            if (GUILayout.Button("A-", _button, GUILayout.Width(44f), small)) Later(() => _sizeFactor = Mathf.Max(0.6f, _sizeFactor - 0.1f));
            if (GUILayout.Button("A+", _button, GUILayout.Width(44f), small)) Later(() => _sizeFactor = Mathf.Min(2f, _sizeFactor + 0.1f));
            if (GUILayout.Button("HIDE", _button, GUILayout.Width(76f), small)) Later(() => _controller.TextVisible = false);
            GUILayout.EndHorizontal();
            GUILayout.Space(6f);
        }

        private void DrawTabs()
        {
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("PROJECTOR", TabStyle(!_controller.CameraTabOpen && !_controller.SetupTabOpen), GUILayout.Height(TabHeight)))
                Later(_controller.ShowProjectorTab);
            if (GUILayout.Button("CAMERA", TabStyle(_controller.CameraTabOpen && !_controller.SetupTabOpen), GUILayout.Height(TabHeight)))
                Later(_controller.ShowCameraTab);
            if (GUILayout.Button("SETUP", TabStyle(_controller.SetupTabOpen), GUILayout.Height(TabHeight)))
                Later(_controller.ToggleSetupTab);
            GUILayout.EndHorizontal();
            GUILayout.Space(8f);
            if (!_controller.SetupTabOpen) DrawAreaSwitcher();
        }

        private void DrawAreaSwitcher()
        {
            var areas = _sensors.Areas;
            if (areas.Count < 2) return;
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("<", _button, GUILayout.Width(RowHeight), GUILayout.Height(RowHeight))) Later(() => _controller.StepPlayArea(-1));
            GUILayout.Label(Caps(_sensors.SelectedArea.Name), _areaLabel, GUILayout.ExpandWidth(true), GUILayout.Height(RowHeight));
            if (GUILayout.Button(">", _button, GUILayout.Width(RowHeight), GUILayout.Height(RowHeight))) Later(() => _controller.StepPlayArea(1));
            GUILayout.EndHorizontal();
            GUILayout.Space(8f);
        }

        private GUIStyle TabStyle(bool active) => active ? _tabActive : _tab;

        private void DrawProjectorTab()
        {
            var editor = _controller.Editor;
            if (editor.ProjectorCount > 1 && !_sensors.AreasReachProjectors(editor.ProjectorCount))
            {
                BeginCard("PROJECTOR");
                GUILayout.BeginHorizontal();
                for (int i = 0; i < editor.ProjectorCount; i++)
                {
                    int index = i;
                    if (GUILayout.Button(Caps(editor.Projectors[i].name), Choice(i == editor.SelectedProjector), GUILayout.Height(RowHeight))) Later(() => editor.SelectProjector(index));
                }
                GUILayout.EndHorizontal();
                EndCard();
            }

            bool mesh = editor.Target == CalibrationEditTarget.MeshNodes;
            BeginCard(mesh ? "MESH" : "CORNERS");
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("CORNERS", Choice(!mesh), GUILayout.Height(RowHeight)) && mesh) Later(() => editor.ToggleMeshMode());
            if (GUILayout.Button("MESH", Choice(mesh), GUILayout.Height(RowHeight)) && !mesh) Later(() => editor.ToggleMeshMode());
            GUILayout.EndHorizontal();
            GUILayout.Label(mesh ? "CLICK A NODE · DRAG OR ARROWS" : "CLICK AN INNER POINT · DRAG OR ARROWS", _muted);
            GUILayout.Label("SHIFT ×10 · ALT FINE · SPACE NEXT POINT", _muted);
            GUILayout.Space(4f);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("RESET POINT", _button, GUILayout.Height(RowHeight))) Later(() => editor.ResetSelected());
            if (GUILayout.Button("UNDO", _button, GUILayout.Height(RowHeight))) Later(() => { if (!editor.Undo()) _controller.SetStatus("Nothing to undo"); });
            if (mesh && GUILayout.Button("FLATTEN", _button, GUILayout.Height(RowHeight))) Later(_controller.FlattenMesh);
            GUILayout.EndHorizontal();
            if (GUILayout.Button(editor.ProjectorCount > 1 ? "RESET THIS PROJECTOR" : "RESET PROJECTOR", _danger, GUILayout.Height(RowHeight))) Later(_controller.ResetProjector);
            if (editor.ProjectorCount > 1 && GUILayout.Button("RESET ALL", _danger, GUILayout.Height(RowHeight))) Later(_controller.ResetAll);
            EndCard();

            if (editor.ProjectorCount > 1)
            {
                var current = editor.Current;
                BeginCard("SEAM");
                bool seamOn = current != null && current.seamEnabled;
                if (GUILayout.Toggle(seamOn, seamOn ? "SEAM CUT ON" : "SEAM CUT OFF", Choice(seamOn), GUILayout.Height(RowHeight)) != seamOn) editor.ToggleSeam();
                float shiftCm = editor.SeamShift * CmPerUv, max = ProjectionCalibrationEditor.MaxSeamShift * CmPerUv;
                float next = Slider("POSITION", shiftCm, -max, max, "{0:+0.0;-0.0;0.0} CM");
                if (!Mathf.Approximately(next, shiftCm)) editor.MoveSeam((next - shiftCm) / CmPerUv);
                EndCard();
            }
        }

        // During a capture the panel gives way to the compact bar (DrawCompactCapture), so this is the camera's summary.
        private void DrawCameraTab()
        {
            var cameras = _sensors.Sensors;
            if (cameras.Count == 0)
            {
                GUILayout.Label("NO GAME RUNNING · NO CAMERA", _label);
                return;
            }

            var sensor = cameras[Mathf.Clamp(_sensors.SelectedIndex, 0, cameras.Count - 1)];
            BeginCard($"CAMERA {sensor.SensorIndex + 1}");
            if (_sensors.IsFitted)
            {
                var saved = _sensors.Saved;
                GUILayout.Label("CALIBRATED", _label);
                GUILayout.Label($"{saved.pointCount} POINTS · ERROR {saved.rmsError * CmPerUv:0.0} CM", _muted);
            }
            else
            {
                GUILayout.Label("NOT CALIBRATED", _label);
            }
            Warn(_controller.Editor.IsDirty ? "SAVE THE PROJECTOR FIRST" : null);
            if (_sensors.IsFitted && _sensors.Saved.projectorMovedSinceFit)
            {
                Warn("PROJECTOR MOVED SINCE THIS FIT");
                if (GUILayout.Button("STILL LINES UP", _button, GUILayout.Height(RowHeight))) Later(() => _sensors.MarkStillAligned());
            }
            GUILayout.Space(4f);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(_sensors.IsFitted ? "CALIBRATE AGAIN" : "CALIBRATE", _primary, GUILayout.Height(RowHeight))) Later(() => _sensors.StartCapture());
            if (_sensors.IsFitted && GUILayout.Button("CLEAR", _danger, GUILayout.Height(RowHeight))) Later(() => _sensors.RequestClearFit());
            GUILayout.EndHorizontal();
            EndCard();
        }

        private void Warn(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            var previous = GUI.contentColor;
            GUI.contentColor = Warning;
            GUILayout.Label(text, _muted);
            GUI.contentColor = previous;
        }

        private void DrawFilterCard()
        {
            var filter = _sensors.Filter;
            BeginCard("JITTER FILTER");
            bool on = GUILayout.Toggle(filter.enabled, filter.enabled ? "FILTER ON" : "FILTER OFF", Choice(filter.enabled), GUILayout.Height(RowHeight));
            if (on != filter.enabled) _sensors.SetFilterEnabled(on);

            float radius = Slider("DEAD ZONE", filter.radiusCm, 0f, SensorFilterSettings.MaxRadiusCm, "{0:0.0} CM");
            if (!Mathf.Approximately(radius, filter.radiusCm)) _sensors.SetRadius(radius);
            float smoothing = Slider("SMOOTHING", filter.smoothingSeconds, 0f, SensorFilterSettings.MaxSmoothingSeconds, "{0:0.00} S");
            if (!Mathf.Approximately(smoothing, filter.smoothingSeconds)) _sensors.SetSmoothing(smoothing);

            GUILayout.BeginHorizontal();
            GUILayout.Label(_sensors.HasJitterReading ? $"JITTER {_sensors.JitterCm:0.00} CM" : "JITTER · LEAVE A TILE STILL", _mutedLine, GUILayout.Height(RowHeight));
            if (GUILayout.Button("AUTO", _button, GUILayout.Width(100f), GUILayout.Height(RowHeight))) Later(() => _sensors.SetRadiusFromJitter());
            GUILayout.EndHorizontal();
            EndCard();
        }

        private void DrawSetupTab()
        {
            BeginCard("TABLE");
            foreach (var setup in TableSetups.All)
            {
                bool active = setup == _rig.ActiveSetup;
                if (GUILayout.Button(Caps(setup.DisplayName), active ? _buttonActive : _button, GUILayout.Height(RowHeight)) && !active)
                    Later(() => _controller.ChooseSetup(setup));
            }
            GUILayout.Label("SWITCHING RESTARTS THE GAME", _muted);
            EndCard();

            if (_sensors.Sensors.Count > 0) DrawFilterCard();
        }

        private void DrawFooter()
        {
            GUILayout.Space(4f);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("CANCEL", _controller.HasUnsavedChanges ? _danger : _button, GUILayout.Height(RowHeight))) Later(() => _controller.DiscardAndClose());
            if (GUILayout.Button("SAVE", _button, GUILayout.Height(RowHeight))) Later(() => _controller.Save());
            if (GUILayout.Button("SAVE & CLOSE", _primary, GUILayout.Height(RowHeight))) Later(() => _controller.SaveAndClose());
            GUILayout.EndHorizontal();
        }

        // ---------- controls ----------

        private GUIStyle Choice(bool active) => active ? _buttonActive : _button;

        private void BeginCard(string heading)
        {
            GUILayout.BeginVertical(_card);
            GUILayout.Label(heading, _section);
        }

        private void EndCard()
        {
            GUILayout.EndVertical();
            GUILayout.Space(8f);
        }

        private float Slider(string label, float value, float min, float max, string format)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, _labelLine);
            GUILayout.FlexibleSpace();
            GUILayout.Label(string.Format(format, value), _labelLine);
            GUILayout.EndHorizontal();

            Rect r = GUILayoutUtility.GetRect(10f, 30f, GUILayout.ExpandWidth(true));
            if (Event.current.type == EventType.Repaint)
            {
                float fill = Mathf.InverseLerp(min, max, value);
                var track = new Rect(r.x, r.center.y - 4f, r.width, 8f);
                DrawRect(track, Control);
                DrawRect(new Rect(track.x, track.y, track.width * fill, track.height), Accent);
            }
            return GUI.HorizontalSlider(r, value, min, max, _sliderTrack, _sliderThumb);
        }

        private void DrawRect(Rect r, Color color)
        {
            var previous = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(r, _white);
            GUI.color = previous;
        }

        // ---------- styles ----------

        private void EnsureStyles()
        {
            if (_panel != null && _white != null) return;

            _white = Solid(Color.white);
            _panel = new GUIStyle { normal = { background = Solid(Panel) }, padding = new RectOffset(14, 14, 8, 12) };
            _card = new GUIStyle { normal = { background = Solid(Card) }, padding = new RectOffset(12, 12, 10, 12) };

            _title = Text(26, FontStyle.Bold, Color.white);
            _section = Text(17, FontStyle.Bold, Accent);
            _section.margin = new RectOffset(0, 0, 0, 6);
            _label = Text(20, FontStyle.Bold, Color.white);
            _muted = Text(15, FontStyle.Normal, Muted);
            _title.wordWrap = false;
            _labelLine = new GUIStyle(_label) { wordWrap = false };
            _mutedLine = new GUIStyle(_muted) { wordWrap = false };
            _areaLabel = new GUIStyle(_labelLine) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };

            _button = Button(Control, ControlHover, Color.white, FontStyle.Normal);
            _buttonActive = Button(Accent * 0.72f + Color.black * 0.28f, Accent, Color.white, FontStyle.Bold);
            _primary = Button(Accent * 0.62f + Color.black * 0.38f, Accent * 0.8f + Color.black * 0.2f, Color.white, FontStyle.Bold);
            _danger = Button(Danger, Danger * 1.25f, Color.white, FontStyle.Bold);
            _tab = Button(Card, ControlHover, Muted, FontStyle.Bold);
            _tabActive = Button(Accent * 0.72f + Color.black * 0.28f, Accent, Color.white, FontStyle.Bold);

            var thumb = Solid(Color.white);
            _sliderTrack = new GUIStyle { fixedHeight = 30f };
            _sliderThumb = new GUIStyle
            {
                normal = { background = thumb },
                hover = { background = thumb },
                active = { background = Solid(Accent) },
                fixedWidth = 16f,
                fixedHeight = 28f,
            };
        }

        private GUIStyle Text(int size, FontStyle style, Color color)
        {
            return new GUIStyle(GUI.skin.label)
            {
                fontSize = size,
                fontStyle = style,
                wordWrap = true,
                normal = { textColor = color },
                margin = new RectOffset(0, 0, 1, 1),
                padding = new RectOffset(0, 0, 1, 1),
                alignment = TextAnchor.MiddleLeft,
            };
        }

        private GUIStyle Button(Color background, Color hover, Color text, FontStyle style)
        {
            Texture2D normal = Solid(new Color(background.r, background.g, background.b, 1f));
            Texture2D over = Solid(new Color(hover.r, hover.g, hover.b, 1f));
            return new GUIStyle
            {
                normal = { background = normal, textColor = text },
                hover = { background = over, textColor = Color.white },
                active = { background = over, textColor = Color.white },
                onNormal = { background = normal, textColor = text },
                onHover = { background = over, textColor = Color.white },
                onActive = { background = over, textColor = Color.white },
                alignment = TextAnchor.MiddleCenter,
                fontSize = 18,
                fontStyle = FontStyle.Bold,
                margin = new RectOffset(3, 3, 3, 3),
                padding = new RectOffset(8, 8, 4, 4),
            };
        }

        private Texture2D Solid(Color color)
        {
            var texture = new Texture2D(1, 1, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
            texture.SetPixel(0, 0, color);
            texture.Apply(false, true);
            _textures.Add(texture);
            return texture;
        }

        public void Dispose()
        {
            foreach (var t in _textures)
                if (t != null) UnityEngine.Object.Destroy(t);
            _textures.Clear();
            _panel = null;
            _white = null;
        }
    }
}
