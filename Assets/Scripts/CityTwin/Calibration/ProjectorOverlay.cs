using System.Collections.Generic;
using UnityEngine;

namespace CityTwin.Calibration
{
    /// <summary>
    /// Calibration graphics drawn in one projector's own frame (projector pixels, unwarped): frame border,
    /// control-point crosshairs or mesh nodes, and the help/status text. Rendered by the projector's camera
    /// on the output layer, so it works on any display (IMGUI would only reach the main window).
    /// Added to a <see cref="ProjectorOutput"/> by <see cref="ProjectionCalibrationController"/> while calibrating.
    /// </summary>
    public class ProjectorOverlay : MonoBehaviour
    {
        public enum MarkerKind { TargetPending, TargetCurrent, TargetCaptured, TargetSkipped, LiveTile, AreaSelected, AreaShared, TargetStored }

        /// <summary>A graphic in this projector's frame.</summary>
        public struct Marker
        {
            public Vector2 Uv;       // projector uv
            public MarkerKind Kind;
            public float Progress;   // TargetCurrent: hold progress 0..1
        }

        /// <summary>What to draw this frame.</summary>
        public struct State
        {
            public bool IsSelectedProjector;
            public IReadOnlyList<Vector2> Handles;  // projector uv
            public IReadOnlyList<Vector2> OuterPoints; // corner mode: the real corners the proxy handles move (projector uv)
            public int SelectedHandle;
            public bool MeshMode;
            public int MeshColumns;                 // mesh mode: grid shape for connecting lines
            public string HudText;                  // null = no HUD on this projector
            public bool HudFlipped;
            public IReadOnlyList<Marker> Markers;
            public Vector2? HudAvoidUv;             // projector uv the HUD must not cover; overrides the selected handle
        }

        private const float HudLinePx = 26f;
        private const float HudMarginPx = 14f;

        private static readonly Color32 Selected = new Color32(255, 214, 0, 255);
        private static readonly Color32 Normal = new Color32(0, 230, 255, 255);
        private static readonly Color32 Outline = new Color32(0, 0, 0, 200);
        private static readonly Color32 FrameSelected = new Color32(60, 255, 90, 255);
        private static readonly Color32 FrameOther = new Color32(120, 120, 120, 255);
        private static readonly Color32 Connector = new Color32(0, 230, 255, 110);
        private static readonly Color32 Pending = new Color32(190, 190, 190, 255);
        private static readonly Color32 Captured = new Color32(60, 255, 90, 255);
        private static readonly Color32 Skipped = new Color32(255, 70, 70, 255);
        private static readonly Color32 LiveTile = new Color32(255, 40, 220, 255);
        private static readonly Color32 OuterLine = new Color32(190, 190, 190, 90);

        private ProjectorOutput _output;
        private Transform _root;
        private Mesh _mesh;
        private Material _material;
        private Transform _hudRoot;
        private TextMesh _hudText;
        private MeshRenderer _hudTextRenderer;
        private Transform _hudBackground;
        private Font _font;
        private readonly List<TextMesh> _labels = new List<TextMesh>();
        private readonly List<Vector3> _verts = new List<Vector3>();
        private readonly List<Color32> _colors = new List<Color32>();
        private readonly List<int> _tris = new List<int>();
        private string _lastHud;
        private float _pxToLocal = 0.001f;
        private float _aspect = 1f;

        public void Init(ProjectorOutput output, Shader overlayShader, Font font)
        {
            _output = output;
            _font = font;
            int layer = output.Layer;
            _material = new Material(overlayShader) { name = "Calibration Overlay" };

            // Everything lives under one root so removing the component removes all of it.
            _root = CreateChild("Calibration Overlay", transform, layer, 0f).transform;
            var meshGo = CreateChild("Overlay Mesh", _root, layer, 0.9f);
            _mesh = new Mesh { name = "Calibration Overlay" };
            _mesh.MarkDynamic();
            meshGo.AddComponent<MeshFilter>().sharedMesh = _mesh;
            var mr = meshGo.AddComponent<MeshRenderer>();
            mr.sharedMaterial = _material;
            mr.sortingOrder = 10;

            _hudRoot = CreateChild("HUD", _root, layer, 0.8f).transform;

            var bg = CreateChild("HUD Background", _hudRoot, layer, 0.01f);
            var quad = new Mesh { name = "HUD Background" };
            quad.vertices = new[] { new Vector3(-0.5f, -0.5f), new Vector3(0.5f, -0.5f), new Vector3(0.5f, 0.5f), new Vector3(-0.5f, 0.5f) };
            quad.colors32 = new[] { Outline, Outline, Outline, Outline };
            quad.triangles = new[] { 0, 2, 1, 0, 3, 2 };
            bg.AddComponent<MeshFilter>().sharedMesh = quad;
            var bgr = bg.AddComponent<MeshRenderer>();
            bgr.sharedMaterial = _material;
            bgr.sortingOrder = 15;
            _hudBackground = bg.transform;

            _hudText = CreateText("HUD Text", _hudRoot, layer, TextAnchor.MiddleCenter, 20);
            _hudTextRenderer = _hudText.GetComponent<MeshRenderer>();
        }

