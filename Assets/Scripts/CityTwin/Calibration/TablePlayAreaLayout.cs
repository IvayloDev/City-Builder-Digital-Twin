using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using CityTwin.Core;

namespace CityTwin.Calibration
{
    /// <summary>
    /// Seats the four game instances around one circle when the two-projector setup is on.
    /// Each copy of the single-player fan is one wedge: bottom, right, top, left. Adjacent wedges
    /// make a half circle, and the diagonal seam gives each projector two play areas
    /// (bottom + right, and top + left). The one-projector setup keeps the positions saved in the scene.
    /// </summary>
    public static class TablePlayAreaLayout
    {
        /// <summary>Z rotation that swings the downward fan onto this instance's side of the table (bottom, right, top, left). 0 for an unknown id.</summary>
        public static float WedgeAngle(int instanceId)
        {
            switch (instanceId)
            {
                case 0: return 0f;
                case 1: return 90f;
                case 2: return 180f;
                case 3: return -90f;
                default: return 0f;
            }
        }

        /// <summary>Root pivot that puts this wedge's circle centre on <paramref name="tableCenter"/>.</summary>
        public static Vector2 PivotFor(int instanceId, Vector2 circleCenterInRoot, Vector2 tableCenter)
        {
            var rot = Quaternion.Euler(0f, 0f, WedgeAngle(instanceId));
            return tableCenter - (Vector2)(rot * (Vector3)circleCenterInRoot);
        }

        /// <summary>
        /// Move every running instance onto its wedge. Returns how many were moved.
        /// Does nothing for a one-play-area setup, so the pilot keeps the scene layout.
        /// </summary>
        public static int Arrange(TableSetup setup, IReadOnlyList<GameInstanceRoot> roots)
        {
            if (setup == null || roots == null || CountRunning(setup) < 2) return 0;

            GameInstanceRoot sample = null;
            for (int i = 0; i < roots.Count; i++)
            {
                var root = roots[i];
                if (root == null || !setup.RunsPlayArea(root.InstanceId)) continue;
                if (sample == null || root.InstanceId < sample.InstanceId) sample = root;
            }
            if (sample == null || !TryCircleCenterInRoot(sample, out Vector2 circleCenter, out float radius))
            {
                Debug.LogWarning("[TablePlayAreaLayout] Could not find the fan's circle centre; leaving the scene positions.");
                return 0;
            }

            int placed = 0;
            var tableCenter = Vector2.zero;
            for (int i = 0; i < roots.Count; i++)
            {
                var root = roots[i];
                if (root == null || root.InstanceId < 0 || root.InstanceId > 3) continue;
                if (!setup.RunsPlayArea(root.InstanceId)) continue;
                var rt = root.GetComponent<RectTransform>();
                if (rt == null) continue;
                rt.localRotation = Quaternion.Euler(0f, 0f, WedgeAngle(root.InstanceId));
                rt.anchoredPosition = PivotFor(root.InstanceId, circleCenter, tableCenter);
                placed++;
            }
            Debug.Log($"[TablePlayAreaLayout] {placed} play areas around the table (circle centre {circleCenter}, radius {radius:F0}). " +
                      "Bottom+right share one projector, top+left the other.");
            return placed;
        }

        /// <summary>
        /// The four-table setup shows the fans only. Hides the playtest chrome around each running game:
        /// the building pins, the clear-drop zone, and the restart and calibration (config) buttons.
        /// </summary>
        public static void HideSurroundings(IReadOnlyList<GameInstanceRoot> roots, TableSetup setup)
        {
            if (roots == null || setup == null || CountRunning(setup) < 2) return;
            for (int i = 0; i < roots.Count; i++)
            {
                var root = roots[i];
                if (root == null || !setup.RunsPlayArea(root.InstanceId)) continue;
                var transforms = root.GetComponentsInChildren<Transform>(true);
                for (int t = 0; t < transforms.Length; t++)
                {
                    if (transforms[t] == null || !IsSurroundingChrome(transforms[t].name)) continue;
                    if (transforms[t].gameObject.activeSelf) transforms[t].gameObject.SetActive(false);
                }
            }
        }

        // Playtest UI owns the pins, the clear zone and the restart button. Calibration is cloned under it at runtime.
        private static bool IsSurroundingChrome(string name)
        {
            return name == "Playtest UI"
                || name == "Building Palette"
                || name == "Clear Drop Zone"
                || name == "Restart Button"
                || name == "Calibration Button"
                || (name.StartsWith("Pin ") && name.Length > 4);
        }

