#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using CityTwin.Calibration;

namespace CityTwin.Tests
{
    public class PlayAreaLayoutTests
    {
        private static readonly Vector2 Bottom = new Vector2(0.5f, 0.2f);
        private static readonly Vector2 Top = new Vector2(0.5f, 0.8f);
        private static readonly Vector2 Left = new Vector2(0.2f, 0.5f);
        private static readonly Vector2 Right = new Vector2(0.8f, 0.5f);

        [Test]
        public void SideOf_NamesTheSide()
        {
            Assert.AreEqual("bottom", PlayAreaLayout.SideOf(Bottom));
            Assert.AreEqual("top", PlayAreaLayout.SideOf(Top));
            Assert.AreEqual("left", PlayAreaLayout.SideOf(Left));
            Assert.AreEqual("right", PlayAreaLayout.SideOf(Right));
        }

        [Test]
        public void TwoProjectors_PairTopWithLeftAndBottomWithRight()
        {
            var projectors = ProjectorCalibration.DefaultTwoProjectorLayout();
            Assert.AreEqual(0, PlayAreaLayout.ProjectorFor(Top, projectors));
            Assert.AreEqual(0, PlayAreaLayout.ProjectorFor(Left, projectors));
            Assert.AreEqual(1, PlayAreaLayout.ProjectorFor(Bottom, projectors));
            Assert.AreEqual(1, PlayAreaLayout.ProjectorFor(Right, projectors));
        }

        [Test]
        public void Pairing_SurvivesTheSeamCutBeingOff()
        {
            var projectors = ProjectorCalibration.DefaultTwoProjectorLayout();
            foreach (var p in projectors) p.seamEnabled = false;
            Assert.AreEqual(0, PlayAreaLayout.ProjectorFor(Left, projectors));
            Assert.AreEqual(1, PlayAreaLayout.ProjectorFor(Right, projectors));
        }

        [Test]
        public void SingleProjector_LightsEverything()
        {
            Assert.AreEqual(0, PlayAreaLayout.ProjectorFor(Top, ProjectorCalibration.DefaultSingleProjectorLayout()));
        }

        [Test]
        public void Geometry_MovingAPointCounts()
        {
            var p = ProjectorCalibration.DefaultTwoProjectorLayout()[0];
            var saved = new ProjectorGeometry(p);
            Assert.IsFalse(saved.DiffersFrom(p));
            p.projectorPoints[2] += new Vector2(0.01f, 0f);
            Assert.IsTrue(saved.DiffersFrom(p));
        }

        [Test]
        public void Geometry_SeamAndFlatMeshDoNotCount()
        {
            var p = ProjectorCalibration.DefaultTwoProjectorLayout()[0];
            var saved = new ProjectorGeometry(p);
            p.seamOffset += 0.05f;
            p.seamEnabled = false;
            p.meshOffsets = new Vector2[25];
            Assert.IsFalse(saved.DiffersFrom(p));
            p.meshOffsets[6] = new Vector2(0.01f, 0f);
            Assert.IsTrue(saved.DiffersFrom(p));
        }

        [Test]
        public void ResetGeometry_RestoresPointsAndRemovesMeshButKeepsSeamAndOutput()
        {
            var defaults = ProjectorCalibration.DefaultTwoProjectorLayout()[0];
            var p = ProjectorCalibration.DefaultTwoProjectorLayout()[0];
            p.projectorPoints[1] += new Vector2(0.1f, 0.1f);
            p.meshColumns = p.meshRows = 4;
            p.meshOffsets = new Vector2[25];
            p.meshOffsets[3] = Vector2.one;
            p.seamOffset = 0.05f;
            p.displayIndex = 1;

            p.ResetGeometryTo(defaults);

            Assert.IsFalse(new ProjectorGeometry(defaults).DiffersFrom(p));
            Assert.IsFalse(p.HasMeshWarp);
            Assert.AreEqual(0.05f, p.seamOffset);
            Assert.AreEqual(1, p.displayIndex);

            p.projectorPoints[0] = Vector2.one;
            Assert.AreNotEqual(Vector2.one, defaults.projectorPoints[0]);
        }