        public void Draw(State s)
        {
            if (_output == null || _output.OutputCamera == null) return;
            Vector2 px = _output.PixelSize;
            _aspect = px.x / Mathf.Max(1f, px.y);
            _pxToLocal = 1f / Mathf.Max(1f, px.y);

            _verts.Clear(); _colors.Clear(); _tris.Clear();

            // Frame border: shows each projector's extent and which one is selected.
            float t = (s.IsSelectedProjector ? 6f : 3f) * _pxToLocal;
            Color32 frame = s.IsSelectedProjector ? FrameSelected : FrameOther;
            float hw = _aspect * 0.5f, hh = 0.5f;
            AddRect(new Vector2(0f, hh - t * 0.5f), new Vector2(hw, t * 0.5f), frame);
            AddRect(new Vector2(0f, -hh + t * 0.5f), new Vector2(hw, t * 0.5f), frame);
            AddRect(new Vector2(-hw + t * 0.5f, 0f), new Vector2(t * 0.5f, hh), frame);
            AddRect(new Vector2(hw - t * 0.5f, 0f), new Vector2(t * 0.5f, hh), frame);

            int labelCount = 0;
            if (s.Handles != null)
            {
                if (s.MeshMode) DrawMeshNodes(s);
                else
                {
                    DrawControlPoints(s);
                    labelCount = s.Handles.Count;
                }
            }
            UpdateLabels(s, labelCount);
            DrawMarkers(s);

            _mesh.Clear();
            _mesh.SetVertices(_verts);
            _mesh.SetColors(_colors);
            _mesh.SetTriangles(_tris, 0);
            _mesh.bounds = new Bounds(Vector3.zero, new Vector3(10000f, 10000f, 10f));

            UpdateHud(s);
        }

        private void DrawControlPoints(State s)
        {
            int n = s.Handles.Count;
            if (s.OuterPoints != null && s.OuterPoints.Count == n)
            {
                // The real corners at the edge of the projection: a dim quad, a small cross each, tied to their proxy.
                for (int i = 0; i < n; i++)
                {
                    Vector2 outer = Local(s.OuterPoints[i]);
                    AddLine(outer, Local(s.OuterPoints[(i + 1) % n]), 2f * _pxToLocal, OuterLine);
                    AddLine(outer, Local(s.Handles[i]), 1.5f * _pxToLocal, OuterLine);
                    AddCrosshair(outer, 20f, 2f, Pending);
                }
            }
            for (int i = 0; i < n; i++)
            {
                // Faint connectors show the corner-pin quad.
                AddLine(Local(s.Handles[i]), Local(s.Handles[(i + 1) % n]), 2f * _pxToLocal, Connector);
            }
            for (int i = 0; i < n; i++)
            {
                bool sel = s.IsSelectedProjector && i == s.SelectedHandle;
                AddCrosshair(Local(s.Handles[i]), sel ? 46f : 32f, sel ? 3f : 2f, sel ? Selected : Normal);
            }
        }

        private void DrawMeshNodes(State s)
        {
            int stride = Mathf.Max(1, s.MeshColumns) + 1;
            for (int i = 0; i < s.Handles.Count; i++)
            {
                Vector2 p = Local(s.Handles[i]);
                if (i % stride != stride - 1 && i + 1 < s.Handles.Count) AddLine(p, Local(s.Handles[i + 1]), 1.5f * _pxToLocal, Connector);
                if (i + stride < s.Handles.Count) AddLine(p, Local(s.Handles[i + stride]), 1.5f * _pxToLocal, Connector);
            }
            for (int i = 0; i < s.Handles.Count; i++)
            {
                bool sel = s.IsSelectedProjector && i == s.SelectedHandle;
                float size = (sel ? 16f : 9f) * _pxToLocal;
                Vector2 p = Local(s.Handles[i]);
                AddRect(p, Vector2.one * (size * 0.5f + 2f * _pxToLocal), Outline);
                AddRect(p, Vector2.one * size * 0.5f, sel ? Selected : Normal);
            }
        }