        /// <summary>Circle fit. Public so tests can check a known circle comes back.</summary>
        public static bool TryFitCircle(IReadOnlyList<Vector2> points, out Vector2 center, out float radius)
        {
            center = default;
            radius = 0f;
            if (points == null || points.Count < 8) return false;

            double sx = 0, sy = 0, sxx = 0, syy = 0, sxy = 0, sxz = 0, syz = 0, sz = 0;
            for (int i = 0; i < points.Count; i++)
            {
                double x = points[i].x, y = points[i].y, z = x * x + y * y;
                sx += x; sy += y; sxx += x * x; syy += y * y; sxy += x * y;
                sxz += x * z; syz += y * z; sz += z;
            }
            double n = points.Count;
            double det = Det(sxx, sxy, sx, sxy, syy, sy, sx, sy, n);
            if (Math.Abs(det) < 1e-4) return false;
            double a = Det(sxz, sxy, sx, syz, syy, sy, sz, sy, n) / det;
            double b = Det(sxx, sxz, sx, sxy, syz, sy, sx, sz, n) / det;
            double c = Det(sxx, sxy, sxz, sxy, syy, syz, sx, sy, sz) / det;
            double r2 = (a * a + b * b) * 0.25 + c;
            if (r2 <= 1.0) return false;
            center = new Vector2((float)(a * 0.5), (float)(b * 0.5));
            radius = (float)Math.Sqrt(r2);
            return true;
        }

        private static int CountRunning(TableSetup setup)
        {
            int count = 0;
            for (int i = 0; i < 4; i++)
                if (setup.RunsPlayArea(i)) count++;
            return count;
        }

        // Outer arc of the play-area fan, in the instance root's local space. The root pivot sits near that centre.
        private static bool TryCircleCenterInRoot(GameInstanceRoot root, out Vector2 center, out float radius)
        {
            center = default;
            radius = 0f;
            var art = FindPlayAreaArt(root);
            if (art == null || art.sprite == null) return false;
            var tex = art.sprite.texture;
            if (tex == null || !tex.isReadable) return false;

            var rt = art.rectTransform;
            Rect tr = art.sprite.textureRect;
            DrawnSize(rt, art.sprite, art.preserveAspect, out float drawnW, out float drawnH);
            Vector2 rectCenter = rt.rect.center;
            Color32[] pixels = tex.GetPixels32();
            int width = tex.width;
            int xStart = Mathf.Clamp(Mathf.RoundToInt(tr.xMin), 0, width - 1);
            int xEnd = Mathf.Clamp(Mathf.RoundToInt(tr.xMax), 0, width);
            int yStart = Mathf.Clamp(Mathf.RoundToInt(tr.yMin), 0, tex.height - 1);
            int yEnd = Mathf.Clamp(Mathf.RoundToInt(tr.yMax), 0, tex.height);

            var edge = new List<Vector2>(256);
            const int step = 8;
            for (int x = xStart; x < xEnd; x += step)
            {
                int ymin = -1;
                for (int y = yStart; y < yEnd; y++)
                {
                    if (!Opaque(pixels[y * width + x])) continue;
                    ymin = y;
                    break;
                }
                if (ymin < 0) continue;
                float u = tr.width > 1f ? (x - tr.xMin) / tr.width : 0.5f;
                float v = tr.height > 1f ? (ymin - tr.yMin) / tr.height : 0.5f;
                var local = new Vector3(rectCenter.x + (u - 0.5f) * drawnW, rectCenter.y + (v - 0.5f) * drawnH, 0f);
                Vector3 inRoot = root.transform.InverseTransformPoint(rt.TransformPoint(local));
                edge.Add(inRoot);
            }
            if (!TryFitCircle(edge, out center, out radius)) return false;

            float error = 0f;
            for (int i = 0; i < edge.Count; i++) error += Mathf.Abs(Vector2.Distance(edge[i], center) - radius);
            error /= edge.Count;
            // The prefab root is built around this centre, so a fit that lands far away is the wrong edge.
            if (error > 40f || radius < 400f || radius > 4000f || center.magnitude > 800f) return false;
            return true;
        }

        private static Image FindPlayAreaArt(GameInstanceRoot root)
        {
            var images = root.GetComponentsInChildren<Image>(true);
            Image found = null;
            for (int i = 0; i < images.Length; i++)
            {
                if (images[i].name != "Main BG") continue;
                if (Under(images[i].transform, "End Screen")) continue;
                found = images[i];
                break;
            }
            return found;
        }

        private static bool Under(Transform t, string ancestorName)
        {
            while (t != null)
            {
                if (t.name == ancestorName) return true;
                t = t.parent;
            }
            return false;
        }

        private static void DrawnSize(RectTransform rt, Sprite sprite, bool preserveAspect, out float width, out float height)
        {
            Rect rect = rt.rect;
            width = rect.width;
            height = rect.height;
            if (!preserveAspect || height < 1f || sprite.rect.height < 1f) return;
            float spriteAspect = sprite.rect.width / sprite.rect.height;
            float rectAspect = width / height;
            if (rectAspect > spriteAspect) width = height * spriteAspect;
            else height = width / spriteAspect;
        }

        private static bool Opaque(Color32 c)
        {
            if (c.a < 89) return false; // TableBounds alpha threshold, 0.35
            byte max = c.r > c.g ? c.r : c.g;
            if (c.b > max) max = c.b;
            return max >= 15; // darker than this is the shadow band, not the map
        }

        private static double Det(double a11, double a12, double a13, double a21, double a22, double a23, double a31, double a32, double a33)
        {
            return a11 * (a22 * a33 - a23 * a32) - a12 * (a21 * a33 - a23 * a31) + a13 * (a21 * a32 - a22 * a31);
        }
    }
}
