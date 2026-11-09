using UnityEngine;

namespace CityTwin.Calibration
{
    /// <summary>Pure math behind the projector warp mesh, kept free of GameObjects so it is testable.</summary>
    public static class ProjectionMath
    {
        /// <summary>Bilinear mesh-warp offset (projector uv) at projector-frame position (s, t).</summary>
        public static Vector2 MeshOffset(ProjectorCalibration cal, float s, float t)
        {
            if (!cal.HasMeshWarp) return Vector2.zero;
            int cols = cal.meshColumns, rows = cal.meshRows;
            float fx = Mathf.Clamp01(s) * cols, fy = Mathf.Clamp01(t) * rows;
            int x0 = Mathf.Min((int)fx, cols - 1), y0 = Mathf.Min((int)fy, rows - 1);
            float ax = fx - x0, ay = fy - y0;
            int stride = cols + 1;
            Vector2 a = cal.meshOffsets[y0 * stride + x0];
            Vector2 b = cal.meshOffsets[y0 * stride + x0 + 1];
            Vector2 c = cal.meshOffsets[(y0 + 1) * stride + x0];
            Vector2 d = cal.meshOffsets[(y0 + 1) * stride + x0 + 1];
            return Vector2.Lerp(Vector2.Lerp(a, b, ax), Vector2.Lerp(c, d, ax), ay);
        }

        /// <summary>Unwarped position of mesh-warp node <paramref name="index"/>: a regular grid over the projector frame, row-major from bottom-left.</summary>
        public static Vector2 MeshNodeRestUv(ProjectorCalibration cal, int index)
        {
            int cols = Mathf.Max(1, cal.meshColumns), rows = Mathf.Max(1, cal.meshRows);
            int stride = cols + 1;
            return new Vector2((index % stride) / (float)cols, (index / stride) / (float)rows);
        }

        /// <summary>Where mesh-warp node <paramref name="index"/> currently sits in the projector frame (projector uv).</summary>
        public static Vector2 MeshNodeProjectorUv(ProjectorCalibration cal, int index)
        {
            Vector2 p = MeshNodeRestUv(cal, index);
            if (cal.HasMeshWarp && index >= 0 && index < cal.meshOffsets.Length) p += cal.meshOffsets[index];
            return p;
        }

        /// <summary>
        /// Fill a (cols+1) x (rows+1) grid over the projector frame.
        /// UV = the table point the homography puts there; position = the frame point plus the mesh-warp offset,
        /// laid out for an orthographic camera of size 0.5 whose frame spans x in [-aspect/2, aspect/2], y in [-0.5, 0.5].
        /// Gridding the frame (not the table) keeps every node on screen whatever the seam angle; the shader
        /// blacks out the part of the frame beyond the seam. The grid is dense enough that per-triangle linear
        /// interpolation looks perspective-correct.
        /// </summary>
        public static void BuildWarpVertices(ProjectorCalibration cal, Homography projectorToTable, int cols, int rows, float aspect,
            Vector3[] vertices, Vector2[] uvs)
        {
            int i = 0;
            for (int y = 0; y <= rows; y++)
            {
                float t = y / (float)rows;
                for (int x = 0; x <= cols; x++, i++)
                {
                    float s = x / (float)cols;
                    if (projectorToTable.TryApply(s, t, out double u, out double v))
                    {
                        uvs[i] = new Vector2((float)u, (float)v);
                        vertices[i] = ProjectorUvToLocal(new Vector2(s, t) + MeshOffset(cal, s, t), aspect);
                    }
                    else
                    {
                        uvs[i] = Vector2.zero;
                        vertices[i] = ProjectorUvToLocal(new Vector2(-10f, -10f), aspect); // on the horizon line: park far off-frame
                    }
                }
            }
        }

        /// <summary>Where a table point appears in the projector frame: homography, then the mesh-warp offset at that spot.</summary>
        public static Vector2 TableUvToProjectorUv(ProjectorCalibration cal, Homography tableToProjector, Vector2 tableUv)
        {
            Vector2 rest = tableToProjector.Apply(tableUv);
            return rest + MeshOffset(cal, rest.x, rest.y);
        }

        /// <summary>
        /// Inverse of <see cref="TableUvToProjectorUv"/>: which table point shows at a spot of the projector frame.
        /// The mesh offset is undone by fixed-point iteration (offsets are small and smooth), then the homography.
        /// </summary>
        public static Vector2 ProjectorUvToTableUv(ProjectorCalibration cal, Homography projectorToTable, Vector2 projectorUv)
        {
            Vector2 rest = projectorUv;
            if (cal.HasMeshWarp)
                for (int i = 0; i < 6; i++) rest = projectorUv - MeshOffset(cal, rest.x, rest.y);
            return projectorToTable.Apply(rest);
        }

        /// <summary>
        /// Largest rect of <paramref name="aspect"/> (width / height, in pixels) centred inside a normalized viewport on a
        /// target of <paramref name="targetPixels"/>. Used to letterbox a projector frame; aspect &lt;= 0 returns the viewport.
        /// </summary>
        public static Rect FitAspect(Rect viewport, Vector2 targetPixels, float aspect)
        {
            float w = viewport.width * targetPixels.x, h = viewport.height * targetPixels.y;
            if (aspect <= 0f || w <= 0f || h <= 0f) return viewport;
            float current = w / h;
            if (Mathf.Abs(current - aspect) <= 1e-4f * aspect) return viewport;
            if (current > aspect)
            {
                float width = viewport.width * aspect / current;
                return new Rect(viewport.x + (viewport.width - width) * 0.5f, viewport.y, width, viewport.height);
            }
            float height = viewport.height * current / aspect;
            return new Rect(viewport.x, viewport.y + (viewport.height - height) * 0.5f, viewport.width, height);
        }

        /// <summary>Projector uv (0..1) -> local position in the output camera's view (see <see cref="BuildWarpVertices"/>).</summary>
        public static Vector3 ProjectorUvToLocal(Vector2 projectorUv, float aspect)
        {
            return new Vector3((projectorUv.x - 0.5f) * aspect, projectorUv.y - 0.5f, 0f);
        }

        /// <summary>Triangle indices for a (cols+1) x (rows+1) vertex grid, two triangles per cell.</summary>
        public static int[] BuildGridTriangles(int cols, int rows)
        {
            var tris = new int[cols * rows * 6];
            int stride = cols + 1, k = 0;
            for (int y = 0; y < rows; y++)
                for (int x = 0; x < cols; x++)
                {
                    int a = y * stride + x, b = a + 1, c = a + stride, d = c + 1;
                    tris[k++] = a; tris[k++] = c; tris[k++] = b;
                    tris[k++] = b; tris[k++] = c; tris[k++] = d;
                }
            return tris;
        }
    }
}
