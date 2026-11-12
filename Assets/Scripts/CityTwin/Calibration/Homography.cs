using System;
using System.Collections.Generic;
using UnityEngine;

namespace CityTwin.Calibration
{
    /// <summary>
    /// Planar projective transform (3x3, row-major, doubles). Maps points on one plane to another:
    /// handles translation, rotation, scale, mirroring and perspective (keystone) in one matrix.
    /// Used for both projector corner pin (table -> projector) and sensor mapping (camera -> table).
    /// Public fields so it serializes with JsonUtility / Newtonsoft as-is.
    /// </summary>
    [Serializable]
    public struct Homography
    {
        public double m00, m01, m02;
        public double m10, m11, m12;
        public double m20, m21, m22;

        public static Homography Identity => new Homography { m00 = 1, m11 = 1, m22 = 1 };

        public Homography(double[] rowMajor)
        {
            if (rowMajor == null || rowMajor.Length != 9) throw new ArgumentException("Expected 9 values (row-major 3x3).", nameof(rowMajor));
            m00 = rowMajor[0]; m01 = rowMajor[1]; m02 = rowMajor[2];
            m10 = rowMajor[3]; m11 = rowMajor[4]; m12 = rowMajor[5];
            m20 = rowMajor[6]; m21 = rowMajor[7]; m22 = rowMajor[8];
        }

        public double[] ToRowMajor() => new[] { m00, m01, m02, m10, m11, m12, m20, m21, m22 };

        /// <summary>Map a point. Returns false when the point maps to infinity (lies on the horizon line).</summary>
        public bool TryApply(double x, double y, out double ox, out double oy)
        {
            double w = m20 * x + m21 * y + m22;
            if (Math.Abs(w) < 1e-12) { ox = oy = double.NaN; return false; }
            ox = (m00 * x + m01 * y + m02) / w;
            oy = (m10 * x + m11 * y + m12) / w;
            return true;
        }

        /// <summary>Map a point. Points on the horizon line come back as NaN.</summary>
        public Vector2 Apply(Vector2 p)
        {
            TryApply(p.x, p.y, out double x, out double y);
            return new Vector2((float)x, (float)y);
        }

        /// <summary>a * b: applying the result equals applying b first, then a.</summary>
        public static Homography operator *(Homography a, Homography b)
        {
            return new Homography
            {
                m00 = a.m00 * b.m00 + a.m01 * b.m10 + a.m02 * b.m20,
                m01 = a.m00 * b.m01 + a.m01 * b.m11 + a.m02 * b.m21,
                m02 = a.m00 * b.m02 + a.m01 * b.m12 + a.m02 * b.m22,
                m10 = a.m10 * b.m00 + a.m11 * b.m10 + a.m12 * b.m20,
                m11 = a.m10 * b.m01 + a.m11 * b.m11 + a.m12 * b.m21,
                m12 = a.m10 * b.m02 + a.m11 * b.m12 + a.m12 * b.m22,
                m20 = a.m20 * b.m00 + a.m21 * b.m10 + a.m22 * b.m20,
                m21 = a.m20 * b.m01 + a.m21 * b.m11 + a.m22 * b.m21,
                m22 = a.m20 * b.m02 + a.m21 * b.m12 + a.m22 * b.m22,
            };
        }

        public bool TryInverse(out Homography inverse)
        {
            // Adjugate / determinant.
            double c00 = m11 * m22 - m12 * m21;
            double c01 = m12 * m20 - m10 * m22;
            double c02 = m10 * m21 - m11 * m20;
            double det = m00 * c00 + m01 * c01 + m02 * c02;
            double scale = MaxAbs();
            if (scale <= 0 || double.IsNaN(det) || double.IsInfinity(det) || double.IsInfinity(scale) || Math.Abs(det) < 1e-12 * scale * scale * scale)
            {
                inverse = Identity;
                return false;
            }
            double inv = 1.0 / det;
            inverse = new Homography
            {
                m00 = c00 * inv,
                m01 = (m02 * m21 - m01 * m22) * inv,
                m02 = (m01 * m12 - m02 * m11) * inv,
                m10 = c01 * inv,
                m11 = (m00 * m22 - m02 * m20) * inv,
                m12 = (m02 * m10 - m00 * m12) * inv,
                m20 = c02 * inv,
                m21 = (m01 * m20 - m00 * m21) * inv,
                m22 = (m00 * m11 - m01 * m10) * inv,
            }.Normalized();
            return true;
        }

