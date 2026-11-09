#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using CityTwin.Calibration;

namespace CityTwin.Tests
{
    public class CalibrationMathTests
    {
        // A realistic sensor -> table transform: 45 deg rotation, mirrored Y, scale to cm, offset, mild keystone.
        private static Homography KnownTransform()
        {
            const double c = 0.7071067811865476;
            var rotScale = new Homography { m00 = 150 * c, m01 = 150 * c, m10 = 150 * c, m11 = -150 * c, m22 = 1 };
            var translate = new Homography { m00 = 1, m02 = 80, m11 = 1, m12 = -40, m22 = 1 };
            var keystone = new Homography { m00 = 1, m11 = 1, m20 = 0.12, m21 = -0.07, m22 = 1 };
            return translate * rotScale * keystone;
        }

        private static List<Vector2> Grid(int nx, int ny, float min, float max)
        {
            var pts = new List<Vector2>();
            for (int y = 0; y < ny; y++)
                for (int x = 0; x < nx; x++)
                    pts.Add(new Vector2(Mathf.Lerp(min, max, x / (nx - 1f)), Mathf.Lerp(min, max, y / (ny - 1f))));
            return pts;
        }

        private static List<Vector2> MapAll(Homography h, List<Vector2> pts)
        {
            var o = new List<Vector2>(pts.Count);
            foreach (var p in pts) o.Add(h.Apply(p));
            return o;
        }

        [Test]
        public void Identity_LeavesPointsUnchanged()
        {
            var p = new Vector2(0.3f, 0.8f);
            Assert.That(Vector2.Distance(Homography.Identity.Apply(p), p), Is.LessThan(1e-6f));
        }

        [Test]
        public void FourPoints_ExactFit_GeneralizesToOtherPoints()
        {
            var truth = KnownTransform();
            var src = new List<Vector2> { new Vector2(0.1f, 0.1f), new Vector2(0.9f, 0.15f), new Vector2(0.85f, 0.9f), new Vector2(0.12f, 0.88f) };
            Assert.That(Homography.TrySolve(src, MapAll(truth, src), out var h), Is.True);

            foreach (var p in Grid(5, 5, 0f, 1f))
                Assert.That(Vector2.Distance(h.Apply(p), truth.Apply(p)), Is.LessThan(1e-3f), $"at {p}");
        }

        [Test]
        public void ManyPoints_NoiseFree_RecoversTransform()
        {
            var truth = KnownTransform();
            var src = Grid(5, 4, 0.05f, 0.95f);
            Assert.That(Homography.TrySolve(src, MapAll(truth, src), out var h), Is.True);
            Assert.That(Homography.RmsError(h, src, MapAll(truth, src)), Is.LessThan(1e-3));
        }

        [Test]
        public void ManyPoints_WithNoise_ResidualMatchesNoiseLevel()
        {
            var truth = KnownTransform();
            var src = Grid(6, 6, 0.05f, 0.95f);
            var dst = MapAll(truth, src);
            var rng = new System.Random(1234);
            for (int i = 0; i < dst.Count; i++)
                dst[i] += new Vector2((float)(rng.NextDouble() - 0.5), (float)(rng.NextDouble() - 0.5)); // +/-0.5 cm

            Assert.That(Homography.TrySolve(src, dst, out var h), Is.True);
            Assert.That(Homography.RmsError(h, src, dst), Is.LessThan(0.5));
            // Least squares should land closer to the truth than the noise itself.
            foreach (var p in Grid(3, 3, 0.2f, 0.8f))
                Assert.That(Vector2.Distance(h.Apply(p), truth.Apply(p)), Is.LessThan(0.4f));
        }

        [Test]
        public void Inverse_RoundTrips()
        {
            var h = KnownTransform();
            Assert.That(h.TryInverse(out var inv), Is.True);
            foreach (var p in Grid(4, 4, 0f, 1f))
                Assert.That(Vector2.Distance(inv.Apply(h.Apply(p)), p), Is.LessThan(1e-5f));
        }

        [Test]
        public void RowMajor_RoundTrips()
        {
            var h = KnownTransform();
            var back = new Homography(h.ToRowMajor());
            Assert.That(back.ToRowMajor(), Is.EqualTo(h.ToRowMajor()));
        }

        [Test]
        public void TooFewPoints_Fails()
        {
            var src = new List<Vector2> { Vector2.zero, Vector2.right, Vector2.up };
            Assert.That(Homography.TrySolve(src, src, out _), Is.False);
        }

        [Test]
        public void MismatchedCounts_Fails()
        {
            var src = Grid(2, 2, 0f, 1f);
            var dst = Grid(3, 2, 0f, 1f);
            Assert.That(Homography.TrySolve(src, dst, out _), Is.False);
        }

