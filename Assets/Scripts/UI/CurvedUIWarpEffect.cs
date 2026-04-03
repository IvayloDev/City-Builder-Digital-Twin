using UnityEngine;
using UnityEngine.UI;

#if UNITY_EDITOR
using UnityEditor;
#endif

/// Warps a UI Graphic vertically based on its X position inside a reference RectTransform.
/// Works best on TMP text (many verts). Images need more verts (see notes below).
[RequireComponent(typeof(Graphic))]
public class CurvedUIWarpEffect : BaseMeshEffect
{
    [Header("Reference Space")]
    [Tooltip("Usually your full-width header container or the Canvas root RectTransform.")]
    public RectTransform referenceRect;

    [Header("Warp")]
    [Tooltip("Max vertical offset at the center (in local UI units / pixels).")]
    public float curvePixels = 40f;

    [Tooltip("If true uses sine curve, else parabola (faster).")]
    public bool useSine = false;

    [Tooltip("Optional: scale curve based on element's vertical position (rarely needed).")]
    [Range(0f, 1f)] public float yInfluence = 0f;

    // Cached verts list to avoid alloc
    static readonly System.Collections.Generic.List<UIVertex> Verts = new();

    public override void ModifyMesh(VertexHelper vh)
    {
        if (!IsActive() || vh.currentVertCount == 0) return;
        if (referenceRect == null) return;

        // Reference rect width in world/local space
        Rect refRect = referenceRect.rect;
        float refWidth = refRect.width;
        if (refWidth <= 0.0001f) return;

        // Convert reference rect local space to world, then compare to our verts in world
        // We'll compute normalized X across referenceRect: 0..1
        Vector3 refWorldCenter = referenceRect.TransformPoint(refRect.center);
        Vector3 refWorldLeft   = referenceRect.TransformPoint(new Vector2(refRect.xMin, refRect.center.y));
        Vector3 refWorldRight  = referenceRect.TransformPoint(new Vector2(refRect.xMax, refRect.center.y));

        float worldWidth = Vector3.Distance(refWorldLeft, refWorldRight);
        if (worldWidth <= 0.0001f) return;

        Verts.Clear();
        vh.GetUIVertexStream(Verts);

        // Transform each vert to world, compute normalized X, then apply Y offset in local space.
        // We apply offset in the Graphic's local space so masking/clipping stays sane.
        var rt = transform as RectTransform;

        for (int i = 0; i < Verts.Count; i++)
        {
            UIVertex v = Verts[i];

            // Vertex position is in local space of this Graphic
            Vector3 worldPos = rt.TransformPoint(v.position);

            // Project onto reference width line (left->right)
            float t = InverseLerpSafe(refWorldLeft, refWorldRight, worldPos); // 0..1 across the header

            float f = Curve01(t);

            float yScale = 1f;
            if (yInfluence > 0f)
            {
                // Optionally modulate based on normalized Y inside reference rect (usually keep 0)
                Vector3 refWorldBottom = referenceRect.TransformPoint(new Vector2(refRect.center.x, refRect.yMin));
                Vector3 refWorldTop    = referenceRect.TransformPoint(new Vector2(refRect.center.x, refRect.yMax));
                float ty = InverseLerpSafe(refWorldBottom, refWorldTop, worldPos);
                yScale = Mathf.Lerp(1f, ty, yInfluence);
            }

            // Apply vertical offset in LOCAL space
            v.position.y += f * curvePixels * yScale;

            Verts[i] = v;
        }

        vh.Clear();
        vh.AddUIVertexTriangleStream(Verts);
    }

    float Curve01(float t01)
    {
        t01 = Mathf.Clamp01(t01);
        if (useSine)
        {
            // 0..1..0
            return Mathf.Sin(t01 * Mathf.PI);
        }
        else
        {
            // Parabola: 0 at edges, 1 at center
            float x = t01 - 0.5f;          // -0.5..0.5
            return 1f - (4f * x * x);      // 0..1..0
        }
    }

    static float InverseLerpSafe(Vector3 a, Vector3 b, Vector3 p)
    {
        Vector3 ab = b - a;
        float denom = Vector3.Dot(ab, ab);
        if (denom <= 0.000001f) return 0f;
        float t = Vector3.Dot(p - a, ab) / denom;
        return Mathf.Clamp01(t);
    }

#if UNITY_EDITOR
    protected override void OnValidate()
    {
        base.OnValidate();
        if (!Application.isPlaying)
            graphic?.SetVerticesDirty();
    }
#endif
}