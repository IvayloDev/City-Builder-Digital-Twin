using UnityEngine;
using UnityEngine.UI;

[ExecuteInEditMode]
public class CurvedBarFill : MonoBehaviour
{
    [SerializeField] private RawImage barImage;
    [Range(0f, 1f)] public float fill = 0.7f;
    [Range(0f, 0.6f)] public float capSize = 0.05f;
    public float aspect = 5.0f;

    private Material _matInstance;

    void OnEnable()
    {
        if (barImage != null)
        {
            _matInstance = new Material(barImage.material);
            barImage.material = _matInstance;
            ApplyProperties();
        }
    }

    void OnValidate()
    {
        // Auto-calculate aspect from texture if available
        if (barImage != null && barImage.texture != null)
        {
            aspect = (float)barImage.texture.width / barImage.texture.height;
        }
        ApplyProperties();
    }

    void Update()
    {
        ApplyProperties();
    }

    private void ApplyProperties()
    {
        if (_matInstance == null) return;
        _matInstance.SetFloat("_Fill", fill);
        _matInstance.SetFloat("_CapSize", capSize);
        _matInstance.SetFloat("_Aspect", aspect);
    }

    void OnDestroy()
    {
        if (_matInstance != null)
        {
            if (Application.isPlaying)
                Destroy(_matInstance);
            else
                DestroyImmediate(_matInstance);
        }
    }
}