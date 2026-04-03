using UnityEngine;
using UnityEngine.UI;

[ExecuteInEditMode]
public class RoundedRadialFill : MonoBehaviour
{
    [SerializeField] private RawImage ringImage;
    [Range(0f, 1f)] public float fill = 0.75f;
    [Range(0.5f, 3f)] public float endCapScale = 1.5f;

    private Material _matInstance;

    void OnEnable()
    {
        if (ringImage != null)
        {
            _matInstance = new Material(ringImage.material);
            ringImage.material = _matInstance;
            ApplyProperties();
        }
    }

    void OnValidate()
    {
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
        _matInstance.SetFloat("_EndCapScale", endCapScale);
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