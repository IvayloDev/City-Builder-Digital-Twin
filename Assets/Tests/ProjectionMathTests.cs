#if UNITY_INCLUDE_TESTS
using NUnit.Framework;
using UnityEngine;
using CityTwin.Calibration;

namespace CityTwin.Tests
{
    public class ProjectionMathTests
    {
        // Does a projector light a table point: inside its frame and on its side of the seam.
        private static bool Lights(ProjectorCalibration cal, Vector2 uv)
        {
            Assert.That(cal.TryGetTableToProjector(out var h), Is.True, cal.name);
            Vector2 p = h.Apply(uv);
            const float eps = 1e-4f;
            bool inFrame = p.x >= -eps && p.x <= 1f + eps && p.y >= -eps && p.y <= 1f + eps;
            return inFrame && cal.KeepsTablePoint(uv);
        }

        [Test]
        public void DefaultLayout_EveryTablePointLitByExactlyOneProjector()
        {
            var layout = ProjectorCalibration.DefaultTwoProjectorLayout();
            Assert.That(layout.Count, Is.EqualTo(2));

            int samples = 0;
            for (int y = 0; y < 41; y++)
                for (int x = 0; x < 41; x++)
                {
                    var uv = new Vector2(x / 40f + 0.0013f, y / 40f + 0.0007f); // off the exact diagonal, where the cut is a tie
                    if (Vector2.Distance(uv, new Vector2(0.5f, 0.5f)) > 0.5f) continue; // table is round
                    int count = (Lights(layout[0], uv) ? 1 : 0) + (Lights(layout[1], uv) ? 1 : 0);
                    Assert.That(count, Is.EqualTo(1), $"uv={uv}");
                    samples++;
                }
            Assert.That(samples, Is.GreaterThan(1000));
        }

        [Test]
        public void DefaultLayout_SeamOnDiagonalSoEachGameHasOneProjector()
        {
            var layout = ProjectorCalibration.DefaultTwoProjectorLayout();
            ProjectorCalibration a = layout[0], b = layout[1];

            // Middle of each game wedge (left, top / bottom, right).
            foreach (var uv in new[] { new Vector2(0.15f, 0.5f), new Vector2(0.5f, 0.85f) })
            {
                Assert.That(Lights(a, uv), Is.True, $"A at {uv}");
                Assert.That(Lights(b, uv), Is.False, $"B at {uv}");
            }
            foreach (var uv in new[] { new Vector2(0.5f, 0.15f), new Vector2(0.85f, 0.5f) })
            {
                Assert.That(Lights(a, uv), Is.False, $"A at {uv}");
                Assert.That(Lights(b, uv), Is.True, $"B at {uv}");
            }

            // The cut flips 1 cm either side of the bottom-left -> top-right diagonal, all along it.
            var across = new Vector2(-1f, 1f).normalized * (1f / 300f);
            for (int i = 0; i <= 10; i++)
            {
                float d = 0.16f + 0.068f * i;
                var onSeam = new Vector2(d, d);
                Assert.That(Lights(a, onSeam + across) && !Lights(b, onSeam + across), Is.True, $"A side, d={d}");
                Assert.That(Lights(b, onSeam - across) && !Lights(a, onSeam - across), Is.True, $"B side, d={d}");
            }
        }

        [Test]
        public void DefaultLayout_FramesReachPastTheSeam()
        {
            // Each frame's seam-side edge lies beyond the cut, so the cut (not the frame edge) decides where the image stops.
            foreach (var cal in ProjectorCalibration.DefaultTwoProjectorLayout())
            {
                Assert.That(cal.KeepsTablePoint(cal.tablePoints[0]), Is.False, cal.name);
                Assert.That(cal.KeepsTablePoint(cal.tablePoints[2]), Is.True, cal.name);
                cal.seamEnabled = false;
                Assert.That(cal.KeepsTablePoint(cal.tablePoints[0]), Is.True, $"{cal.name} with the cut off");
            }
        }

        [Test]
        public void DefaultLayout_RegionCornersLandOnFrameCorners()
        {
            foreach (var cal in ProjectorCalibration.DefaultTwoProjectorLayout())
            {
                Assert.That(cal.TryGetTableToProjector(out var h), Is.True, cal.name);
                for (int i = 0; i < cal.tablePoints.Length; i++)
                    Assert.That(Vector2.Distance(h.Apply(cal.tablePoints[i]), cal.projectorPoints[i]), Is.LessThan(1e-4f), $"{cal.name} point {i}");
            }
        }