        [Test]
        public void ClearFit_ClearsTheCheckMark()
        {
            var sensor = new SensorCalibration { pointCount = 9, projectorMovedSinceFit = true };
            sensor.ClearFit();
            Assert.IsFalse(sensor.projectorMovedSinceFit);
        }
    }

    public class ProjectorResetEditorTests
    {
        private static ProjectionCalibrationEditor MakeEditor(out List<ProjectorCalibration> projectors, out List<int> changes)
        {
            projectors = ProjectorCalibration.DefaultSingleProjectorLayout();
            var editor = new ProjectionCalibrationEditor(projectors);
            var seen = new List<int>();
            editor.GeometryChanged += seen.Add;
            changes = seen;
            return editor;
        }

        [Test]
        public void Flatten_ZeroesTheMeshAndRedraws()
        {
            var editor = MakeEditor(out var projectors, out var changes);
            editor.ToggleMeshMode();
            projectors[0].meshOffsets[6] = new Vector2(0.1f, 0f);
            changes.Clear();

            Assert.IsTrue(editor.ClearMesh());
            Assert.AreEqual(Vector2.zero, projectors[0].meshOffsets[6]);
            Assert.AreEqual(1, changes.Count);
        }

        [Test]
        public void Flatten_WithoutAMesh_SaysSo()
        {
            var editor = MakeEditor(out _, out var changes);
            Assert.IsFalse(editor.ClearMesh());
            Assert.AreEqual(0, changes.Count);
        }

        [Test]
        public void ResetProjector_FromMeshMode_RestoresAndUndoes()
        {
            var editor = MakeEditor(out var projectors, out var changes);
            Vector2 original = projectors[0].projectorPoints[1];
            editor.Nudge(new Vector2(50f, 0f), new Vector2(1000f, 1000f));
            Vector2 moved = projectors[0].projectorPoints[0];
            editor.ToggleMeshMode();
            changes.Clear();

            Assert.IsTrue(editor.ResetProjectorToDefault(ProjectorCalibration.DefaultSingleProjectorLayout()));
            Assert.AreEqual(Vector2.zero, projectors[0].projectorPoints[0]);
            Assert.AreEqual(original, projectors[0].projectorPoints[1]);
            Assert.IsFalse(projectors[0].HasMeshWarp);
            Assert.AreEqual(CalibrationEditTarget.ControlPoints, editor.Target);
            Assert.AreEqual(1, changes.Count);

            Assert.IsTrue(editor.Undo());
            Assert.AreEqual(moved, projectors[0].projectorPoints[0]);
            Assert.IsTrue(projectors[0].HasMeshWarp);
        }
    }

    public class SensorInputFilterTests
    {
        private const float Cm = 1f / SensorInputFilter.TableWidthCm;
        private const float FrameSeconds = 1f / 30f;

        private static SensorInputFilter MakeFilter(float radiusCm = 3f, float smoothing = 0.06f, int confirm = 2)
        {
            return new SensorInputFilter
            {
                Settings = new SensorFilterSettings { radiusCm = radiusCm, smoothingSeconds = smoothing, confirmSamples = confirm },
            };
        }

        [Test]
        public void JitterInsideRadius_DoesNotMoveOutput()
        {
            var f = MakeFilter();
            Vector2 start = new Vector2(0.5f, 0.5f);
            f.Push(start, 0f);
            float[] jitter = { 2f, -2.5f, 1f, -3f, 2.9f, 0f };
            for (int i = 0; i < jitter.Length; i++)
                Assert.AreEqual(start, f.Push(start + new Vector2(jitter[i] * Cm, 0f), (i + 1) * FrameSeconds));
        }

