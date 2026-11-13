using System;
using System.Collections.Generic;
using UnityEngine;

namespace CityTwin.Calibration
{
    /// <summary>What the arrow keys / mouse move in calibration mode.</summary>
    public enum CalibrationEditTarget
    {
        /// <summary>Corner-pin / homography control points.</summary>
        ControlPoints,
        /// <summary>Fine mesh-warp nodes.</summary>
        MeshNodes,
    }

    /// <summary>
    /// Editing logic for output calibration, independent of input devices and rendering so it can be tested.
    /// Edits the live <see cref="ProjectorCalibration"/> objects in place (the outputs keep their references)
    /// and raises <see cref="GeometryChanged"/> so the caller can rebuild warp meshes.
    /// </summary>
    public class ProjectionCalibrationEditor
    {
        public const int DefaultMeshCells = 4;
        public const float MaxSeamShift = 0.4f;
        private const int MaxUndo = 200;

        private List<ProjectorCalibration> _projectors;
        private readonly List<string[]> _undo = new List<string[]>();
        private string[] _openSnapshot;
        private bool _dragRecorded;

        public int SelectedProjector { get; private set; }
        public int SelectedPoint { get; private set; }
        public int SelectedNode { get; private set; }
        public CalibrationEditTarget Target { get; private set; } = CalibrationEditTarget.ControlPoints;

        /// <summary>
        /// Corner points are edited through proxies: four points half way in (the table frame shrunk to
        /// <see cref="ProxyScale"/> around its centre, so they start on the middle half of the screen). Moving a proxy
        /// solves the corner pin through the four proxies and moves the real (outer) corners with it. The outer corners
        /// stay at the edge of the projection, where they are often off the round table and can't be aimed at.
        /// </summary>
        public bool UseCornerProxies { get; set; }

        /// <summary>Proxy frame size as a fraction of the table frame.</summary>
        public const float ProxyScale = 0.5f;

        /// <summary>True when there are edits not yet saved.</summary>
        public bool IsDirty { get; private set; }
        public int UndoDepth => _undo.Count;

        /// <summary>Raised with the projector index whose warp mesh must be rebuilt (-1 = all).</summary>
        public event Action<int> GeometryChanged;

        public ProjectionCalibrationEditor(List<ProjectorCalibration> projectors)
        {
            Rebind(projectors);
        }

        public int ProjectorCount => _projectors.Count;
        public ProjectorCalibration Current => _projectors.Count > 0 ? _projectors[SelectedProjector] : null;
        public IReadOnlyList<ProjectorCalibration> Projectors => _projectors;

        /// <summary>Point at a (re)loaded projector list: clears undo history and the dirty flag.</summary>
        public void Rebind(List<ProjectorCalibration> projectors)
        {
            _projectors = projectors ?? new List<ProjectorCalibration>();
            _undo.Clear();
            _openSnapshot = Snapshot();
            IsDirty = false;
            _dragRecorded = false;
            ClampSelection();
        }

        public void MarkSaved()
        {
            IsDirty = false;
            _openSnapshot = Snapshot();
        }

        // ---------- selection ----------

        public void SelectProjector(int index)
        {
            if (_projectors.Count == 0) return;
            SelectedProjector = Mod(index, _projectors.Count);
            ClampSelection();
        }

        public void NextProjector(int direction) => SelectProjector(SelectedProjector + Math.Sign(direction));

        /// <summary>Cycle control points or mesh nodes, depending on <see cref="Target"/>.</summary>
        public void NextHandle(int direction)
        {
            int count = HandleCount;
            if (count == 0) return;
            if (Target == CalibrationEditTarget.ControlPoints) SelectedPoint = Mod(SelectedPoint + Math.Sign(direction), count);
            else SelectedNode = Mod(SelectedNode + Math.Sign(direction), count);
        }

        public void SelectHandle(int index)
        {
            int count = HandleCount;
            if (count == 0) return;
            if (Target == CalibrationEditTarget.ControlPoints) SelectedPoint = Mathf.Clamp(index, 0, count - 1);
            else SelectedNode = Mathf.Clamp(index, 0, count - 1);
        }

