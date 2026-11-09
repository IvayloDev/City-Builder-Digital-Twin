using System.Collections.Generic;
using UnityEngine;
using CityTwin.Input;

namespace CityTwin.Calibration
{
    /// <summary>One running game as the F9 menu sees it: its camera and where it sits on the table.</summary>
    internal sealed class PlayArea
    {
        public TileTrackingManager Sensor;
        public Vector2 Center; // table uv
        public string Side;

        public string Name => $"Play area {Sensor.SensorIndex + 1} ({Side})";
    }

    /// <summary>Which side of the table a game is on and which projector lights it.</summary>
    public static class PlayAreaLayout
    {
        private const float CentreRadius = 0.05f;

        public static string SideOf(Vector2 tableUv)
        {
            Vector2 d = tableUv - new Vector2(0.5f, 0.5f);
            if (Mathf.Abs(d.x) < CentreRadius && Mathf.Abs(d.y) < CentreRadius) return "centre";
            if (Mathf.Abs(d.x) > Mathf.Abs(d.y)) return d.x < 0f ? "left" : "right";
            return d.y < 0f ? "bottom" : "top";
        }

        /// <summary>
        /// The projector on whose side of the seam a table point lies, or -1 with no projectors. Uses the seam line even
        /// while its cut is switched off, and ignores the warp, so the answer doesn't change while a projector is being adjusted.
        /// </summary>
        public static int ProjectorFor(Vector2 tableUv, IReadOnlyList<ProjectorCalibration> projectors)
        {
            int best = -1;
            float bestDistance = float.NegativeInfinity;
            for (int i = 0; projectors != null && i < projectors.Count; i++)
            {
                var p = projectors[i];
                if (p == null) continue;
                float magnitude = p.seamNormal.magnitude;
                float distance = magnitude > 1e-4f ? (Vector2.Dot(tableUv, p.seamNormal) - p.seamOffset) / magnitude : 0f;
                if (distance <= bestDistance) continue;
                bestDistance = distance;
                best = i;
            }
            return best;
        }
    }

    /// <summary>A copy of what decides where a projector's picture lands, to tell whether a save really moved it.</summary>
    public sealed class ProjectorGeometry
    {
        private readonly Vector2[] _tablePoints;
        private readonly Vector2[] _projectorPoints;
        private readonly Vector2[] _meshOffsets;

        public ProjectorGeometry(ProjectorCalibration projector)
        {
            _tablePoints = Copy(projector.tablePoints);
            _projectorPoints = Copy(projector.projectorPoints);
            _meshOffsets = Copy(projector.meshOffsets);
        }

        /// <summary>The seam and a mesh that is still flat don't count: neither moves the picture.</summary>
        public bool DiffersFrom(ProjectorCalibration projector)
        {
            return !Same(_tablePoints, projector.tablePoints)
                || !Same(_projectorPoints, projector.projectorPoints)
                || !SameMesh(_meshOffsets, projector.meshOffsets);
        }

        private static Vector2[] Copy(Vector2[] points) => points != null ? (Vector2[])points.Clone() : new Vector2[0];

        private static bool Same(Vector2[] a, Vector2[] b)
        {
            int length = b != null ? b.Length : 0;
            if (a.Length != length) return false;
            for (int i = 0; i < length; i++)
                if (a[i] != b[i]) return false;
            return true;
        }

        private static bool SameMesh(Vector2[] a, Vector2[] b)
        {
            if (Same(a, b)) return true;
            return IsFlat(a) && IsFlat(b);
        }

        private static bool IsFlat(Vector2[] offsets)
        {
            if (offsets == null) return true;
            foreach (var offset in offsets)
                if (offset != Vector2.zero) return false;
            return true;
        }
    }
}