        [Test]
        public void RealMove_IsPassedAfterConfirmAndCatchesUpFully()
        {
            var f = MakeFilter();
            Vector2 start = new Vector2(0.5f, 0.5f), end = start + new Vector2(4f * Cm, 0f);
            f.Push(start, 0f);

            Assert.AreEqual(start, f.Push(end, FrameSeconds), "first sample outside only arms the move");
            f.Push(end, 2f * FrameSeconds);
            Assert.IsTrue(f.IsMoving);

            float t = 2f * FrameSeconds;
            for (int i = 0; i < 60; i++) { t += 1f / 60f; f.Advance(t); }
            Assert.Less(SensorInputFilter.DistanceCm(f.Output, end), 0.05f);
            Assert.IsFalse(f.IsMoving, "rests again once caught up");
        }

        [Test]
        public void SingleFrameSpike_IsRejected()
        {
            var f = MakeFilter();
            Vector2 start = new Vector2(0.5f, 0.5f);
            f.Push(start, 0f);
            f.Push(start + new Vector2(20f * Cm, 0f), FrameSeconds);
            Assert.AreEqual(start, f.Push(start, 2f * FrameSeconds));
            Assert.IsFalse(f.IsMoving);
        }

        [Test]
        public void Advance_FinishesGlideWithoutNewSamples()
        {
            var f = MakeFilter(radiusCm: 1f, smoothing: 0.1f, confirm: 1);
            Vector2 start = new Vector2(0.2f, 0.2f), end = new Vector2(0.4f, 0.2f);
            f.Push(start, 0f);
            f.Push(end, 0.1f);
            Assert.Less(f.Output.x, end.x);

            float t = 0.1f;
            for (int i = 0; i < 120; i++) { t += 1f / 60f; f.Advance(t); }
            Assert.That(f.Output.x, Is.EqualTo(end.x).Within(1f * Cm));
        }

        [Test]
        public void ZeroSmoothing_JumpsStraightToTarget()
        {
            var f = MakeFilter(radiusCm: 1f, smoothing: 0f, confirm: 1);
            f.Push(Vector2.zero, 0f);
            Vector2 end = new Vector2(0.3f, 0f);
            Assert.AreEqual(end, f.Push(end, FrameSeconds));
        }

        [Test]
        public void DisabledOrMissingSettings_PassThrough()
        {
            var f = new SensorInputFilter();
            f.Push(Vector2.zero, 0f);
            Vector2 p = new Vector2(0.001f, 0f);
            Assert.AreEqual(p, f.Push(p, FrameSeconds));

            f = MakeFilter();
            f.Settings.enabled = false;
            f.Push(Vector2.zero, 0f);
            Assert.AreEqual(p, f.Push(p, FrameSeconds));
        }

        [Test]
        public void JitterMeter_ReportsPeakAndForgetsOldSamples()
        {
            var m = new SensorJitterMeter();
            m.Add(new Vector2(0.5f, 0.5f), 0f);
            m.Add(new Vector2(0.5f + 2f * Cm, 0.5f), 0.1f);
            Assert.That(m.PeakCm, Is.EqualTo(1f).Within(0.01f));

            m.Add(new Vector2(0.5f, 0.5f), 5f);
            Assert.AreEqual(1, m.SampleCount);
            Assert.AreEqual(0f, m.PeakCm);
        }
    }

    public class SensorCalibrationSessionTests
    {
        private static List<Vector2> Grid()
        {
            var points = new List<Vector2>();
            for (int y = 0; y < 3; y++)
                for (int x = 0; x < 3; x++)
                    points.Add(new Vector2(0.2f + 0.3f * x, 0.1f + 0.1f * y));
            return points;
        }

        // A mirrored camera with some scale and offset: table = (0.9 - 0.6 raw.x, 0.05 + 0.4 raw.y).
        private static Vector2 TableToRaw(Vector2 table) => new Vector2((0.9f - table.x) / 0.6f, (table.y - 0.05f) / 0.4f);

        private static List<SensorMarker> Marker(int session, Vector2 raw, float firstSeen = 0f)
        {
            return new List<SensorMarker> { new SensorMarker { SessionId = session, Raw = raw, FirstSeen = firstSeen } };
        }