        public int HandleCount
        {
            get
            {
                var c = Current;
                if (c == null) return 0;
                if (Target == CalibrationEditTarget.ControlPoints) return c.projectorPoints?.Length ?? 0;
                return c.HasMeshWarp ? c.meshOffsets.Length : 0;
            }
        }

        public int SelectedHandle => Target == CalibrationEditTarget.ControlPoints ? SelectedPoint : SelectedNode;

        /// <summary>Switch between control points and mesh nodes. Entering mesh mode the first time creates a flat 4x4 mesh.</summary>
        public void ToggleMeshMode()
        {
            var c = Current;
            if (c == null) return;
            if (Target == CalibrationEditTarget.MeshNodes)
            {
                Target = CalibrationEditTarget.ControlPoints;
                return;
            }
            if (!c.HasMeshWarp)
            {
                RecordUndo();
                c.meshColumns = DefaultMeshCells;
                c.meshRows = DefaultMeshCells;
                c.meshOffsets = new Vector2[(DefaultMeshCells + 1) * (DefaultMeshCells + 1)];
                Changed(SelectedProjector);
            }
            Target = CalibrationEditTarget.MeshNodes;
            SelectedNode = Mathf.Clamp(SelectedNode, 0, c.meshOffsets.Length - 1);
        }

        // ---------- geometry ----------

        /// <summary>Move the selected handle by a number of output pixels (sub-pixel allowed).</summary>
        public void Nudge(Vector2 pixels, Vector2 outputPixelSize)
        {
            var c = Current;
            if (c == null || HandleCount == 0) return;
            var delta = new Vector2(pixels.x / Mathf.Max(1f, outputPixelSize.x), pixels.y / Mathf.Max(1f, outputPixelSize.y));
            RecordUndo();
            if (Target == CalibrationEditTarget.MeshNodes) c.meshOffsets[SelectedNode] += delta;
            else if (UseCornerProxies && TryGetCornerProxies(c, out var proxies))
            {
                proxies[SelectedPoint] += delta;
                ApplyCornerProxies(c, proxies);
            }
            else c.projectorPoints[SelectedPoint] += delta;
            Changed(SelectedProjector);
        }

        /// <summary>Call once when a mouse drag starts; the whole drag is one undo step.</summary>
        public void BeginDrag()
        {
            RecordUndo();
            _dragRecorded = true;
        }

        public void EndDrag() => _dragRecorded = false;

        /// <summary>Drag the selected handle so it sits at a projector uv. Mesh nodes store the offset from their rest grid position.</summary>
        public void DragSelectedTo(Vector2 projectorUv)
        {
            var c = Current;
            if (c == null || HandleCount == 0) return;
            if (!_dragRecorded) BeginDrag();
            if (Target == CalibrationEditTarget.MeshNodes)
                c.meshOffsets[SelectedNode] = projectorUv - ProjectionMath.MeshNodeRestUv(c, SelectedNode);
            else if (UseCornerProxies && TryGetCornerProxies(c, out var proxies))
            {
                proxies[SelectedPoint] = projectorUv;
                ApplyCornerProxies(c, proxies);
            }
            else
                c.projectorPoints[SelectedPoint] = projectorUv;
            Changed(SelectedProjector);
        }

        /// <summary>Selected control point back to where it was when calibration opened (or last save); mesh node back to zero offset.</summary>
        public void ResetSelected()
        {
            var c = Current;
            if (c == null || HandleCount == 0) return;
            RecordUndo();
            if (Target == CalibrationEditTarget.MeshNodes)
            {
                c.meshOffsets[SelectedNode] = Vector2.zero;
            }
            else
            {
                var original = new ProjectorCalibration();
                JsonUtility.FromJsonOverwrite(_openSnapshot[SelectedProjector], original);
                if (UseCornerProxies && TryGetCornerProxies(c, out var proxies) && TryGetCornerProxies(original, out var originalProxies))
                {
                    proxies[SelectedPoint] = originalProxies[SelectedPoint];
                    ApplyCornerProxies(c, proxies);
                }
                else if (original.projectorPoints != null && SelectedPoint < original.projectorPoints.Length)
                    c.projectorPoints[SelectedPoint] = original.projectorPoints[SelectedPoint];
            }
            Changed(SelectedProjector);
        }