        [Test]
        public void ThreeCollinearOfFour_Fails()
        {
            var src = new List<Vector2> { new Vector2(0, 0), new Vector2(0.5f, 0), new Vector2(1, 0), new Vector2(0.5f, 1) };
            var dst = new List<Vector2> { new Vector2(0, 0), new Vector2(0.4f, 0.1f), new Vector2(1, 0), new Vector2(0.5f, 1) };
            Assert.That(Homography.TrySolve(src, dst, out _), Is.False);
        }

        [Test]
        public void AllCollinear_Fails()
        {
            var src = new List<Vector2>();
            for (int i = 0; i < 8; i++) src.Add(new Vector2(i * 0.1f, i * 0.05f));
            Assert.That(Homography.TrySolve(src, src, out _), Is.False);
        }

        // --- SensorMapping ---

        private static List<Vector2> SyntheticRaw(SensorMapping truth, List<Vector2> table)
        {
            var raw = new List<Vector2>(table.Count);
            foreach (var t in table)
            {
                Assert.That(truth.TryUnmap(t, out var r), Is.True);
                raw.Add(r);
            }
            return raw;
        }

        // Round trip from raw sensor space, which is what Distort is for. (The reverse direction isn't
        // defined everywhere: with k1 < 0, undistorted points past the model's fold have no raw pre-image.)
        [TestCase(-0.18)]
        [TestCase(0.15)]
        public void Distort_InvertsUndistort(double k1)
        {
            var m = new SensorMapping { k1 = k1 };
            foreach (var p in Grid(5, 5, 0f, 1f))
                Assert.That(Vector2.Distance(m.Distort(m.Undistort(p)), p), Is.LessThan(1e-5f), $"at {p}");
        }

        [Test]
        public void Fit_RecoversLensCoefficientAndMapping()
        {
            var truth = new SensorMapping { homography = KnownTransform(), k1 = -0.15 };

            // Calibration marks only in the well-covered middle of the camera frame...
            var tableMarks = new List<Vector2>();
            foreach (var p in Grid(4, 4, 0.2f, 0.8f)) tableMarks.Add(truth.Map(p));
            var raw = SyntheticRaw(truth, tableMarks);

            Assert.That(SensorMapping.TryFit(raw, tableMarks, null, out var fit, out var report), Is.True);
            Assert.That(report.UsedLensModel, Is.True);
            Assert.That(report.K1, Is.EqualTo(-0.15).Within(0.01));
            Assert.That(report.RmsError, Is.LessThan(0.05));

            // ...should still map the poorly covered frame edges well, thanks to the lens model.
            foreach (var p in Grid(5, 5, 0.02f, 0.98f))
                Assert.That(Vector2.Distance(fit.Map(p), truth.Map(p)), Is.LessThan(0.5f), $"edge point {p}");
        }

        [Test]
        public void Fit_NoDistortion_KeepsK1NearZero()
        {
            var truth = new SensorMapping { homography = KnownTransform() };
            var tableMarks = new List<Vector2>();
            foreach (var p in Grid(3, 3, 0.15f, 0.85f)) tableMarks.Add(truth.Map(p));
            var raw = SyntheticRaw(truth, tableMarks);

            Assert.That(SensorMapping.TryFit(raw, tableMarks, null, out _, out var report), Is.True);
            Assert.That(report.K1, Is.EqualTo(0).Within(0.005));
            Assert.That(report.RmsError, Is.LessThan(0.05));
        }

        [Test]
        public void Fit_FourPoints_SkipsLensModel()
        {
            var truth = new SensorMapping { homography = KnownTransform() };
            var tableMarks = new List<Vector2>();
            foreach (var p in Grid(2, 2, 0.2f, 0.8f)) tableMarks.Add(truth.Map(p));
            var raw = SyntheticRaw(truth, tableMarks);

            Assert.That(SensorMapping.TryFit(raw, tableMarks, null, out var fit, out var report), Is.True);
            Assert.That(report.UsedLensModel, Is.False);
            Assert.That(fit.k1, Is.EqualTo(0));
        }

        [Test]
        public void Fit_KeepsTemplateCenterAndAspect()
        {
            var template = new SensorMapping { distortionCenter = new Vector2(0.48f, 0.52f), aspect = 4f / 3f };
            var src = Grid(3, 3, 0.1f, 0.9f);
            Assert.That(SensorMapping.TryFit(src, MapAll(KnownTransform(), src), template, out var fit, out _), Is.True);
            Assert.That(fit.distortionCenter, Is.EqualTo(template.distortionCenter));
            Assert.That(fit.aspect, Is.EqualTo(template.aspect));
        }

        [Test]
        public void Unmap_InvertsMap()
        {
            var m = new SensorMapping { homography = KnownTransform(), k1 = 0.1 };
            foreach (var p in Grid(4, 4, 0.05f, 0.95f))
            {
                Assert.That(m.TryUnmap(m.Map(p), out var back), Is.True);
                Assert.That(Vector2.Distance(back, p), Is.LessThan(1e-4f), $"at {p}");
            }
        }
    }
}
#endif
