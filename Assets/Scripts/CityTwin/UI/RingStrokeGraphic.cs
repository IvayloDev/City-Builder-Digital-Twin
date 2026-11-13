using UnityEngine;
using UnityEngine.UI;

namespace CityTwin.UI
{
    /// <summary>
    /// A crisp circular stroke (annulus) drawn as a mesh: the stroke keeps its pixel width at any radius, unlike a
    /// scaled ring sprite. Radius and width are in the graphic's local units; the rect size does not matter.
    /// </summary>
    [RequireComponent(typeof(CanvasRenderer))]
    public class RingStrokeGraphic : MaskableGraphic
    {
        [SerializeField] private float radius = 50f;
        [SerializeField] private float strokeWidth = 4f;
        [Tooltip("Soft edge on both sides of the stroke, in local units (anti-aliasing).")]
        [SerializeField] private float feather = 1.5f;
        [SerializeField, Range(16, 256)] private int segments = 96;

        public float Radius
        {
            get => radius;
            set { if (!Mathf.Approximately(radius, value)) { radius = value; SetVerticesDirty(); } }
        }

        public float StrokeWidth
        {
            get => strokeWidth;
            set { if (!Mathf.Approximately(strokeWidth, value)) { strokeWidth = value; SetVerticesDirty(); } }
        }

        public float Feather
        {
            get => feather;
            set { if (!Mathf.Approximately(feather, value)) { feather = value; SetVerticesDirty(); } }
        }

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            float half = Mathf.Max(0.05f, strokeWidth * 0.5f);
            float f = Mathf.Max(0f, feather);
            float r = Mathf.Max(0f, radius);
            // Four rings of vertices: inner fade, inner solid, outer solid, outer fade.
            float r0 = Mathf.Max(0f, r - half - f), r1 = Mathf.Max(0f, r - half), r2 = r + half, r3 = r + half + f;
            Color32 solid = color;
            Color32 clear = new Color32(solid.r, solid.g, solid.b, 0);
            int n = Mathf.Clamp(segments, 16, 256);
            for (int i = 0; i <= n; i++)
            {
                float a = i * Mathf.PI * 2f / n;
                var d = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
                vh.AddVert(d * r0, clear, Vector2.zero);
                vh.AddVert(d * r1, solid, Vector2.zero);
                vh.AddVert(d * r2, solid, Vector2.zero);
                vh.AddVert(d * r3, clear, Vector2.zero);
            }
            for (int i = 0; i < n; i++)
            {
                int b = i * 4, c = (i + 1) * 4;
                for (int k = 0; k < 3; k++)
                {
                    vh.AddTriangle(b + k, c + k, c + k + 1);
                    vh.AddTriangle(b + k, c + k + 1, b + k + 1);
                }
            }
        }
    }
}