        /// <summary>Flatten the selected projector's mesh warp (keeps the grid). False when it has no mesh.</summary>
        public bool ClearMesh()
        {
            var c = Current;
            if (c == null || !c.HasMeshWarp) return false;
            RecordUndo();
            for (int i = 0; i < c.meshOffsets.Length; i++) c.meshOffsets[i] = Vector2.zero;
            Changed(SelectedProjector);
            return true;
        }

        /// <summary>The selected projector back to its default picture. The seam is shared, so it stays.</summary>
        public bool ResetProjectorToDefault(IReadOnlyList<ProjectorCalibration> defaults)
        {
            var c = Current;
            if (c == null || SelectedProjector >= defaults.Count) return false;
            RecordUndo();
            c.ResetGeometryTo(defaults[SelectedProjector]);
            ClampSelection();
            Changed(SelectedProjector);
            return true;
        }

        /// <summary>Every projector and the seam back to their defaults.</summary>
        public bool ResetAllToDefault(IReadOnlyList<ProjectorCalibration> defaults)
        {
            if (_projectors.Count != defaults.Count) return false;
            RecordUndo();
            for (int i = 0; i < _projectors.Count; i++)
            {
                _projectors[i].ResetGeometryTo(defaults[i]);
                _projectors[i].ResetSeamTo(defaults[i]);
            }
            ClampSelection();
            Changed(-1);
            return true;
        }

        // ---------- corner proxies ----------

        /// <summary>The table frame shrunk to <see cref="ProxyScale"/> around its centre (table uv). False without exactly 4 corners.</summary>
        public static bool TryGetProxyTablePoints(ProjectorCalibration c, out Vector2[] inner)
        {
            inner = null;
            if (c?.tablePoints == null || c.tablePoints.Length != 4 || c.projectorPoints == null || c.projectorPoints.Length != 4) return false;
            Vector2 centre = Vector2.zero;
            for (int i = 0; i < 4; i++) centre += c.tablePoints[i];
            centre *= 0.25f;
            inner = new Vector2[4];
            for (int i = 0; i < 4; i++) inner[i] = centre + (c.tablePoints[i] - centre) * ProxyScale;
            return true;
        }

        /// <summary>Where the proxies sit on the projector (projector uv), through the current corner pin.</summary>
        public static bool TryGetCornerProxies(ProjectorCalibration c, out Vector2[] proxies)
        {
            proxies = null;
            if (!TryGetProxyTablePoints(c, out var inner) || !c.TryGetTableToProjector(out var h)) return false;
            proxies = new Vector2[4];
            for (int i = 0; i < 4; i++) proxies[i] = h.Apply(inner[i]);
            return true;
        }

        /// <summary>Move the real corners so the corner pin passes through the proxies. Unchanged when they are degenerate.</summary>
        public static bool ApplyCornerProxies(ProjectorCalibration c, Vector2[] proxies)
        {
            if (!TryGetProxyTablePoints(c, out var inner) || !Homography.TrySolve(inner, proxies, out var h)) return false;
            var outer = new Vector2[4];
            for (int i = 0; i < 4; i++)
            {
                outer[i] = h.Apply(c.tablePoints[i]);
                if (!float.IsFinite(outer[i].x) || !float.IsFinite(outer[i].y)) return false;
            }
            for (int i = 0; i < 4; i++) c.projectorPoints[i] = outer[i];
            return true;
        }

        // ---------- seam ----------