        /// <summary>Same transform scaled so m22 = 1 (or unit max element if m22 ~ 0). Homographies are scale-invariant.</summary>
        public Homography Normalized()
        {
            double s = Math.Abs(m22) > 1e-12 ? m22 : MaxAbs();
            if (s == 0) return this;
            double k = 1.0 / s;
            return new Homography
            {
                m00 = m00 * k, m01 = m01 * k, m02 = m02 * k,
                m10 = m10 * k, m11 = m11 * k, m12 = m12 * k,
                m20 = m20 * k, m21 = m21 * k, m22 = m22 * k,
            };
        }

        private double MaxAbs()
        {
            double m = 0;
            foreach (double v in ToRowMajor()) m = Math.Max(m, Math.Abs(v));
            return m;
        }

        /// <summary>
        /// Solve the homography mapping src[i] -> dst[i]. Exactly 4 pairs gives an exact fit;
        /// more pairs gives a least-squares fit (normalized DLT). Returns false for fewer than 4 pairs,
        /// mismatched counts, or degenerate input (e.g. 3+ of 4 points on one line).
        /// </summary>
        public static bool TrySolve(IReadOnlyList<Vector2> src, IReadOnlyList<Vector2> dst, out Homography h)
        {
            h = Identity;
            if (src == null || dst == null || src.Count != dst.Count || src.Count < 4) return false;
            if (!TryNormalization(src, out Homography ts) || !TryNormalization(dst, out Homography td)) return false;
            if (!td.TryInverse(out Homography tdInv)) return false;
            // With only 4 pairs, 3 points on one line leaves no valid homography, yet the linear system
            // can still produce a (singular) answer. Reject explicitly.
            if (src.Count == 4 && (HasCollinearTriple(src, ts) || HasCollinearTriple(dst, td))) return false;

            // Unknowns h0..h7 with h8 fixed at 1. Safe after normalization: the src centroid sits at the
            // origin and maps to a finite point, so the true h8 is never 0. Accumulate normal equations.
            var ata = new double[8, 8];
            var atb = new double[8];
            var row = new double[8];
            for (int i = 0; i < src.Count; i++)
            {
                ts.TryApply(src[i].x, src[i].y, out double x, out double y);
                td.TryApply(dst[i].x, dst[i].y, out double u, out double v);

                row[0] = x; row[1] = y; row[2] = 1; row[3] = 0; row[4] = 0; row[5] = 0; row[6] = -u * x; row[7] = -u * y;
                Accumulate(ata, atb, row, u);
                row[0] = 0; row[1] = 0; row[2] = 0; row[3] = x; row[4] = y; row[5] = 1; row[6] = -v * x; row[7] = -v * y;
                Accumulate(ata, atb, row, v);
            }

            if (!SolveLinear(ata, atb, out double[] p)) return false;
            foreach (double v in p) if (double.IsNaN(v) || double.IsInfinity(v)) return false;

            var hn = new Homography
            {
                m00 = p[0], m01 = p[1], m02 = p[2],
                m10 = p[3], m11 = p[4], m12 = p[5],
                m20 = p[6], m21 = p[7], m22 = 1,
            };
            h = (tdInv * hn * ts).Normalized();
            // A singular result squashes the plane onto a line or point: never a usable calibration.
            if (!h.TryInverse(out _)) { h = Identity; return false; }
            return true;
        }

        // Triangle areas measured in normalized coordinates (mean distance sqrt(2)), so the
        // threshold means the same whatever units the points are in.
        private static bool HasCollinearTriple(IReadOnlyList<Vector2> pts, Homography norm)
        {
            var n = new Vector2[pts.Count];
            for (int i = 0; i < pts.Count; i++)
            {
                norm.TryApply(pts[i].x, pts[i].y, out double x, out double y);
                n[i] = new Vector2((float)x, (float)y);
            }
            for (int a = 0; a < n.Length; a++)
                for (int b = a + 1; b < n.Length; b++)
                    for (int c = b + 1; c < n.Length; c++)
                    {
                        double area = 0.5 * Math.Abs((n[b].x - n[a].x) * (n[c].y - n[a].y) - (n[c].x - n[a].x) * (n[b].y - n[a].y));
                        if (area < 1e-3) return true;
                    }
            return false;
        }

