#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using CityTwin.Calibration;

namespace CityTwin.Tests
{
    public class ProjectionCalibrationEditorTests
    {
        private static readonly Vector2 Px1080 = new Vector2(1920f, 1080f);

        private static (ProjectionCalibrationEditor editor, List<ProjectorCalibration> list) NewEditor()
        {
            var list = ProjectorCalibration.DefaultTwoProjectorLayout();
            return (new ProjectionCalibrationEditor(list), list);
        }

        [Test]
        public void Nudge_MovesSelectedPointInPixels_AndRaisesChange()
        {
            var (ed, list) = NewEditor();
            int changed = -99;
            ed.GeometryChanged += i => changed = i;
            ed.NextHandle(1); // point 2
            Vector2 before = list[0].projectorPoints[1];

            ed.Nudge(new Vector2(10f, -5f), Px1080);

            Vector2 after = list[0].projectorPoints[1];
            Assert.That(after.x - before.x, Is.EqualTo(10f / 1920f).Within(1e-6f));
            Assert.That(after.y - before.y, Is.EqualTo(-5f / 1080f).Within(1e-6f));
            Assert.That(changed, Is.EqualTo(0));
            Assert.That(ed.IsDirty, Is.True);
        }

        [Test]
        public void Selection_Wraps()
        {
            var (ed, _) = NewEditor();
            ed.NextProjector(-1);
            Assert.That(ed.SelectedProjector, Is.EqualTo(1));
            ed.NextProjector(1);
            Assert.That(ed.SelectedProjector, Is.EqualTo(0));
            ed.NextHandle(-1);
            Assert.That(ed.SelectedHandle, Is.EqualTo(3));
        }

        [Test]
        public void Undo_RestoresInPlace()
        {
            var (ed, list) = NewEditor();
            var top = list[0];
            Vector2 original = top.projectorPoints[0];
            ed.Nudge(new Vector2(50f, 50f), Px1080);
            ed.Nudge(new Vector2(50f, 50f), Px1080);

            Assert.That(ed.Undo(), Is.True);
            Assert.That(ed.Undo(), Is.True);
            Assert.That(ed.Undo(), Is.False);
            Assert.That(list[0], Is.SameAs(top), "outputs hold references, so undo must not replace objects");
            Assert.That(Vector2.Distance(top.projectorPoints[0], original), Is.LessThan(1e-6f));
        }

        [Test]
        public void Drag_IsOneUndoStep()
        {
            var (ed, list) = NewEditor();
            Vector2 original = list[0].projectorPoints[0];
            ed.BeginDrag();
            for (int i = 1; i <= 10; i++) ed.DragSelectedTo(new Vector2(0.01f * i, 0.02f * i));
            ed.EndDrag();

            Assert.That(Vector2.Distance(list[0].projectorPoints[0], new Vector2(0.1f, 0.2f)), Is.LessThan(1e-6f));
            Assert.That(ed.UndoDepth, Is.EqualTo(1));
            ed.Undo();
            Assert.That(list[0].projectorPoints[0], Is.EqualTo(original));
        }

        [Test]
        public void ResetSelected_ReturnsToOpenedPosition()
        {
            var (ed, list) = NewEditor();
            Vector2 original = list[0].projectorPoints[0];
            ed.Nudge(new Vector2(30f, 0f), Px1080);
            ed.ResetSelected();
            Assert.That(list[0].projectorPoints[0], Is.EqualTo(original));
        }

        [Test]
        public void MarkSaved_ResetBaselineAndClearsDirty()
        {
            var (ed, list) = NewEditor();
            ed.Nudge(new Vector2(30f, 0f), Px1080);
            Vector2 saved = list[0].projectorPoints[0];
            ed.MarkSaved();
            Assert.That(ed.IsDirty, Is.False);
            ed.Nudge(new Vector2(30f, 0f), Px1080);
            ed.ResetSelected();
            Assert.That(list[0].projectorPoints[0], Is.EqualTo(saved), "reset goes back to the last save, not the original open");
        }