        /// <summary>Seam cut on/off for every projector at once: off shows each whole frame, to check the overlap lines up.</summary>
        public void ToggleSeam()
        {
            var c = Current;
            if (c == null) return;
            RecordUndo();
            bool on = !c.seamEnabled;
            foreach (var p in _projectors)
                if (p != null) p.seamEnabled = on;
            Changed(-1);
        }

        /// <summary>
        /// Slide the seam line by <paramref name="delta"/> table uv along the selected projector's seam normal
        /// (positive = into the selected projector's side). Every projector cut by the same line moves with it,
        /// so they never leave a gap or overlap. Stays within <see cref="MaxSeamShift"/> of the table centre.
        /// </summary>
        public void MoveSeam(float delta)
        {
            var c = Current;
            if (c == null || c.seamNormal.sqrMagnitude < 1e-8f) return;
            Vector2 n = c.seamNormal.normalized;
            float tableCenter = Vector2.Dot(new Vector2(0.5f, 0.5f), n);
            float line = Mathf.Clamp(c.seamOffset / c.seamNormal.magnitude + delta, tableCenter - MaxSeamShift, tableCenter + MaxSeamShift);

            RecordUndo();
            foreach (var p in _projectors)
            {
                if (p == null || p.seamNormal.sqrMagnitude < 1e-8f) continue;
                float alignment = Vector2.Dot(p.seamNormal.normalized, n);
                if (Mathf.Abs(alignment) < 0.9999f) continue; // a different seam
                p.seamOffset = Mathf.Sign(alignment) * line * p.seamNormal.magnitude;
            }
            Changed(-1);
        }

        /// <summary>Seam distance (table uv) from the table centre along the selected projector's normal.</summary>
        public float SeamShift
        {
            get
            {
                var c = Current;
                if (c == null || c.seamNormal.sqrMagnitude < 1e-8f) return 0f;
                Vector2 n = c.seamNormal.normalized;
                return c.seamOffset / c.seamNormal.magnitude - Vector2.Dot(new Vector2(0.5f, 0.5f), n);
            }
        }

        // ---------- undo ----------

        public bool Undo()
        {
            if (_undo.Count == 0) return false;
            var snap = _undo[_undo.Count - 1];
            _undo.RemoveAt(_undo.Count - 1);
            Restore(snap);
            IsDirty = true;
            ClampSelection();
            GeometryChanged?.Invoke(-1);
            return true;
        }

        private void RecordUndo()
        {
            if (_dragRecorded) return; // a drag in progress already recorded its starting state
            _undo.Add(Snapshot());
            if (_undo.Count > MaxUndo) _undo.RemoveAt(0);
        }

        private string[] Snapshot()
        {
            var s = new string[_projectors.Count];
            for (int i = 0; i < s.Length; i++) s[i] = JsonUtility.ToJson(_projectors[i]);
            return s;
        }

        // Overwrite in place: the projector outputs hold references to these objects.
        private void Restore(string[] snap)
        {
            for (int i = 0; i < snap.Length && i < _projectors.Count; i++)
                JsonUtility.FromJsonOverwrite(snap[i], _projectors[i]);
        }

        private void Changed(int projector)
        {
            IsDirty = true;
            GeometryChanged?.Invoke(projector);
        }

        private void ClampSelection()
        {
            SelectedProjector = _projectors.Count == 0 ? 0 : Mathf.Clamp(SelectedProjector, 0, _projectors.Count - 1);
            var c = Current;
            int points = c?.projectorPoints?.Length ?? 0;
            SelectedPoint = points == 0 ? 0 : Mathf.Clamp(SelectedPoint, 0, points - 1);
            if (c == null || !c.HasMeshWarp)
            {
                SelectedNode = 0;
                if (Target == CalibrationEditTarget.MeshNodes) Target = CalibrationEditTarget.ControlPoints;
            }
            else SelectedNode = Mathf.Clamp(SelectedNode, 0, c.meshOffsets.Length - 1);
        }

        private static int Mod(int a, int n) => ((a % n) + n) % n;
    }
}