        [Test]
        public void MeshOffset_InterpolatesBilinearly()
        {
            var cal = new ProjectorCalibration
            {
                meshColumns = 1,
                meshRows = 1,
                meshOffsets = new[] { new Vector2(0f, 0f), new Vector2(0.1f, 0f), new Vector2(0f, 0.2f), new Vector2(0.1f, 0.2f) },
            };
            Assert.That(ProjectionMath.MeshOffset(cal, 0.5f, 0.5f), Is.EqualTo(new Vector2(0.05f, 0.1f)));
            Assert.That(ProjectionMath.MeshOffset(cal, 1f, 1f), Is.EqualTo(new Vector2(0.1f, 0.2f)));

            cal.meshOffsets = new Vector2[3]; // wrong size = mesh warp off
            Assert.That(ProjectionMath.MeshOffset(cal, 0.5f, 0.5f), Is.EqualTo(Vector2.zero));
        }

        [Test]
        public void WarpVertices_GridTheFrameAndSampleTheMappedTable()
        {
            var cal = ProjectorCalibration.DefaultTwoProjectorLayout()[0];
            Assert.That(cal.TryGetTableToProjector(out var h), Is.True);
            Assert.That(h.TryInverse(out var inv), Is.True);
            const int n = 8;
            const float aspect = 16f / 9f;
            var verts = new Vector3[(n + 1) * (n + 1)];
            var uvs = new Vector2[verts.Length];
            ProjectionMath.BuildWarpVertices(cal, inv, n, n, aspect, verts, uvs);

            // First vertex = frame bottom-left, showing the table point pinned there.
            Assert.That(Vector3.Distance(verts[0], new Vector3(-aspect / 2f, -0.5f, 0f)), Is.LessThan(1e-4f));
            Assert.That(Vector2.Distance(uvs[0], cal.tablePoints[0]), Is.LessThan(1e-4f));
            // Last vertex = frame top-right.
            Assert.That(Vector3.Distance(verts[verts.Length - 1], new Vector3(aspect / 2f, 0.5f, 0f)), Is.LessThan(1e-4f));
            Assert.That(Vector2.Distance(uvs[uvs.Length - 1], cal.tablePoints[2]), Is.LessThan(1e-4f));

            // A mesh offset moves the vertex but keeps what it shows.
            cal.meshColumns = 1; cal.meshRows = 1;
            cal.meshOffsets = new[] { new Vector2(0.1f, 0f), Vector2.zero, Vector2.zero, Vector2.zero };
            ProjectionMath.BuildWarpVertices(cal, inv, n, n, aspect, verts, uvs);
            Assert.That(Vector3.Distance(verts[0], new Vector3(-aspect / 2f + 0.1f * aspect, -0.5f, 0f)), Is.LessThan(1e-4f));
            Assert.That(Vector2.Distance(uvs[0], cal.tablePoints[0]), Is.LessThan(1e-4f));
        }

        [Test]
        public void TableToProjectorAndBack_RoundTripsWithMeshWarp()
        {
            // Mouse remapping inverts the drawn warp: corner pin plus mesh offsets.
            var cal = ProjectorCalibration.DefaultTwoProjectorLayout()[0];
            cal.meshColumns = 2; cal.meshRows = 2; cal.meshOffsets = new Vector2[9];
            cal.meshOffsets[4] = new Vector2(0.03f, -0.02f);
            cal.meshOffsets[1] = new Vector2(0.01f, 0.012f);
            Assert.That(cal.TryGetTableToProjector(out var h), Is.True);
            Assert.That(h.TryInverse(out var inv), Is.True);

            foreach (var uv in new[] { new Vector2(0.3f, 0.6f), new Vector2(0.5f, 0.55f), new Vector2(0.2f, 0.45f), new Vector2(0.45f, 0.9f) })
            {
                Vector2 p = ProjectionMath.TableUvToProjectorUv(cal, h, uv);
                Vector2 back = ProjectionMath.ProjectorUvToTableUv(cal, inv, p);
                Assert.That(Vector2.Distance(back, uv), Is.LessThan(1e-4f), $"uv={uv} -> {p} -> {back}");
            }
        }

        [Test]
        public void GridTriangles_CountAndRange()
        {
            var tris = ProjectionMath.BuildGridTriangles(4, 3);
            Assert.That(tris.Length, Is.EqualTo(4 * 3 * 6));
            foreach (int idx in tris) Assert.That(idx, Is.InRange(0, 5 * 4 - 1));
        }
    }
}
#endif