        private void DrawMarkers(State s)
        {
            if (s.Markers == null) return;
            for (int i = 0; i < s.Markers.Count; i++)
            {
                var m = s.Markers[i];
                Vector2 p = Local(m.Uv);
                switch (m.Kind)
                {
                    case MarkerKind.TargetPending:
                        AddCrosshair(p, 14f, 2f, Pending);
                        break;
                    case MarkerKind.TargetCaptured: // calibrated: stays marked with a green box for the rest of the capture
                        AddCrosshair(p, 20f, 2f, Captured);
                        AddBox(p, 16f, 3f, Captured);
                        break;
                    case MarkerKind.TargetSkipped:
                        AddRect(p, Vector2.one * 5f * _pxToLocal, Skipped);
                        break;
                    case MarkerKind.TargetCurrent:
                        AddCrosshair(p, 36f, 3f, Selected);
                        DrawProgress(p, m.Progress);
                        break;
                    case MarkerKind.TargetStored: // current target, stored, waiting for Next
                        AddCrosshair(p, 36f, 3f, Captured);
                        AddBox(p, 16f, 3f, Captured);
                        break;
                    case MarkerKind.LiveTile:
                        AddRect(p, Vector2.one * 11f * _pxToLocal, Outline);
                        AddRect(p, Vector2.one * 8f * _pxToLocal, LiveTile);
                        break;
                    case MarkerKind.AreaSelected:
                        AddBox(p, 90f, 5f, Selected);
                        break;
                    case MarkerKind.AreaShared:
                        AddBox(p, 60f, 2f, Normal);
                        break;
                }
            }
        }

        private void DrawProgress(Vector2 center, float progress)
        {
            if (progress <= 0f) return;
            float halfWidth = 24f * _pxToLocal, halfHeight = 3f * _pxToLocal;
            Vector2 c = center + new Vector2(0f, -34f * _pxToLocal);
            AddRect(c, new Vector2(halfWidth, halfHeight), Outline);
            float fill = halfWidth * Mathf.Clamp01(progress);
            if (fill > 0f) AddRect(c + new Vector2(fill - halfWidth, 0f), new Vector2(fill, halfHeight), Captured);
        }

        private void UpdateLabels(State s, int count)
        {
            while (_labels.Count < count)
                _labels.Add(CreateText("Label " + _labels.Count, _root, _output.Layer, TextAnchor.LowerLeft, 25));
            for (int i = 0; i < _labels.Count; i++)
            {
                bool on = i < count;
                _labels[i].gameObject.SetActive(on);
                if (!on) continue;
                var tm = _labels[i];
                tm.text = (i + 1).ToString();
                tm.color = s.IsSelectedProjector && i == s.SelectedHandle ? (Color)Selected : (Color)Normal;
                // Put the number on the side facing the frame centre so corner points keep a visible label.
                Vector2 p = Local(s.Handles[i]);
                float ox = p.x > 0f ? -1f : 1f, oy = p.y > 0f ? -1f : 1f;
                tm.anchor = oy > 0f ? (ox > 0f ? TextAnchor.LowerLeft : TextAnchor.LowerRight)
                                    : (ox > 0f ? TextAnchor.UpperLeft : TextAnchor.UpperRight);
                tm.transform.localPosition = new Vector3(p.x + ox * 14f * _pxToLocal, p.y + oy * 14f * _pxToLocal, 0.85f);
                ScaleTextToLineHeight(tm, 26f);
            }
        }

        private void UpdateHud(State s)
        {
            bool show = !string.IsNullOrEmpty(s.HudText);
            _hudRoot.gameObject.SetActive(show);
            if (!show) return;

            _hudRoot.localRotation = Quaternion.Euler(0f, 0f, s.HudFlipped ? 180f : 0f);
            // Keep the text in the half of the frame away from the handle being moved.
            float hudY = 0f;
            if (s.HudAvoidUv.HasValue)
                hudY = s.HudAvoidUv.Value.y > 0.5f ? -0.25f : 0.25f;
            else if (s.Handles != null && s.SelectedHandle >= 0 && s.SelectedHandle < s.Handles.Count)
                hudY = s.Handles[s.SelectedHandle].y > 0.5f ? -0.25f : 0.25f;
            _hudRoot.localPosition = new Vector3(0f, hudY, 0.8f);
            if (_hudText.text != s.HudText || _lastHud == null)
            {
                _hudText.text = s.HudText;
                _lastHud = s.HudText;
            }
            ScaleTextToLineHeight(_hudText, HudLinePx);

            // Background sized to the text (TextMesh bounds are only known after layout).
            Bounds b = _hudTextRenderer.bounds;
            Vector3 size = _hudRoot.InverseTransformVector(b.size);
            float margin = HudMarginPx * _pxToLocal;
            _hudBackground.localScale = new Vector3(Mathf.Abs(size.x) + 2f * margin, Mathf.Abs(size.y) + 2f * margin, 1f);
            _hudBackground.localPosition = new Vector3(0f, 0f, 0.01f);
        }