        [Test]
        public void MeshMode_CreatesFlatGrid_AndNudgesOffsets()
        {
            var (ed, list) = NewEditor();
            ed.ToggleMeshMode();
            var top = list[0];
            Assert.That(ed.Target, Is.EqualTo(CalibrationEditTarget.MeshNodes));
            Assert.That(top.HasMeshWarp, Is.True);
            Assert.That(top.meshOffsets.Length, Is.EqualTo(25));

            ed.SelectHandle(12); // centre node
            ed.Nudge(new Vector2(0f, 10.8f), Px1080);
            Assert.That(top.meshOffsets[12].y, Is.EqualTo(0.01f).Within(1e-6f));

            // Drag places the node exactly; the offset is relative to its rest grid position.
            ed.DragSelectedTo(new Vector2(0.52f, 0.47f));
            Assert.That(Vector2.Distance(ProjectionMath.MeshNodeProjectorUv(top, 12), new Vector2(0.52f, 0.47f)), Is.LessThan(1e-5f));
            Assert.That(Vector2.Distance(top.meshOffsets[12], new Vector2(0.02f, -0.03f)), Is.LessThan(1e-5f));

            ed.ClearMesh();
            Assert.That(top.meshOffsets[12], Is.EqualTo(Vector2.zero));
            ed.ToggleMeshMode();
            Assert.That(ed.Target, Is.EqualTo(CalibrationEditTarget.ControlPoints));
        }

        [Test]
        public void MeshNodeRestUv_IsFrameGrid()
        {
            var cal = ProjectorCalibration.DefaultTwoProjectorLayout()[0];
            cal.meshColumns = 4; cal.meshRows = 4; cal.meshOffsets = new Vector2[25];
            Assert.That(ProjectionMath.MeshNodeRestUv(cal, 0), Is.EqualTo(Vector2.zero));
            Assert.That(ProjectionMath.MeshNodeRestUv(cal, 24), Is.EqualTo(Vector2.one));
            Assert.That(ProjectionMath.MeshNodeRestUv(cal, 4), Is.EqualTo(new Vector2(1f, 0f)));
            Assert.That(ProjectionMath.MeshNodeRestUv(cal, 12), Is.EqualTo(new Vector2(0.5f, 0.5f)));
        }

        [Test]
        public void MoveSeam_MovesBothCutsTogether_NoGapNoOverlap()
        {
            var (ed, list) = NewEditor();
            ProjectorCalibration a = list[0], b = list[1];
            ed.MoveSeam(0.02f); // into A's side
            Assert.That(ed.SeamShift, Is.EqualTo(0.02f).Within(1e-5f));
            Assert.That(ed.IsDirty, Is.True);

            // Walk across the new seam through the table centre: exactly one projector keeps each point.
            for (int i = 0; i <= 60; i++)
            {
                var uv = new Vector2(0.5f, 0.5f) + a.seamNormal * (-0.3f + 0.6f * i / 60f + 0.0007f);
                int count = (a.KeepsTablePoint(uv) ? 1 : 0) + (b.KeepsTablePoint(uv) ? 1 : 0);
                Assert.That(count, Is.EqualTo(1), $"uv={uv}");
            }
            Assert.That(b.KeepsTablePoint(new Vector2(0.5f, 0.5f) + a.seamNormal * 0.015f), Is.True, "B now reaches past the old centre line");

            ed.SelectProjector(1);
            Assert.That(ed.SeamShift, Is.EqualTo(-0.02f).Within(1e-5f), "measured along B's own normal");
            ed.MoveSeam(0.02f); // into B's side: back to the centre
            Assert.That(a.seamOffset, Is.EqualTo(0f).Within(1e-5f));
            Assert.That(b.seamOffset, Is.EqualTo(0f).Within(1e-5f));
        }

        [Test]
        public void MoveSeam_Clamps()
        {
            var (ed, _) = NewEditor();
            ed.MoveSeam(5f);
            Assert.That(ed.SeamShift, Is.EqualTo(ProjectionCalibrationEditor.MaxSeamShift).Within(1e-5f));
        }

        [Test]
        public void ToggleSeam_AppliesToAllProjectors_AndUndoes()
        {
            var (ed, list) = NewEditor();
            ed.ToggleSeam();
            Assert.That(list[0].seamEnabled, Is.False);
            Assert.That(list[1].seamEnabled, Is.False);
            ed.Undo();
            Assert.That(list[0].seamEnabled && list[1].seamEnabled, Is.True);
        }

        [Test]
        public void Rebind_ClearsUndoAndDirty()
        {
            var (ed, _) = NewEditor();
            ed.Nudge(Vector2.one, Px1080);
            ed.Rebind(ProjectorCalibration.DefaultTwoProjectorLayout());
            Assert.That(ed.IsDirty, Is.False);
            Assert.That(ed.UndoDepth, Is.EqualTo(0));
        }
    }
}
#endif
