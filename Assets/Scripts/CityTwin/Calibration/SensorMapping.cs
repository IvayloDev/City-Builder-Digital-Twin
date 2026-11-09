using System;
using System.Collections.Generic;
using UnityEngine;

namespace CityTwin.Calibration
{
    /// <summary>Result of fitting a <see cref="SensorMapping"/> to calibration points. Errors are in table units.</summary>
    public struct SensorFitReport
    {
        public int PointCount;
        public double RmsError;
        public double MaxError;
        public double K1;
        /// <summary>False when there were too few points to fit lens distortion (k1 left at 0).</summary>
        public bool UsedLensModel;
    }

    /// <summary>
    /// Maps raw sensor coordinates (reacTIVision TUIO 0..1) to table coordinates:
    /// first undoes radial lens distortion (one coefficient, k1), then applies a homography.
    /// The single-coefficient lens model extrapolates sensibly past the calibration points,
    /// which matters because the cameras see the table edges poorly.
    /// </summary>
    [Serializable]
    public class SensorMapping
    {
        [Tooltip("Camera (undistorted) -> table homography.")]
        public Homography homography = Homography.Identity;

        [Tooltip("Radial distortion coefficient. Negative pulls edges in (undoes pincushion), positive pushes them out (undoes barrel). 0 = no correction.")]
        public double k1;

        [Tooltip("Distortion centre in raw sensor coordinates; the lens optical axis, normally the frame centre.")]
        public Vector2 distortionCenter = new Vector2(0.5f, 0.5f);

        [Tooltip("Camera frame width / height. TUIO x and y are both 0..1, so radius must be corrected for a non-square frame (1080p camera = 16:9).")]
        public float aspect = 16f / 9f;

        public const int DefaultMinPointsForLens = 6;
        public const double DefaultMaxK1 = 0.5;

        /// <summary>Raw sensor point with lens distortion removed (still in sensor 0..1 space).</summary>
        public Vector2 Undistort(Vector2 raw)
        {
            if (k1 == 0) return raw;
            double dx = (raw.x - distortionCenter.x) * aspect;
            double dy = raw.y - distortionCenter.y;
            double f = 1 + k1 * (dx * dx + dy * dy);
            return new Vector2((float)(distortionCenter.x + dx * f / aspect), (float)(distortionCenter.y + dy * f));
        }

        /// <summary>Inverse of <see cref="Undistort"/>. Solves r + k1*r^3 = r_undistorted for the radius by Newton's method.</summary>
        public Vector2 Distort(Vector2 undistorted)
        {
            if (k1 == 0) return undistorted;
            double ux = (undistorted.x - distortionCenter.x) * aspect;
            double uy = undistorted.y - distortionCenter.y;
            double ru = Math.Sqrt(ux * ux + uy * uy);
            if (ru < 1e-12) return undistorted;

            double r = ru;
            for (int i = 0; i < 20; i++)
            {
                double slope = 1 + 3 * k1 * r * r;
                if (slope <= 1e-6) break; // past the fold of the model; keep the last good radius
                double step = (r + k1 * r * r * r - ru) / slope;
                r -= step;
                if (Math.Abs(step) < 1e-12) break;
            }
            double s = r / ru;
            return new Vector2((float)(distortionCenter.x + ux * s / aspect), (float)(distortionCenter.y + uy * s));
        }

        /// <summary>Raw sensor point -> table point.</summary>
        public Vector2 Map(Vector2 raw) => homography.Apply(Undistort(raw));

        /// <summary>Table point -> raw sensor point (for debug tools that synthesize sensor input).</summary>
        public bool TryUnmap(Vector2 table, out Vector2 raw)
        {
            raw = table;
            if (!homography.TryInverse(out Homography inv)) return false;
            if (!inv.TryApply(table.x, table.y, out double x, out double y)) return false;
            raw = Distort(new Vector2((float)x, (float)y));
            return true;
        }