        private static float Hold(SensorCalibrationSession s, List<SensorMarker> markers, float t, float seconds = SensorCalibrationSession.HoldSeconds + 0.1f)
        {
            for (float end = t + seconds; t < end; t += 0.05f) s.Feed(markers, t);
            return t;
        }

        private static readonly List<SensorMarker> Empty = new List<SensorMarker>();

        // A session past its "remove all tiles" step.
        private static SensorCalibrationSession Started(out float t)
        {
            var s = new SensorCalibrationSession(Grid(), null);
            t = Hold(s, Empty, 0f, SensorCalibrationSession.ClearSeconds + 0.1f);
            Assert.IsFalse(s.IsClearingTable);
            return s;
        }

        [Test]
        public void ClearingTable_EndsOnlyAfterAnEmptyTableForClearSeconds()
        {
            var s = new SensorCalibrationSession(Grid(), null);
            Assert.IsTrue(s.IsClearingTable);

            float t = Hold(s, Marker(1, TableToRaw(s.Targets[0])), 0f, 3f);
            Assert.IsTrue(s.IsClearingTable, "a tile on the table keeps it in the clearing step");
            Assert.AreEqual(0, s.CapturedCount, "nothing is captured while clearing");

            t = Hold(s, Empty, t, 0.5f);
            Assert.IsTrue(s.IsClearingTable, "empty for less than ClearSeconds");
            t = Hold(s, Marker(2, new Vector2(0.5f, 0.5f)), t, 0.1f);
            t = Hold(s, Empty, t, 0.5f);
            Assert.IsTrue(s.IsClearingTable, "a tile put back restarts the wait");

            Hold(s, Empty, t, 0.7f);
            Assert.IsFalse(s.IsClearingTable);
            Assert.AreEqual(0, s.CurrentIndex);
            Assert.IsFalse(s.Next(), "Next does nothing before a capture");
        }

        [Test]
        public void Capture_NeedsHoldSecondsOfStillness()
        {
            var s = Started(out float t);
            t = Hold(s, Marker(1, TableToRaw(s.Targets[0])), t, SensorCalibrationSession.HoldSeconds - 0.3f);
            Assert.AreEqual(0, s.CapturedCount, "not still for long enough yet");
            Hold(s, Marker(1, TableToRaw(s.Targets[0])), t, 0.5f);
            Assert.AreEqual(1, s.CapturedCount);
        }

        [Test]
        public void Capture_DoesNotAdvance_NextDoes()
        {
            var s = Started(out float t);
            Hold(s, Marker(1, TableToRaw(s.Targets[0])), t);
            Assert.AreEqual(1, s.CapturedCount);
            Assert.AreEqual(0, s.CurrentIndex, "a capture waits for Next");
            Assert.IsTrue(s.AwaitingNext);
            Assert.IsFalse(s.CaptureNow(), "a stored target can't be captured again");

            Assert.IsTrue(s.Next());
            Assert.AreEqual(1, s.CurrentIndex);
            Assert.IsFalse(s.AwaitingNext);
        }

        [Test]
        public void StoredTileLeftStill_AdvancesByItself()
        {
            var s = Started(out float t);
            t = Hold(s, Marker(1, TableToRaw(s.Targets[0])), t, SensorCalibrationSession.AutoAdvanceSeconds - 0.4f);
            Assert.AreEqual(1, s.CapturedCount);
            Assert.AreEqual(0, s.CurrentIndex, "not yet: still counting down");
            Assert.Greater(s.AutoAdvanceIn, 0f);

            Hold(s, Marker(1, TableToRaw(s.Targets[0])), t, 0.6f);
            Assert.AreEqual(1, s.CurrentIndex, "still for AutoAdvanceSeconds: moves on without Next");
            Assert.AreEqual(1, s.CapturedCount, "and the tile left there does not fill the next target");
        }

