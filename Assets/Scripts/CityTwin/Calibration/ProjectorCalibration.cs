using System;
using System.Collections.Generic;
using UnityEngine;

namespace CityTwin.Calibration
{
    /// <summary>
    /// Everything needed to warp the table image for one projector and cut it at the seam.
    /// Coordinates: "table uv" is 0..1 over the square table render texture (0,0 = bottom-left);
    /// "projector uv" is 0..1 over this projector's output frame (0,0 = bottom-left).
    /// Kept resolution-independent so a projector swap doesn't invalidate a calibration.
    /// Plain serializable data: edited by the calibration UI, saved to JSON (phase 3).
    /// </summary>
    [Serializable]
    public class ProjectorCalibration
    {
        public string name = "Projector";

        [Header("Output")]
        [Tooltip("Unity display index. 0 = main window (use the viewport to place this projector inside a window spanning several outputs).")]
        public int displayIndex;
        [Tooltip("Where this projector's frame sits inside the display/window, normalized (like Camera.rect).")]
        public Rect viewport = new Rect(0f, 0f, 1f, 1f);
        [Tooltip("Native shape of this projector's image (width / height). The frame is letterboxed to it inside the viewport, so a preview window of another shape never squashes the picture. 0 = fill the viewport.")]
        public float aspect = 16f / 9f;

        [Header("Geometry")]
        [Tooltip("Control points on the table (table uv). Paired by index with Projector Points; 4 = exact corner pin, more = least-squares.")]
        public Vector2[] tablePoints;
        [Tooltip("Where each table control point lands in the projector frame (projector uv).")]
        public Vector2[] projectorPoints;

        [Header("Mesh warp (optional fine correction)")]
        [Tooltip("Mesh cells across the projector frame. 0 = mesh warp off.")]
        public int meshColumns;
        [Tooltip("Mesh cells up the projector frame. 0 = mesh warp off.")]
        public int meshRows;
        [Tooltip("Per-node offsets in projector uv, (meshColumns+1) x (meshRows+1), row-major from the frame's bottom-left.")]
        public Vector2[] meshOffsets;

        [Header("Seam (hard cut, no blending)")]
        [Tooltip("Black out the part of the table beyond the seam. The seam sits in the black gap between the game wedges, so no edge blend is needed. Off = draw the whole frame, handy for checking the overlap lines up.")]
        public bool seamEnabled = true;
        [Tooltip("Unit table-uv direction from the seam into the side this projector keeps. (-0.707,0.707) = seam on the bottom-left to top-right diagonal, keeping the top-left side.")]
        public Vector2 seamNormal = new Vector2(-0.70710678f, 0.70710678f);
        [Tooltip("Seam position: this projector draws table uv where dot(uv, seamNormal) >= seamOffset. 0 = through the table centre.")]
        public float seamOffset;

        [Header("Mask")]
        [Tooltip("Black out everything outside this radius (table uv, centred on the table). 0 = off; 0.5 = circle touching the texture edges.")]
        public float circleMaskRadius;
        [Tooltip("Soft edge width of the circle mask, in table uv.")]
        public float circleMaskFeather = 0.004f;

        /// <summary>Solve the table -> projector homography from the control points.</summary>
        public bool TryGetTableToProjector(out Homography h)
        {
            return Homography.TrySolve(tablePoints, projectorPoints, out h);
        }

        public bool HasMeshWarp =>
            meshColumns > 0 && meshRows > 0 && meshOffsets != null &&
            meshOffsets.Length == (meshColumns + 1) * (meshRows + 1);

        /// <summary>True if this projector draws the table point: on its side of the seam, or the seam cut is off. Mirrors ProjectorWarp.shader.</summary>
        public bool KeepsTablePoint(Vector2 tableUv) => !seamEnabled || Vector2.Dot(tableUv, seamNormal) >= seamOffset;

        /// <summary>Back to another calibration's corner points, with no mesh. Which output this projector drives is kept.</summary>
        public void ResetGeometryTo(ProjectorCalibration defaults)
        {
            tablePoints = (Vector2[])defaults.tablePoints.Clone();
            projectorPoints = (Vector2[])defaults.projectorPoints.Clone();
            meshColumns = 0;
            meshRows = 0;
            meshOffsets = null;
        }

