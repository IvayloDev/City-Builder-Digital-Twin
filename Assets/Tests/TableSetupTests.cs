#if UNITY_INCLUDE_TESTS
using System.IO;
using NUnit.Framework;
using UnityEngine;
using CityTwin.Calibration;

namespace CityTwin.Tests
{
    public class TableSetupTests
    {
        [Test]
        public void Find_IsCaseInsensitive_AndNullForUnknown()
        {
            Assert.That(TableSetups.Find("TestRig"), Is.SameAs(TableSetups.TestRig));
            Assert.That(TableSetups.Find(" pilot "), Is.SameAs(TableSetups.Pilot));
            Assert.That(TableSetups.Find("nope"), Is.Null);
            Assert.That(TableSetups.Find(null), Is.Null);
        }

        [Test]
        public void LaunchOption_BothForms()
        {
            Assert.That(TableSetups.FromLaunchOptions(new[] { "game.exe", "-tableSetup", "testrig" }), Is.EqualTo("testrig"));
            Assert.That(TableSetups.FromLaunchOptions(new[] { "game.exe", "-tablesetup=pilot" }), Is.EqualTo("pilot"));
            Assert.That(TableSetups.FromLaunchOptions(new[] { "game.exe", "-tableSetup" }), Is.Null);
            Assert.That(TableSetups.FromLaunchOptions(new[] { "game.exe", "-batchmode" }), Is.Null);
        }

        [Test]
        public void Resolve_InGameSwitchThenLaunchOptionThenLastChoiceThenDefault()
        {
            string source;
            var args = new[] { "game.exe", "-tableSetup", "testrig" };
            Assert.That(TableSetups.Resolve("pilot", args, "testrig", "testrig", out source), Is.SameAs(TableSetups.Pilot),
                "a switch made in game this run beats the launch option, or the reload would undo it");
            Assert.That(source, Is.EqualTo("switched in game"));

            Assert.That(TableSetups.Resolve(null, args, "pilot", "pilot", out source), Is.SameAs(TableSetups.TestRig));
            StringAssert.Contains("launch option", source);

            Assert.That(TableSetups.Resolve(null, new[] { "game.exe" }, "testrig", "pilot", out source), Is.SameAs(TableSetups.TestRig));
            Assert.That(source, Is.EqualTo("last choice"));

            Assert.That(TableSetups.Resolve(null, new[] { "game.exe" }, null, "testrig", out source), Is.SameAs(TableSetups.TestRig));
            Assert.That(source, Is.EqualTo("default"));

            Debug.unityLogger.logEnabled = false; // unknown ids log a warning by design
            try
            {
                var bogus = new[] { "game.exe", "-tableSetup", "bogus" };
                Assert.That(TableSetups.Resolve("nope", bogus, "garbage", "also-garbage", out source), Is.SameAs(TableSetups.Pilot));
            }
            finally { Debug.unityLogger.logEnabled = true; }
        }

        [Test]
        public void PlayAreas_PilotRunsOnlyGame1()
        {
            Assert.That(TableSetups.Pilot.RunsPlayArea(0), Is.True);
            for (int i = 1; i < 4; i++) Assert.That(TableSetups.Pilot.RunsPlayArea(i), Is.False, $"game {i + 1}");
            for (int i = 0; i < 4; i++) Assert.That(TableSetups.TestRig.RunsPlayArea(i), Is.True, $"game {i + 1}");
            Assert.That(TableSetups.Pilot.PlayAreaList, Is.EqualTo("1"));
            Assert.That(TableSetups.TestRig.PlayAreaList, Is.EqualTo("1, 2, 3, 4"));
        }