        [Test]
        public void StoredTileMovedAway_WaitsForNext()
        {
            var s = Started(out float t);
            t = Hold(s, Marker(1, TableToRaw(s.Targets[0])), t);
            Assert.IsTrue(s.AwaitingNext);
            Hold(s, Marker(1, TableToRaw(s.Targets[1])), t, 6f);
            Assert.AreEqual(0, s.CurrentIndex, "moved off before the countdown ended: Next is needed");
            Assert.AreEqual(-1f, s.AutoAdvanceIn);
            Assert.IsTrue(s.Next());
        }

        [Test]
        public void Next_WithoutCapture_ReturnsFalse()
        {
            var s = Started(out float t);
            Assert.IsFalse(s.Next());
            Hold(s, Marker(1, new Vector2(0.4f, 0.4f)), t, 0.5f);
            Assert.IsFalse(s.Next(), "a tile still settling is not a capture");
            Assert.AreEqual(0, s.CurrentIndex);
        }

        [Test]
        public void RestingTile_AutoCapturesOnce()
        {
            var s = Started(out float t);
            t = Hold(s, Marker(1, TableToRaw(s.Targets[0])), t);
            Assert.AreEqual(1, s.CapturedCount);

            s.Next();
            Hold(s, Marker(1, TableToRaw(s.Targets[0])), t, 4f);
            Assert.AreEqual(1, s.CapturedCount, "a tile left in place must not fill the next target");
            Assert.IsTrue(s.WaitingForMove);
        }

        [Test]
        public void TileWithNewSessionIds_IsNotCapturedAgainInPlace()
        {
            // reacTIVision can issue a new session id for the same tile every frame.
            var s = Started(out float t);
            Vector2 at = TableToRaw(s.Targets[0]);
            int session = 100;
            for (float end = t + 2.5f; t < end; t += 0.05f) s.Feed(Marker(session++, at, t), t);
            Assert.AreEqual(1, s.CapturedCount, "the hold follows the position, not the session id");

            s.Next();
            for (float end = t + 4f; t < end; t += 0.05f) s.Feed(Marker(session++, at, t), t);
            Assert.AreEqual(1, s.CapturedCount, "the same tile with a new id must still be moved first");
            Assert.AreEqual(1, s.CurrentIndex);

            Vector2 next = TableToRaw(s.Targets[1]);
            for (float end = t + 2.5f; t < end; t += 0.05f) s.Feed(Marker(session++, next, t), t);
            Assert.AreEqual(2, s.CapturedCount, "moved to the next cross and held still: captured");
        }

        [Test]
        public void MovingTile_DoesNotCapture()
        {
            var s = Started(out float t);
            for (int i = 0; i < 80; i++, t += 0.05f)
                s.Feed(Marker(1, new Vector2(0.1f + i * 0.01f, 0.5f)), t);
            Assert.AreEqual(0, s.CapturedCount);
        }

        [Test]
        public void SameTileMovedToNextTarget_Captures()
        {
            var s = Started(out float t);
            t = Hold(s, Marker(1, TableToRaw(s.Targets[0])), t);
            Assert.IsTrue(s.Next());
            Hold(s, Marker(1, TableToRaw(s.Targets[1])), t);
            Assert.AreEqual(2, s.CapturedCount);
        }

        [Test]
        public void NewestMarker_IsTheOneUsed()
        {
            var s = Started(out float t);
            var markers = new List<SensorMarker>
            {
                new SensorMarker { SessionId = 1, Raw = new Vector2(0.9f, 0.9f), FirstSeen = 0f },
                new SensorMarker { SessionId = 2, Raw = new Vector2(0.3f, 0.3f), FirstSeen = 5f },
            };
            s.Feed(markers, t + 6f);
            Assert.AreEqual(new Vector2(0.3f, 0.3f), s.LiveRaw);
        }

