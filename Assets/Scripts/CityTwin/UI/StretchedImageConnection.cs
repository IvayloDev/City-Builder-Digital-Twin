using UnityEngine;
using UnityEngine.UI;

namespace CityTwin.UI
{
    /// <summary>
    /// Default IConnectionVisual using a thin stretched UI Image.
    /// Place on a prefab with a RectTransform + Image.
    /// To replace with particles or LineRenderer, create a new MonoBehaviour implementing IConnectionVisual.
    /// </summary>
    [RequireComponent(typeof(RectTransform), typeof(Image))]
    public class StretchedImageConnection : MonoBehaviour, IConnectionVisual
    {
        [SerializeField] private float thickness = 2f;

        private RectTransform _rt;
        private Image _image;

        private void Awake()
        {
            _rt = GetComponent<RectTransform>();
            _image = GetComponent<Image>();
            _rt.pivot = new Vector2(0f, 0.5f);
            _rt.anchorMin = Vector2.zero;
            _rt.anchorMax = Vector2.zero;
        }

        public void UpdateEndpoints(Vector2 from, Vector2 to)
        {
            if (_rt == null) return;

            Vector2 delta = to - from;
            float distance = delta.magnitude;
            float angle = Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg;

            _rt.anchoredPosition = from;
            _rt.sizeDelta = new Vector2(distance, thickness);
            _rt.localRotation = Quaternion.Euler(0f, 0f, angle);
        }

        public void SetActive(bool active)
        {
            gameObject.SetActive(active);
        }
    }
}