        // TextMesh has no pixel size: measure the laid-out height and rescale to the wanted line height.
        private void ScaleTextToLineHeight(TextMesh tm, float linePx)
        {
            tm.transform.localScale = Vector3.one;
            var r = tm.GetComponent<MeshRenderer>();
            float h = tm.transform.parent.InverseTransformVector(r.bounds.size).y;
            int lines = Mathf.Max(1, tm.text.Split('\n').Length);
            if (h <= 1e-6f) return;
            float scale = linePx * _pxToLocal * lines / Mathf.Abs(h);
            tm.transform.localScale = Vector3.one * scale;
        }

        private Vector2 Local(Vector2 projectorUv)
        {
            Vector3 v = ProjectionMath.ProjectorUvToLocal(projectorUv, _aspect);
            return new Vector2(v.x, v.y);
        }

        private void AddCrosshair(Vector2 c, float lengthPx, float thickPx, Color32 color)
        {
            float l = lengthPx * 0.5f * _pxToLocal, th = thickPx * 0.5f * _pxToLocal, o = 2f * _pxToLocal;
            // Dark outline first so the cross reads on both bright and dark content.
            AddRect(c, new Vector2(l + o, th + o), Outline);
            AddRect(c, new Vector2(th + o, l + o), Outline);
            AddRect(c, new Vector2(l, th), color);
            AddRect(c, new Vector2(th, l), color);
            float box = lengthPx * 0.22f * _pxToLocal;
            AddRect(c + new Vector2(0f, box), new Vector2(box, th), color);
            AddRect(c - new Vector2(0f, box), new Vector2(box, th), color);
            AddRect(c + new Vector2(box, 0f), new Vector2(th, box), color);
            AddRect(c - new Vector2(box, 0f), new Vector2(th, box), color);
        }

        private void AddBox(Vector2 c, float halfPx, float thickPx, Color32 color)
        {
            float h = halfPx * _pxToLocal, th = thickPx * 0.5f * _pxToLocal;
            AddRect(c + new Vector2(0f, h), new Vector2(h + th, th), color);
            AddRect(c - new Vector2(0f, h), new Vector2(h + th, th), color);
            AddRect(c + new Vector2(h, 0f), new Vector2(th, h + th), color);
            AddRect(c - new Vector2(h, 0f), new Vector2(th, h + th), color);
        }

        private void AddRect(Vector2 center, Vector2 half, Color32 color)
        {
            int i = _verts.Count;
            _verts.Add(new Vector3(center.x - half.x, center.y - half.y));
            _verts.Add(new Vector3(center.x + half.x, center.y - half.y));
            _verts.Add(new Vector3(center.x + half.x, center.y + half.y));
            _verts.Add(new Vector3(center.x - half.x, center.y + half.y));
            for (int k = 0; k < 4; k++) _colors.Add(color);
            _tris.Add(i); _tris.Add(i + 2); _tris.Add(i + 1);
            _tris.Add(i); _tris.Add(i + 3); _tris.Add(i + 2);
        }

        private void AddLine(Vector2 a, Vector2 b, float thickness, Color32 color)
        {
            Vector2 d = b - a;
            if (d.sqrMagnitude < 1e-12f) return;
            Vector2 n = new Vector2(-d.y, d.x).normalized * (thickness * 0.5f);
            int i = _verts.Count;
            _verts.Add(a - n); _verts.Add(a + n); _verts.Add(b + n); _verts.Add(b - n);
            for (int k = 0; k < 4; k++) _colors.Add(color);
            _tris.Add(i); _tris.Add(i + 1); _tris.Add(i + 2);
            _tris.Add(i); _tris.Add(i + 2); _tris.Add(i + 3);
        }

        private static GameObject CreateChild(string name, Transform parent, int layer, float z)
        {
            var go = new GameObject(name);
            go.layer = layer;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = new Vector3(0f, 0f, z);
            return go;
        }

        private TextMesh CreateText(string name, Transform parent, int layer, TextAnchor anchor, int sortingOrder)
        {
            var go = CreateChild(name, parent, layer, 0f);
            var tm = go.AddComponent<TextMesh>();
            tm.font = _font;
            tm.fontSize = 48;
            tm.characterSize = 0.1f;
            tm.anchor = anchor;
            tm.alignment = TextAlignment.Left;
            tm.color = Color.white;
            var r = go.GetComponent<MeshRenderer>();
            r.sharedMaterial = _font.material;
            r.sortingOrder = sortingOrder;
            return tm;
        }

        private void OnDestroy()
        {
            if (_mesh != null) Destroy(_mesh);
            if (_material != null) Destroy(_material);
            if (_hudBackground != null) Destroy(_hudBackground.GetComponent<MeshFilter>().sharedMesh);
            if (_root != null) Destroy(_root.gameObject);
        }
    }
}