        [Test]
        public void SkipBackAndGoTo_KeepCaptures()
        {
            var s = Started(out float t);
            Vector2 raw0 = TableToRaw(s.Targets[0]);
            t = Hold(s, Marker(1, raw0), t);
            Assert.IsTrue(s.Next());
            s.Skip();
            Assert.AreEqual(2, s.CurrentIndex);
            Assert.AreEqual(SensorTargetState.Skipped, s.StateOf(1));

            Assert.IsTrue(s.Back());
            Assert.IsTrue(s.Back());
            Assert.AreEqual(0, s.CurrentIndex);
            Assert.AreEqual(1, s.CapturedCount, "going back keeps the captures");
            Assert.AreEqual(SensorTargetState.Captured, s.StateOf(0));
            Assert.IsFalse(s.Back());

            // Retake: lift the tile, hold it on the target again; the new capture replaces the old one.
            t = Hold(s, Empty, t, 0.2f);
            Vector2 retake = raw0 + new Vector2(0.002f, 0f);
            t = Hold(s, Marker(2, retake), t);
            Assert.AreEqual(1, s.CapturedCount);
            Assert.That(Vector2.Distance(s.CapturedRaw(0), retake), Is.LessThan(1e-4f));

            Assert.IsTrue(s.Next());
            Assert.AreEqual(2, s.CurrentIndex, "Next goes to the next target still without a capture");

            Assert.IsTrue(s.GoTo(1), "a skipped target can be taken after all");
            Assert.AreEqual(1, s.CurrentIndex);
            Assert.IsFalse(s.Next(), "it has no capture yet");
        }

        [Test]
        public void FullCapture_FitsTheCameraTransform()
        {
            var s = Started(out float t);
            for (int i = 0; i < 9; i++)
            {
                t = Hold(s, Marker(i, TableToRaw(s.Targets[i]), i), t);
                Assert.IsFalse(s.IsComplete, "the last capture still waits for Next");
                Assert.IsTrue(s.Next());
            }

            Assert.IsTrue(s.IsComplete);
            Assert.IsTrue(s.HasFit);
            Assert.AreEqual(9, s.Report.PointCount);
            Vector2 probe = new Vector2(0.45f, 0.17f);
            Assert.Less(Vector2.Distance(s.Fit.Map(TableToRaw(probe)), probe), 0.001f);
        }

        [Test]
        public void FewerThanFourCaptures_HasNoFit()
        {
            var s = Started(out float t);
            for (int i = 0; i < 3; i++)
            {
                t = Hold(s, Marker(i, TableToRaw(s.Targets[i]), i), t);
                s.Next();
            }
            Assert.IsFalse(s.HasFit);
        }

        [Test]
        public void PickSpread_CoversTheExtremes()
        {
            var candidates = new List<Vector2>();
            for (int y = 0; y <= 10; y++)
                for (int x = 0; x <= 10; x++)
                    candidates.Add(new Vector2(x / 10f, y / 10f));
            var picked = SensorTargetLayout.PickSpread(candidates, 9);
            Assert.AreEqual(9, picked.Count);
            foreach (var corner in new[] { Vector2.zero, Vector2.right, Vector2.up, Vector2.one })
                Assert.Contains(corner, picked);
        }

        [Test]
        public void PickSpread_ReturnsWhatExistsWhenShortOfCandidates()
        {
            var picked = SensorTargetLayout.PickSpread(new List<Vector2> { Vector2.zero, Vector2.one }, 9);
            Assert.AreEqual(2, picked.Count);
        }

        [Test]
        public void CalibrationFile_WithoutFilterField_GetsDefaults()
        {
            var setup = TableSetups.Pilot;
            var file = TableCalibrationFile.CreateDefault(setup);
            var sensor = file.GetOrAddSensor(0);
            sensor.filter.radiusCm = 1.5f;
            string json = file.ToJson();

            Assert.IsTrue(TableCalibrationFile.TryFromJson(json, out var loaded, out string error), error);
            Assert.AreEqual(1.5f, loaded.FindSensor(0).filter.radiusCm);
            Assert.IsFalse(loaded.FindSensor(0).IsFitted);

            string legacy = json.Replace("\"filter\"", "\"unusedFilter\"");
            Assert.IsTrue(TableCalibrationFile.TryFromJson(legacy, out loaded, out error), error);
            Assert.AreEqual(new SensorFilterSettings().radiusCm, loaded.FindSensor(0).filter.radiusCm);
        }
    }
}
#endif