        [Test]
        public void FitAspect_LetterboxesToTheProjectorShape()
        {
            // Half of a 1841x1036 Game view (the 2-screen preview): too narrow for 16:9 -> bars top and bottom.
            var r = ProjectionMath.FitAspect(new Rect(0f, 0f, 0.5f, 1f), new Vector2(1841f, 1036f), 16f / 9f);
            Assert.That(r.width * 1841f / (r.height * 1036f), Is.EqualTo(16f / 9f).Within(1e-3f));
            Assert.That(r.x, Is.EqualTo(0f).Within(1e-6f));
            Assert.That(r.width, Is.EqualTo(0.5f).Within(1e-6f));
            Assert.That(r.center.y, Is.EqualTo(0.5f).Within(1e-5f));

            // Too wide -> bars left and right, centred in the viewport.
            r = ProjectionMath.FitAspect(new Rect(0.5f, 0f, 0.5f, 1f), new Vector2(4000f, 1000f), 16f / 9f);
            Assert.That(r.width * 4000f / (r.height * 1000f), Is.EqualTo(16f / 9f).Within(1e-3f));
            Assert.That(r.center.x, Is.EqualTo(0.75f).Within(1e-5f));

            // Already right (real hardware: 3840x1080 window, each half 1920x1080) or aspect 0: unchanged.
            var half = new Rect(0.5f, 0f, 0.5f, 1f);
            Assert.That(ProjectionMath.FitAspect(half, new Vector2(3840f, 1080f), 16f / 9f), Is.EqualTo(half));
            Assert.That(ProjectionMath.FitAspect(half, new Vector2(1000f, 1000f), 0f), Is.EqualTo(half));
        }

        [Test]
        public void SavedChoice_RoundTrips()
        {
            string dir = Path.Combine(Application.temporaryCachePath, "TableSetupTests_" + System.Guid.NewGuid().ToString("N"));
            try
            {
                Assert.That(TableSetups.ReadSavedChoice(dir), Is.Null);
                Assert.That(TableSetups.WriteSavedChoice(dir, TableSetups.TestRig), Is.True);
                Assert.That(TableSetups.ReadSavedChoice(dir), Is.EqualTo("testrig"));
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        }

        private static bool InFrame(Vector2 p) => p.x >= -1e-4f && p.x <= 1f + 1e-4f && p.y >= -1e-4f && p.y <= 1f + 1e-4f;

        [Test]
        public void PilotLayout_FramesPlayArea1()
        {
            var layout = TableSetups.Pilot.CreateDefaultProjectors();
            Assert.That(layout.Count, Is.EqualTo(1));
            var cal = layout[0];
            Assert.That(cal.seamEnabled, Is.False);
            Assert.That(cal.TryGetTableToProjector(out var h), Is.True);

            // Play area 1 = the bottom wedge (game mask: 224-316 degrees, radius 0.195-0.46 table uv). All of it in frame.
            for (int ai = 0; ai <= 12; ai++)
                for (int ri = 0; ri <= 6; ri++)
                {
                    float a = Mathf.Deg2Rad * (224f + 92f * ai / 12f);
                    float r = 0.195f + (0.46f - 0.195f) * ri / 6f;
                    var uv = new Vector2(0.5f + r * Mathf.Cos(a), 0.5f + r * Mathf.Sin(a));
                    Assert.That(InFrame(h.Apply(uv)), Is.True, $"wedge point {uv} -> {h.Apply(uv)}");
                }

            // Centred horizontally on play area 1; the zone sits above the frame centre so the pins below fit.
            Vector2 zoneCenter = h.Apply(ProjectorCalibration.PlayArea1Center);
            Assert.That(zoneCenter.x, Is.EqualTo(0.5f).Within(1e-4f));
            Assert.That(zoneCenter.y, Is.GreaterThan(0.5f));
            Assert.That(Vector2.Distance(h.Apply(ProjectorCalibration.PilotFrameCenter), new Vector2(0.5f, 0.5f)), Is.LessThan(1e-4f));

            // The table centre and the top game are out of frame; the frame keeps the table undistorted on 16:9.
            Assert.That(InFrame(h.Apply(new Vector2(0.5f, 0.5f))), Is.False);
            Assert.That(InFrame(h.Apply(new Vector2(0.5f, 0.85f))), Is.False);
            Vector2 dx = h.Apply(new Vector2(0.6f, 0.2f)) - h.Apply(new Vector2(0.5f, 0.2f));
            Vector2 dy = h.Apply(new Vector2(0.5f, 0.3f)) - h.Apply(new Vector2(0.5f, 0.2f));
            Assert.That(dx.x * 16f / (dy.y * 9f), Is.EqualTo(1f).Within(1e-3f));
        }

        [Test]
        public void EverySetupHasAValidPlaceholder()
        {
            foreach (var s in TableSetups.All)
            {
                var file = TableCalibrationFile.CreateDefault(s);
                Assert.That(file.setup, Is.EqualTo(s.Id));
                Assert.That(TableCalibrationFile.TryFromJson(file.ToJson(), out _, out string error), Is.True, $"{s.Id}: {error}");
            }
        }
    }
}
#endif
