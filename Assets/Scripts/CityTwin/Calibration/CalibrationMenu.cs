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
        private const float PanelWidth = 372f;
        private const float MinPanelWidth = 330f;
        private const float MaxPanelWidth = 900f;
        private const float TitleBarHeight = 34f;
        private const float EdgeGrab = 7f;
        private const float CornerGrab = 16f;
        private const string PrefsPrefix = "CityTwin.CalibrationMenu.";
        private const float Margin = 14f;
        private const float RowHeight = 26f;
        private const float TabHeight = 30f;
        private const float StatusHeight = 30f;
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
        private float _stepPx = 1f;
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

        private const float CompactWidth = 560f;
        private const float CompactButtonHeight = 40f;
        // Kept well inside the window: a projector can crop the outer edge of its image.
        private const float CompactInset = 0.07f;

        // One row of big buttons, centred at the bottom (the black band under a letterboxed frame); the on-table text says what to do.
        private void DrawCompactCapture()
        {
            var session = _sensors.Session;
            float screenW = Screen.width / Scale, screenH = Screen.height / Scale;
            float height = CompactButtonHeight + 16f;
            var area = new Rect((screenW - CompactWidth) * 0.5f, screenH * (1f - CompactInset) - height, CompactWidth, height);
            GUILayout.BeginArea(area, _panel);
            GUILayout.BeginHorizontal();
            var tall = GUILayout.Height(CompactButtonHeight);
            if (session.IsClearingTable)
            {
                GUILayout.Label("Clear the table", _label, tall);
            }
            else
            {
                int shown = Mathf.Min(session.CurrentIndex + 1, session.Targets.Count);
                GUILayout.Label($"{shown}/{session.Targets.Count}", _label, GUILayout.Width(44f), tall);
                if (GUILayout.Button("Back", _button, tall, GUILayout.Width(110f))) Later(() => _sensors.BackTarget());
                if (GUILayout.Button("Next", session.AwaitingNext ? _primary : _button, tall, GUILayout.Width(130f))) Later(() => _sensors.NextTarget());
                if (GUILayout.Button("Skip", _button, tall)) Later(() => _sensors.SkipTarget());
            }
            if (GUILayout.Button("Restart", _button, tall)) Later(() => _sensors.RequestRecapture());
            if (GUILayout.Button("Menu", _button, tall)) Later(() => _sensors.ShowVerify());
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
            if (GUI.Button(new Rect(Margin, Margin, 150f, RowHeight), "Show menu  (H)", _button)) _controller.TextVisible = true;
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
                    DrawRect(new Rect(panel.x + 10f + column * 4f, panel.y + 13f + row * 4f, 2f, 2f), Muted);

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

        private void FitToContent()
        {
            _placed = false;
            _width = PanelWidth;
            _userHeight = 0f;
            _scroll = Vector2.zero;
            SavePlacement();
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

        private void DrawHeader()
        {
            GUILayout.BeginHorizontal();
            GUILayout.Space(12f); // the drag dots
            GUILayout.Label("Calibration", _title);
            GUILayout.Space(8f);
            var previous = GUI.contentColor;
            GUI.contentColor = _controller.HasUnsavedChanges ? Warning : Good;
            GUILayout.Label(_controller.HasUnsavedChanges ? "Unsaved" : "Saved", _mutedLine);
            GUI.contentColor = previous;
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("A-", _button, GUILayout.Width(30f), GUILayout.Height(24f))) Later(() => _sizeFactor = Mathf.Max(0.6f, _sizeFactor - 0.1f));
            if (GUILayout.Button("A+", _button, GUILayout.Width(30f), GUILayout.Height(24f))) Later(() => _sizeFactor = Mathf.Min(2f, _sizeFactor + 0.1f));
            if (GUILayout.Button("Fit", _button, GUILayout.Width(32f), GUILayout.Height(24f))) Later(FitToContent);
            if (GUILayout.Button("Hide", _button, GUILayout.Width(44f), GUILayout.Height(24f))) Later(() => _controller.TextVisible = false);
            GUILayout.EndHorizontal();
            GUILayout.Space(2f);
        }

        private void DrawTabs()
        {
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("1  Projector", TabStyle(!_controller.CameraTabOpen && !_controller.SetupTabOpen), GUILayout.Height(TabHeight)))
                Later(_controller.ShowProjectorTab);
            if (GUILayout.Button("2  Camera", TabStyle(_controller.CameraTabOpen && !_controller.SetupTabOpen), GUILayout.Height(TabHeight)))
                Later(_controller.ShowCameraTab);
            if (GUILayout.Button("Table setup", TabStyle(_controller.SetupTabOpen), GUILayout.Height(TabHeight)))
                Later(_controller.ToggleSetupTab);
            GUILayout.EndHorizontal();
            GUILayout.Space(4f);
            if (!_controller.SetupTabOpen) DrawAreaSwitcher();
        }

        private void DrawAreaSwitcher()
        {
            var areas = _sensors.Areas;
            if (areas.Count < 2) return;
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("<", _button, GUILayout.Width(44f), GUILayout.Height(RowHeight))) Later(() => _controller.StepPlayArea(-1));
            GUILayout.Label($"{_sensors.SelectedArea.Name}   {_sensors.SelectedIndex + 1} of {areas.Count}", _areaLabel, GUILayout.ExpandWidth(true), GUILayout.Height(RowHeight));
            if (GUILayout.Button(">", _button, GUILayout.Width(44f), GUILayout.Height(RowHeight))) Later(() => _controller.StepPlayArea(1));
            GUILayout.EndHorizontal();
            GUILayout.Space(4f);
        }

        private GUIStyle TabStyle(bool active) => active ? _tabActive : _tab;

        private void DrawProjectorTab()
        {
            var editor = _controller.Editor;
            BeginCard("Projector");
            if (editor.ProjectorCount > 1 && !_sensors.AreasReachProjectors(editor.ProjectorCount))
            {
                GUILayout.BeginHorizontal();
                for (int i = 0; i < editor.ProjectorCount; i++)
                {
                    int index = i;
                    if (GUILayout.Button(editor.Projectors[i].name, Choice(i == editor.SelectedProjector), GUILayout.Height(RowHeight))) Later(() => editor.SelectProjector(index));
                }
                GUILayout.EndHorizontal();
            }
            else if (editor.Current != null)
            {
                GUILayout.Label(editor.Current.name, _label);
            }
            string shared = _sensors.SharedText(editor.SelectedProjector);
            if (shared != null) GUILayout.Label(shared, _muted);
            EndCard();

            BeginCard("Adjust the picture");
            bool mesh = editor.Target == CalibrationEditTarget.MeshNodes;
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Corner points", Choice(!mesh), GUILayout.Height(RowHeight)) && mesh) Later(() => editor.ToggleMeshMode());
            if (GUILayout.Button("Fine mesh", Choice(mesh), GUILayout.Height(RowHeight)) && !mesh) Later(() => editor.ToggleMeshMode());
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("<", _button, GUILayout.Width(44f), GUILayout.Height(RowHeight))) Later(() => editor.NextHandle(-1));
            GUILayout.Label($"{(mesh ? "Node" : "Point")} {editor.SelectedHandle + 1} of {editor.HandleCount}", _labelLine, GUILayout.ExpandWidth(true), GUILayout.Height(RowHeight));
            if (GUILayout.Button(">", _button, GUILayout.Width(44f), GUILayout.Height(RowHeight))) Later(() => editor.NextHandle(1));
            GUILayout.EndHorizontal();

            DrawMovePad();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Reset point", _button, GUILayout.Height(RowHeight))) Later(() => editor.ResetSelected());
            if (GUILayout.Button("Undo", _button, GUILayout.Height(RowHeight))) Later(() => { if (!editor.Undo()) _controller.SetStatus("Nothing to undo"); });
            if (GUILayout.Button("Flatten mesh", _button, GUILayout.Height(RowHeight))) Later(_controller.FlattenMesh);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Reset projector to default", _danger, GUILayout.Height(RowHeight))) Later(_controller.ResetProjector);
            if (editor.ProjectorCount > 1 && GUILayout.Button("Reset all", _danger, GUILayout.Height(RowHeight))) Later(_controller.ResetAll);
            GUILayout.EndHorizontal();
            EndCard();

            if (editor.ProjectorCount > 1)
            {
                var current = editor.Current;
                BeginCard("Seam between projectors");
                bool seamOn = current != null && current.seamEnabled;
                if (GUILayout.Toggle(seamOn, seamOn ? "  Seam cut on" : "  Seam cut off", _button, GUILayout.Height(RowHeight)) != seamOn) editor.ToggleSeam();
                float shiftCm = editor.SeamShift * CmPerUv, max = ProjectionCalibrationEditor.MaxSeamShift * CmPerUv;
                float next = Slider("Position", shiftCm, -max, max, "{0:+0.0;-0.0;0.0} cm");
                if (!Mathf.Approximately(next, shiftCm)) editor.MoveSeam((next - shiftCm) / CmPerUv);
                EndCard();
            }

            DrawDisplayCard();

            BeginCard("Cameras");
            foreach (var area in _sensors.Areas)
                AreaStatusLine(area);
            if (_sensors.Areas.Count == 0) GUILayout.Label("No game running", _label);
            EndCard();
        }

        private void AreaStatusLine(PlayArea area)
        {
            var previous = GUI.contentColor;
            if (_sensors.NeedsCheck(area.Sensor)) GUI.contentColor = Warning;
            GUILayout.Label($"{area.Name}: {_sensors.FitSummary(area.Sensor)}", _muted);
            GUI.contentColor = previous;
        }

        private void DrawMovePad()
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("Step", _mutedLine, GUILayout.Width(36f), GUILayout.Height(RowHeight));
            foreach (float step in new[] { 0.2f, 1f, 10f })
                if (GUILayout.Button($"{step:0.#} px", Choice(Mathf.Approximately(_stepPx, step)), GUILayout.Height(RowHeight))) Later(() => _stepPx = step);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.RepeatButton("Up", _button, GUILayout.Width(62f), GUILayout.Height(RowHeight))) _controller.NudgeHeld(Vector2.up * _stepPx);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.RepeatButton("Left", _button, GUILayout.Width(62f), GUILayout.Height(RowHeight))) _controller.NudgeHeld(Vector2.left * _stepPx);
            if (GUILayout.RepeatButton("Down", _button, GUILayout.Width(62f), GUILayout.Height(RowHeight))) _controller.NudgeHeld(Vector2.down * _stepPx);
            if (GUILayout.RepeatButton("Right", _button, GUILayout.Width(62f), GUILayout.Height(RowHeight))) _controller.NudgeHeld(Vector2.right * _stepPx);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
        }

        private void DrawCameraTab()
        {
            var cameras = _sensors.Sensors;
            if (cameras.Count == 0)
            {
                GUILayout.Label("No game is running, so there is no camera to calibrate.", _label);
                return;
            }
            if (_controller.Editor.IsDirty)
            {
                var previous = GUI.contentColor;
                GUI.contentColor = Warning;
                GUILayout.Label("Projector edits are unsaved. Targets show through the current picture, so save those first.", _muted);
                GUI.contentColor = previous;
            }

            var sensor = cameras[Mathf.Clamp(_sensors.SelectedIndex, 0, cameras.Count - 1)];
            BeginCard($"Camera for play area {sensor.SensorIndex + 1}");
            GUILayout.Label($"Port {sensor.ListenPort}: {_sensors.FitSummary(sensor)}", _muted);

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Capture", Choice(_sensors.IsCapturing), GUILayout.Height(RowHeight)) && !_sensors.IsCapturing) Later(() => _sensors.ShowCapture());
            if (GUILayout.Button("Verify", Choice(!_sensors.IsCapturing), GUILayout.Height(RowHeight))) Later(() => _sensors.ShowVerify());
            GUILayout.EndHorizontal();
            EndCard();

            if (_sensors.IsCapturing) DrawCaptureCard();
            else DrawVerifyCard();

            DrawTilesCard();
            DrawFilterCard();
            DrawDisplayCard();
        }

        private void DrawCaptureCard()
        {
            var session = _sensors.Session;
            if (session.IsClearingTable)
            {
                BeginCard("Waiting for an empty table");
                GUILayout.Label("Remove all tiles from the table. The first target appears once it has been empty for a moment.", _label);
                GUILayout.Label(_sensors.TileCount > 0 ? $"{_sensors.TileCount} tile(s) still seen" : $"Empty {session.ClearProgress * 100f:0}%", _muted);
                ProgressBar(session.ClearProgress);
                if (GUILayout.Button("Start again", _button, GUILayout.Height(RowHeight))) Later(() => _sensors.RequestRecapture());
                EndCard();
                return;
            }

            int shown = Mathf.Min(session.CurrentIndex + 1, session.Targets.Count);
            BeginCard($"Target {shown} of {session.Targets.Count}");
            if (session.AwaitingNext)
            {
                GUILayout.Label("Stored. Press Next, then move the tile to the next cross.", _label);
                GUILayout.Label($"({session.CapturedCount} captured)", _muted);
                ProgressBar(1f);
            }
            else
            {
                GUILayout.Label("Put any tile on the yellow cross and let go.", _label);
                GUILayout.Label((session.HasMarker ? $"Hold still {session.Progress * 100f:0}%" : "No tile seen yet") + $"   ({session.CapturedCount} captured)", _muted);
                ProgressBar(session.Progress);
            }
            if (session.HasFit)
                GUILayout.Label($"Fit so far: avg error {session.Report.RmsError * CmPerUv:0.0} cm, worst {session.Report.MaxError * CmPerUv:0.0} cm", _muted);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Next (Enter)", session.AwaitingNext ? _primary : _button, GUILayout.Height(RowHeight))) Later(() => _sensors.NextTarget());
            if (GUILayout.Button("Capture now (Space)", session.AwaitingNext ? _button : _primary, GUILayout.Height(RowHeight))) Later(() => _sensors.CaptureNow());
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Back (Backspace)", _button, GUILayout.Height(RowHeight))) Later(() => _sensors.BackTarget());
            if (GUILayout.Button("Skip (X)", _button, GUILayout.Height(RowHeight))) Later(() => _sensors.SkipTarget());
            GUILayout.EndHorizontal();
            if (GUILayout.Button("Start again", _button, GUILayout.Height(RowHeight))) Later(() => _sensors.RequestRecapture());
            EndCard();
        }

        private void DrawVerifyCard()
        {
            BeginCard("Check the fit");
            if (_sensors.IsFitted)
            {
                var saved = _sensors.Saved;
                GUILayout.Label("Slide a tile around. The pink square should stay under it.", _label);
                GUILayout.Label($"{saved.pointCount} points, avg error {saved.rmsError * CmPerUv:0.0} cm, lens {saved.mapping.k1:0.000}", _muted);
                if (saved.projectorMovedSinceFit)
                {
                    var previous = GUI.contentColor;
                    GUI.contentColor = Warning;
                    GUILayout.Label("The projector moved after this fit. Check it, then keep it or capture again.", _muted);
                    GUI.contentColor = previous;
                    if (GUILayout.Button("Still lines up", _button, GUILayout.Height(RowHeight))) Later(() => _sensors.MarkStillAligned());
                }
            }
            else
            {
                GUILayout.Label("This camera has no fit yet, so tiles assume the camera sees exactly the projected frame.", _label);
            }
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(_sensors.IsFitted ? "Capture again" : "Start capture", _primary, GUILayout.Height(RowHeight))) Later(() => _sensors.StartCapture());
            if (_sensors.IsFitted && GUILayout.Button("Clear fit", _danger, GUILayout.Height(RowHeight))) Later(() => _sensors.RequestClearFit());
            GUILayout.EndHorizontal();
            EndCard();
        }

        private void DrawTilesCard()
        {
            BeginCard("Tiles on the table");
            GUILayout.Label(_sensors.TileListText(), _label);
            EndCard();
        }

        private void DrawFilterCard()
        {
            var filter = _sensors.Filter;
            BeginCard("Jitter filter");
            bool on = GUILayout.Toggle(filter.enabled, filter.enabled ? "  Filter on" : "  Filter off", Choice(filter.enabled), GUILayout.Height(RowHeight));
            if (on != filter.enabled) _sensors.SetFilterEnabled(on);

            float radius = Slider("Ignore movement under", filter.radiusCm, 0f, SensorFilterSettings.MaxRadiusCm, "{0:0.0} cm");
            if (!Mathf.Approximately(radius, filter.radiusCm)) _sensors.SetRadius(radius);
            float smoothing = Slider("Smoothing when it moves", filter.smoothingSeconds, 0f, SensorFilterSettings.MaxSmoothingSeconds, "{0:0.00} s");
            if (!Mathf.Approximately(smoothing, filter.smoothingSeconds)) _sensors.SetSmoothing(smoothing);

            GUILayout.Label(_sensors.HasJitterReading ? $"Measured jitter: {_sensors.JitterCm:0.00} cm" : "Measured jitter: leave a tile still to measure", _muted);
            if (GUILayout.Button("Set radius from measured jitter", _button, GUILayout.Height(RowHeight))) Later(() => _sensors.SetRadiusFromJitter());
            EndCard();
        }

        private void DrawDisplayCard()
        {
            BeginCard("Test pattern");
            GUILayout.BeginHorizontal();
            foreach (var pattern in ProjectionCalibrationController.Patterns)
                if (GUILayout.Button(PatternName(pattern), Choice(_controller.Pattern == pattern), GUILayout.Height(RowHeight))) Later(() => _controller.Pattern = pattern);
            bool tint = GUILayout.Toggle(_controller.Tint, "Tint", Choice(_controller.Tint), GUILayout.Height(RowHeight));
            if (tint != _controller.Tint) _controller.Tint = tint;
            GUILayout.EndHorizontal();
            EndCard();
        }

        private static string PatternName(TestPattern pattern)
        {
            switch (pattern)
            {
                case TestPattern.Overlay: return "Game+";
                case TestPattern.PatternOnly: return "Grid";
                case TestPattern.White: return "White";
                case TestPattern.Black: return "Black";
                default: return "Off";
            }
        }

        private void DrawSetupTab()
        {
            GUILayout.Label("Each setup keeps its own calibration. Switching restarts the game.", _muted);
            GUILayout.Space(6f);
            foreach (var setup in TableSetups.All)
            {
                bool active = setup == _rig.ActiveSetup;
                BeginCard(setup.DisplayName);
                GUILayout.Label(setup.Description, _muted);
                GUI.enabled = !active;
                if (GUILayout.Button(active ? "In use" : "Switch to this setup", active ? _buttonActive : _primary, GUILayout.Height(RowHeight))) Later(() => _controller.ChooseSetup(setup));
                GUI.enabled = true;
                EndCard();
            }
            GUILayout.Label($"Started as: {_rig.SetupSource}", _muted);
        }

        private void DrawFooter()
        {
            GUILayout.Space(2f);
            // Fixed height: a message appearing must not move the buttons under the mouse.
            var previous = GUI.contentColor;
            GUI.contentColor = Warning;
            GUILayout.Label(_controller.Status ?? string.Empty, _muted, GUILayout.Height(StatusHeight));
            GUI.contentColor = previous;
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Save", _button, GUILayout.Height(RowHeight))) Later(() => _controller.Save());
            if (GUILayout.Button("Save and close", _primary, GUILayout.Height(RowHeight))) Later(() => _controller.SaveAndClose());
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(_controller.HasUnsavedChanges ? "Discard and close" : "Close", _controller.HasUnsavedChanges ? _danger : _button, GUILayout.Height(RowHeight))) Later(() => _controller.DiscardAndClose());
            if (GUILayout.Button("Previous save", _button, GUILayout.Height(RowHeight))) Later(() => _controller.RequestRestorePrevious());
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
            GUILayout.Space(4f);
        }

        private float Slider(string label, float value, float min, float max, string format)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, _labelLine);
            GUILayout.FlexibleSpace();
            GUILayout.Label(string.Format(format, value), _labelLine);
            GUILayout.EndHorizontal();

            Rect r = GUILayoutUtility.GetRect(10f, 20f, GUILayout.ExpandWidth(true));
            if (Event.current.type == EventType.Repaint)
            {
                float fill = Mathf.InverseLerp(min, max, value);
                var track = new Rect(r.x, r.center.y - 3f, r.width, 6f);
                DrawRect(track, Control);
                DrawRect(new Rect(track.x, track.y, track.width * fill, track.height), Accent);
            }
            return GUI.HorizontalSlider(r, value, min, max, _sliderTrack, _sliderThumb);
        }

        private void ProgressBar(float value)
        {
            Rect r = GUILayoutUtility.GetRect(10f, 8f, GUILayout.ExpandWidth(true));
            if (Event.current.type != EventType.Repaint) return;
            DrawRect(r, Control);
            DrawRect(new Rect(r.x, r.y, r.width * Mathf.Clamp01(value), r.height), Good);
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
            _panel = new GUIStyle { normal = { background = Solid(Panel) }, padding = new RectOffset(10, 10, 8, 8) };
            _card = new GUIStyle { normal = { background = Solid(Card) }, padding = new RectOffset(8, 8, 6, 7) };

            _title = Text(16, FontStyle.Bold, Color.white);
            _section = Text(12, FontStyle.Bold, Accent);
            _section.margin = new RectOffset(0, 0, 0, 3);
            _label = Text(13, FontStyle.Normal, Color.white);
            _muted = Text(11, FontStyle.Normal, Muted);
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
            _sliderTrack = new GUIStyle { fixedHeight = 20f };
            _sliderThumb = new GUIStyle
            {
                normal = { background = thumb },
                hover = { background = thumb },
                active = { background = Solid(Accent) },
                fixedWidth = 10f,
                fixedHeight = 18f,
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
                fontSize = 12,
                fontStyle = style,
                margin = new RectOffset(2, 2, 2, 2),
                padding = new RectOffset(4, 4, 2, 2),
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