        /// <summary>
        /// Fit a mapping from raw sensor points to their known table positions.
        /// 4+ pairs: homography only. <paramref name="minPointsForLens"/>+ pairs: also searches k1 in
        /// [-maxK1, maxK1] for the value giving the lowest residual. (With exactly 4 points any k1
        /// fits perfectly, so lens distortion can't be told apart from perspective.)
        /// The current <see cref="distortionCenter"/> and <see cref="aspect"/> of <paramref name="template"/>
        /// are kept; pass null for defaults.
        /// </summary>
        public static bool TryFit(IReadOnlyList<Vector2> raw, IReadOnlyList<Vector2> table, SensorMapping template,
            out SensorMapping mapping, out SensorFitReport report,
            bool fitLens = true, int minPointsForLens = DefaultMinPointsForLens, double maxK1 = DefaultMaxK1)
        {
            mapping = new SensorMapping();
            if (template != null)
            {
                mapping.distortionCenter = template.distortionCenter;
                mapping.aspect = template.aspect;
            }
            report = default;
            if (raw == null || table == null || raw.Count != table.Count || raw.Count < 4) return false;

            var undistorted = new List<Vector2>(raw.Count);
            bool lens = fitLens && raw.Count >= Math.Max(5, minPointsForLens) && maxK1 > 0;

            double bestK1 = 0;
            if (lens)
            {
                // Coarse scan then golden-section refine. The residual is smooth in k1 but not
                // guaranteed unimodal over a wide range, so the scan picks the right basin first.
                const int steps = 20;
                double step = 2 * maxK1 / steps;
                double bestErr = double.PositiveInfinity;
                for (int i = 0; i <= steps; i++)
                {
                    double k = -maxK1 + i * step;
                    double e = ResidualFor(mapping, k, raw, table, undistorted);
                    if (e < bestErr) { bestErr = e; bestK1 = k; }
                }

                double a = Math.Max(-maxK1, bestK1 - step), b = Math.Min(maxK1, bestK1 + step);
                const double phi = 0.6180339887498949;
                double c = b - phi * (b - a), d = a + phi * (b - a);
                double fc = ResidualFor(mapping, c, raw, table, undistorted);
                double fd = ResidualFor(mapping, d, raw, table, undistorted);
                for (int i = 0; i < 40 && b - a > 1e-7; i++)
                {
                    if (fc < fd) { b = d; d = c; fd = fc; c = b - phi * (b - a); fc = ResidualFor(mapping, c, raw, table, undistorted); }
                    else { a = c; c = d; fc = fd; d = a + phi * (b - a); fd = ResidualFor(mapping, d, raw, table, undistorted); }
                }
                double refined = 0.5 * (a + b);
                if (ResidualFor(mapping, refined, raw, table, undistorted) <= bestErr) bestK1 = refined;
            }

            mapping.k1 = bestK1;
            FillUndistorted(mapping, raw, undistorted);
            if (!Homography.TrySolve(undistorted, table, out mapping.homography)) return false;

            Homography.ErrorStats(mapping.homography, undistorted, table, out double rms, out double max);
            report = new SensorFitReport
            {
                PointCount = raw.Count,
                RmsError = rms,
                MaxError = max,
                K1 = bestK1,
                UsedLensModel = lens,
            };
            return true;
        }

        private static double ResidualFor(SensorMapping m, double k, IReadOnlyList<Vector2> raw, IReadOnlyList<Vector2> table, List<Vector2> buffer)
        {
            m.k1 = k;
            FillUndistorted(m, raw, buffer);
            if (!Homography.TrySolve(buffer, table, out Homography h)) return double.PositiveInfinity;
            return Homography.RmsError(h, buffer, table);
        }

        private static void FillUndistorted(SensorMapping m, IReadOnlyList<Vector2> raw, List<Vector2> buffer)
        {
            buffer.Clear();
            for (int i = 0; i < raw.Count; i++) buffer.Add(m.Undistort(raw[i]));
        }
    }
}
