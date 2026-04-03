using UnityEngine;
using TMPro;

[ExecuteInEditMode]
public class CurvedTMPText : MonoBehaviour
{
    [SerializeField] private TMP_Text textComponent;
    [SerializeField] private float curveAmount = 10f;

    void OnEnable()
    {
        ApplyCurve();
    }

    void OnValidate()
    {
        if (textComponent != null)
        {
            textComponent.ForceMeshUpdate();
            ApplyCurve();
        }
    }

    void Update()
    {
        if (textComponent != null)
        {
            textComponent.ForceMeshUpdate();
            ApplyCurve();
        }
    }

    private void ApplyCurve()
    {
        if (textComponent == null) return;

        textComponent.ForceMeshUpdate();
        TMP_TextInfo textInfo = textComponent.textInfo;

        if (textInfo.characterCount == 0) return;

        float boundsMinX = textComponent.bounds.min.x;
        float boundsMaxX = textComponent.bounds.max.x;
        float boundsWidth = boundsMaxX - boundsMinX;

        if (boundsWidth < 0.001f) return;

        for (int i = 0; i < textInfo.characterCount; i++)
        {
            if (!textInfo.characterInfo[i].isVisible) continue;

            int vertIndex = textInfo.characterInfo[i].vertexIndex;
            int matIndex = textInfo.characterInfo[i].materialReferenceIndex;
            Vector3[] verts = textInfo.meshInfo[matIndex].vertices;

            // Character center
            float charMidX = (verts[vertIndex].x + verts[vertIndex + 2].x) / 2f;
            float charMidY = (verts[vertIndex].y + verts[vertIndex + 2].y) / 2f;
            Vector3 charCenter = new Vector3(charMidX, charMidY, 0);

            // Normalized position -1..1
            float t = (charMidX - boundsMinX) / boundsWidth * 2f - 1f;

            // Y offset: parabolic curve
            float yOffset = curveAmount * (1f - t * t);

            // Rotation: derivative of (1 - t^2) is -2t, scale to match
            float slope = -2f * t * curveAmount;
            float angle = Mathf.Atan(slope / (boundsWidth * 0.5f)) * Mathf.Rad2Deg;

            // Move verts to origin, rotate, move back + offset
            for (int j = 0; j < 4; j++)
            {
                Vector3 v = verts[vertIndex + j];

                // Translate to character center
                v -= charCenter;

                // Rotate around Z
                float rad = angle * Mathf.Deg2Rad;
                float cos = Mathf.Cos(rad);
                float sin = Mathf.Sin(rad);
                float newX = v.x * cos - v.y * sin;
                float newY = v.x * sin + v.y * cos;

                v = new Vector3(newX, newY, v.z);

                // Translate back + apply curve offset
                v += charCenter;
                v.y += yOffset;

                verts[vertIndex + j] = v;
            }
        }

        // Apply changes
        for (int i = 0; i < textInfo.meshInfo.Length; i++)
        {
            textInfo.meshInfo[i].mesh.vertices = textInfo.meshInfo[i].vertices;
            textComponent.UpdateGeometry(textInfo.meshInfo[i].mesh, i);
        }
    }
}