        public void ResetSeamTo(ProjectorCalibration defaults)
        {
            seamEnabled = defaults.seamEnabled;
            seamNormal = defaults.seamNormal;
            seamOffset = defaults.seamOffset;
        }

        /// <summary>Centre of play area 1 (the bottom wedge's bounding box) in table uv; the wedge spans x 0.17-0.83, y 0.04-0.36.</summary>
        public static readonly Vector2 PlayArea1Center = new Vector2(0.5f, 0.2025f);

        /// <summary>
        /// Where the pilot frame is centred: 0.04 table uv below the zone's centre, so the zone sits higher on screen and
        /// the palette pins in the bottom corners are fully in frame instead of empty black above the zone.
        /// </summary>
        public static readonly Vector2 PilotFrameCenter = new Vector2(0.5f, 0.1625f);

        /// <summary>
        /// Placeholder layout for the pilot ("1 screen, 1 play area"): one projector on play area 1, the bottom game,
        /// like DemoScene's single-zone view. The frame is a 16:9 window onto the table, 0.76 table uv (2.3 m) wide,
        /// holding the whole wedge plus its palette pins; the black around the wedge is the game's own mask. No seam.
        /// Calibration on site replaces the points (the corner pin also absorbs another aspect).
        /// </summary>
        public static List<ProjectorCalibration> DefaultSingleProjectorLayout()
        {
            const float width = 0.76f, height = width * 9f / 16f;
            float left = PilotFrameCenter.x - width * 0.5f, right = PilotFrameCenter.x + width * 0.5f;
            float bottom = PilotFrameCenter.y - height * 0.5f, top = PilotFrameCenter.y + height * 0.5f;
            return new List<ProjectorCalibration>
            {
                new ProjectorCalibration
                {
                    name = "Projector (play area 1)",
                    viewport = new Rect(0f, 0f, 1f, 1f),
                    seamEnabled = false,
                    tablePoints = new[] { new Vector2(left, bottom), new Vector2(right, bottom), new Vector2(right, top), new Vector2(left, top) },
                    projectorPoints = new[] { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f) },
                },
            };
        }

        /// <summary>
        /// Placeholder layout: two projectors facing each other across the table, one per half, split by a hard
        /// seam on the bottom-left to top-right diagonal. That diagonal runs down the middle of the black gaps between
        /// the four game wedges (7-10 cm wide), so each game is lit by one projector and nothing needs blending:
        /// A gets the top and left games, B the bottom and right ones. Each frame reaches a little past the seam
        /// so the cut, not the frame edge, decides where the image stops.
        /// Assumes one window spanning both outputs side by side. Calibration on site replaces the points.
        /// </summary>
        public static List<ProjectorCalibration> DefaultTwoProjectorLayout()
        {
            const float overlap = 0.02f; // frame reaches 6 cm past the seam
            var center = new Vector2(0.5f, 0.5f);
            var seam = new Vector2(1f, 1f).normalized;    // along the seam, bottom-left -> top-right
            var across = new Vector2(-1f, 1f).normalized; // across the seam, towards the top + left games

            var a = HalfTable("A (top + left)", new Rect(0f, 0f, 0.5f, 1f), center, seam, across, overlap);
            a.seamNormal = across;

            // Hung on the opposite side, so its image is rotated 180 degrees relative to A.
            var b = HalfTable("B (bottom + right)", new Rect(0.5f, 0f, 0.5f, 1f), center, -seam, -across, overlap);
            b.seamNormal = -across;

            return new List<ProjectorCalibration> { a, b };
        }

        // Frame bottom edge runs along the seam, overlap past it; frame top touches the far rim.
        private static ProjectorCalibration HalfTable(string name, Rect viewport, Vector2 center, Vector2 along, Vector2 outward, float overlap)
        {
            return new ProjectorCalibration
            {
                name = name,
                viewport = viewport,
                tablePoints = new[]
                {
                    center - 0.5f * along - overlap * outward,
                    center + 0.5f * along - overlap * outward,
                    center + 0.5f * along + 0.5f * outward,
                    center - 0.5f * along + 0.5f * outward,
                },
                projectorPoints = new[] { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f) },
            };
        }
    }
}