        /// <summary>Root-mean-square distance between h(src[i]) and dst[i], in dst units.</summary>
        public static double RmsError(Homography h, IReadOnlyList<Vector2> src, IReadOnlyList<Vector2> dst)
        {
            ErrorStats(h, src, dst, out double rms, out _);
            return rms;
        }

        /// <summary>RMS and max distance between h(src[i]) and dst[i], in dst units.</summary>
        public static void ErrorStats(Homography h, IReadOnlyList<Vector2> src, IReadOnlyList<Vector2> dst, out double rms, out double max)
        {
            rms = 0; max = 0;
            int n = Math.Min(src.Count, dst.Count);
            if (n == 0) return;
            double sum = 0;
            for (int i = 0; i < n; i++)
            {
                if (!h.TryApply(src[i].x, src[i].y, out double x, out double y)) { rms = max = double.PositiveInfinity; return; }
                double dx = x - dst[i].x, dy = y - dst[i].y;
                double d2 = dx * dx + dy * dy;
                sum += d2;
                max = Math.Max(max, Math.Sqrt(d2));
            }
            rms = Math.Sqrt(sum / n);
        }

        // Hartley normalization: move centroid to origin, scale so mean distance from it is sqrt(2).
        // Keeps the linear system well-conditioned whatever units the caller uses (0..1, pixels, cm).
        private static bool TryNormalization(IReadOnlyList<Vector2> pts, out Homography t)
        {
            t = Identity;
            double cx = 0, cy = 0;
            for (int i = 0; i < pts.Count; i++) { cx += pts[i].x; cy += pts[i].y; }
            cx /= pts.Count; cy /= pts.Count;
            double mean = 0;
            for (int i = 0; i < pts.Count; i++)
            {
                double dx = pts[i].x - cx, dy = pts[i].y - cy;
                mean += Math.Sqrt(dx * dx + dy * dy);
            }
            mean /= pts.Count;
            if (mean < 1e-12) return false;
            double s = Math.Sqrt(2.0) / mean;
            t = new Homography { m00 = s, m02 = -s * cx, m11 = s, m12 = -s * cy, m22 = 1 };
            return true;
        }

        private static void Accumulate(double[,] ata, double[] atb, double[] row, double rhs)
        {
            for (int r = 0; r < 8; r++)
            {
                if (row[r] == 0) continue;
                for (int c = 0; c < 8; c++) ata[r, c] += row[r] * row[c];
                atb[r] += row[r] * rhs;
            }
        }

        // Gaussian elimination with partial pivoting. Fails on (near-)singular systems.
        private static bool SolveLinear(double[,] a, double[] b, out double[] x)
        {
            int n = b.Length;
            x = new double[n];
            double scale = 0;
            for (int r = 0; r < n; r++)
                for (int c = 0; c < n; c++) scale = Math.Max(scale, Math.Abs(a[r, c]));
            if (scale == 0) return false;
            double eps = 1e-10 * scale;

            for (int col = 0; col < n; col++)
            {
                int pivot = col;
                for (int r = col + 1; r < n; r++)
                    if (Math.Abs(a[r, col]) > Math.Abs(a[pivot, col])) pivot = r;
                if (Math.Abs(a[pivot, col]) < eps) return false;

                if (pivot != col)
                {
                    for (int c = 0; c < n; c++) { double tmp = a[col, c]; a[col, c] = a[pivot, c]; a[pivot, c] = tmp; }
                    double tb = b[col]; b[col] = b[pivot]; b[pivot] = tb;
                }

                for (int r = col + 1; r < n; r++)
                {
                    double f = a[r, col] / a[col, col];
                    if (f == 0) continue;
                    for (int c = col; c < n; c++) a[r, c] -= f * a[col, c];
                    b[r] -= f * b[col];
                }
            }

            for (int r = n - 1; r >= 0; r--)
            {
                double s = b[r];
                for (int c = r + 1; c < n; c++) s -= a[r, c] * x[c];
                x[r] = s / a[r, r];
            }
            return true;
        }
    }
}
