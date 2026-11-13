using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using CityTwin.Core;

namespace CityTwin.UI
{
    /// <summary>
    /// Pin lift / drop UX. A physical tile's OSC data is lost the moment it is lifted, and it reappears somewhere else
    /// when it is put down; the mouse mirrors that: a grabbed pin turns darker and smaller (<see cref="Hold"/>) and on release it
    /// drops back in (<see cref="Release"/>): a short scale-in plus circular strokes ("echoes") that grow out of the
    /// pin and fade away within the ripple radius. Every new building marker (TUIO, palette or mouse) also drops in.
    /// Lives on the Game Instance root next to <see cref="PlacementFxController"/>, which adds it when missing.
    /// </summary>
    public class PinDropFx : MonoBehaviour
    {
        [Header("Echoes (circular strokes)")]
        [Tooltip("How many rings each drop emits.")]
        [SerializeField, Range(1, 8)] private int echoes = 2;
        [Tooltip("Seconds between two rings.")]
        [SerializeField, Range(0f, 1f)] private float echoInterval = 0.16f;
        [Tooltip("Seconds one ring lives at speed 1, from the pin's edge to the ripple radius.")]
        [SerializeField, Range(0.1f, 4f)] private float ringLifetime = 0.7f;
        [Tooltip("Plays the whole effect faster (>1) or slower (<1).")]
        [SerializeField, Range(0.1f, 4f)] private float speed = 1f;
        [Tooltip("How fast a ring expands early on: 1 = constant speed, higher = bursts out then slows towards the radius.")]
        [SerializeField, Range(1f, 8f)] private float expansionRate = 2.2f;
        [Tooltip("How fast a ring fades over its life: 1 = linear, higher = fades sooner, lower = stays bright longer.")]
        [SerializeField, Range(0.2f, 6f)] private float decay = 2f;
        [Tooltip("Each echo is this much fainter than the one before it.")]
        [SerializeField, Range(0f, 1f)] private float echoDecay = 0.5f;
        [Tooltip("Each echo reaches this fraction of the previous echo's radius, so the echoes die inside the first ring.")]
        [SerializeField, Range(0.3f, 1f)] private float echoRadiusFalloff = 0.85f;

        [Header("Ring shape")]
        [Tooltip("Ripple radius as a multiple of the pin's visual radius.")]
        [SerializeField, Range(0.5f, 6f)] private float radiusScale = 0.75f;
        [Tooltip("Ripple radius when the pin's size is unknown, in content units.")]
        [SerializeField] private float fallbackRadius = 45f;
        [Tooltip("Where a ring starts, as a fraction of the pin's visual radius.")]
        [SerializeField, Range(0f, 2f)] private float startRadiusScale = 0.4f;
        [SerializeField, Range(0.5f, 20f)] private float strokeStart = 2.5f;
        [SerializeField, Range(0f, 20f)] private float strokeEnd = 0.5f;
        [SerializeField, Range(0f, 1f)] private float startAlpha = 0.4f;
        [Tooltip("On: rings take the building's category colour. Off: the fixed colour below.")]
        [SerializeField] private bool tintByCategory = true;
        [SerializeField] private Color ringColor = new Color(0.75f, 0.95f, 1f, 1f);

        [Header("Pin drop")]
        [Tooltip("Pin scale when it appears; it settles to its normal size.")]
        [SerializeField, Range(1f, 2.5f)] private float dropScaleFrom = 1.12f;
        [SerializeField, Range(0.05f, 1f)] private float dropDuration = 0.24f;
        [Tooltip("Overshoot of the settle (0 = none).")]
        [SerializeField, Range(0f, 3f)] private float dropOvershoot = 0.5f;

        [Header("Water (liquid map surface)")]
        [Tooltip("Each drop also splashes the liquid map under the pin: tiles are put down, never dragged, so this is what makes the water move.")]
        [SerializeField] private bool splashWater = true;
        [Tooltip("Splash size, as a multiple of the liquid surface's splat radius.")]
        [SerializeField, Range(0.5f, 6f)] private float splashRadius = 2.5f;
        [SerializeField, Range(0f, 5f)] private float splashStrength = 1.6f;
        [Tooltip("Seconds the splash keeps pushing the water (it eases out).")]
        [SerializeField, Range(0.1f, 3f)] private float splashDuration = 0.8f;
        [Tooltip("Strength of the softer splash with each game-over pulse (0 = none).")]
        [SerializeField, Range(0f, 5f)] private float pulseSplashStrength = 0.6f;

        [Header("Game over: pulse until removed")]
        [Tooltip("When the game is over, every pin still on the table pulses with the ripple until it is lifted.")]
        [SerializeField] private bool pulseUntilRemoved = true;
        [Tooltip("Seconds between two pulses.")]
        [SerializeField, Range(0.3f, 5f)] private float pulseInterval = 1.4f;
        [Tooltip("Pulse ring colour.")]
        [SerializeField] private Color pulseColor = new Color(0.75f, 0.95f, 1f, 1f);
        [Tooltip("The pulses draw on their own layer at this sorting order, above the end screen (4) and below the simulated tiles (10).")]
        [SerializeField] private int pulseSortingOrder = 9;

        [Header("Held pin (mouse drag)")]
        [Tooltip("Opacity of a pin while it is dragged (lower = darker over the dark map).")]
        [SerializeField, Range(0f, 1f)] private float heldAlpha = 0.45f;
        [Tooltip("Size of a pin while it is dragged.")]
        [SerializeField, Range(0.3f, 1f)] private float heldScale = 0.75f;

        /// <summary>Dragged-pin look, for drag ghosts drawn elsewhere (the palette).</summary>
        public float HeldAlpha => heldAlpha;
        public float HeldScale => heldScale;

        private readonly Dictionary<Transform, Vector3> _baseScale = new Dictionary<Transform, Vector3>();

        private static readonly List<PinDropFx> Instances = new List<PinDropFx>();
        private readonly HashSet<Transform> _held = new HashSet<Transform>();
        private readonly Dictionary<Transform, Coroutine> _drops = new Dictionary<Transform, Coroutine>();

        private void OnEnable() { if (!Instances.Contains(this)) Instances.Add(this); }

        private RestartFlowController _restart;
        private LiquidSurface _liquid;
        private bool _liquidSearched;

        private void SplashWater(Transform pin, float strength)
        {
            if (!splashWater || pin == null || strength <= 0f) return;
            if (_liquid == null && !_liquidSearched)
            {
                _liquidSearched = true;
                _liquid = GetComponentInChildren<LiquidSurface>(true) ?? GetComponentInParent<LiquidSurface>();
                if (_liquid == null)
                {
                    // The surface may live in the UI game instance next to this one: take the one whose branch holds the pin.
                    foreach (var surface in FindObjectsByType<LiquidSurface>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
                    {
                        Transform branch = surface.transform.parent != null ? surface.transform.parent : surface.transform;
                        if (pin.IsChildOf(branch)) { _liquid = surface; break; }
                        if (_liquid == null) _liquid = surface;
                    }
                }
            }
            if (_liquid != null && _liquid.isActiveAndEnabled) _liquid.Splash(pin.position, splashRadius, strength, splashDuration);
        }
        private BuildingSpawner _spawner;
        private RectTransform _pulseLayer;
        private float _nextPulse;

        // Game over: the end screen asks to return the tiles; each pin still on the table keeps pulsing until lifted.
        private void Update()
        {
            if (!pulseUntilRemoved) return;
            if (_restart == null) _restart = GetComponentInChildren<RestartFlowController>(true) ?? GetComponentInParent<RestartFlowController>();
            if (_spawner == null) _spawner = GetComponentInChildren<BuildingSpawner>(true) ?? GetComponentInParent<BuildingSpawner>();
            if (_restart == null || _spawner == null || !_restart.IsWaitingForTiles) { _nextPulse = 0f; return; }
            if (Time.unscaledTime < _nextPulse) return;
            _nextPulse = Time.unscaledTime + pulseInterval;
            foreach (var marker in _spawner.SpawnedMarkers)
                if (marker != null && marker.transform.parent != null)
                {
                    EmitRipple(marker.transform, pulseColor, PulseLayer(marker.transform.parent));
                    SplashWater(marker.transform, pulseSplashStrength);
                }
        }

        // A full-size layer over the markers with its own canvas, so the pulses show above the end screen.
        private RectTransform PulseLayer(Transform space)
        {
            if (_pulseLayer != null && _pulseLayer.parent == space) return _pulseLayer;
            var go = new GameObject("PinPulseLayer", typeof(RectTransform));
            _pulseLayer = (RectTransform)go.transform;
            _pulseLayer.SetParent(space, false);
            _pulseLayer.anchorMin = Vector2.zero;
            _pulseLayer.anchorMax = Vector2.one;
            _pulseLayer.offsetMin = Vector2.zero;
            _pulseLayer.offsetMax = Vector2.zero;
            _pulseLayer.SetAsLastSibling();
            var canvas = go.AddComponent<Canvas>();
            canvas.overrideSorting = true;
            canvas.sortingOrder = pulseSortingOrder;
            return _pulseLayer;
        }
        private void OnDisable() { Instances.Remove(this); }

        /// <summary>The effect for a pin: the instance whose game owns it, else any.</summary>
        public static PinDropFx Find(Transform pin)
        {
            if (pin != null)
                for (int i = 0; i < Instances.Count; i++)
                    if (Instances[i] != null && pin.IsChildOf(Instances[i].transform)) return Instances[i];
            return Instances.Count > 0 ? Instances[0] : null;
        }

        public bool IsHeld(Transform pin) => pin != null && _held.Contains(pin);

        /// <summary>
        /// The pin was picked up: until <see cref="Release"/> it is drawn darker and smaller (it still follows the mouse),
        /// or hidden completely with <paramref name="hide"/> (a tray pin whose drag ghost is in the hand instead).
        /// </summary>
        public void Hold(Transform pin, bool hide = false)
        {
            if (pin == null) return;
            StopDrop(pin);
            _held.Add(pin);
            Vector3 baseScale = BaseScale(pin);
            pin.localScale = hide ? baseScale : baseScale * heldScale;
            Group(pin).alpha = hide ? 0f : heldAlpha;
        }

        // The pin's own size, remembered while it is held or dropping (both change localScale).
        private Vector3 BaseScale(Transform pin)
        {
            if (!_baseScale.TryGetValue(pin, out var s)) _baseScale[pin] = s = pin.localScale;
            return s;
        }

        private void ForgetScale(Transform pin)
        {
            if (pin != null && _baseScale.TryGetValue(pin, out var s)) pin.localScale = s;
            _baseScale.Remove(pin);
        }

        /// <summary>The pin was put down: it reappears with the drop (or just reappears).</summary>
        public void Release(Transform pin, bool playDrop = true, Color? tint = null)
        {
            if (pin == null) return;
            _held.Remove(pin);
            if (playDrop) StartDrop(pin, tint);
            else { ForgetScale(pin); Group(pin).alpha = 1f; }
        }

        /// <summary>Drop a pin that just appeared (new marker). Waits a frame so a pin grabbed that frame stays hidden.</summary>
        public void PlayDrop(Transform pin, Color? tint = null)
        {
            if (pin == null || !isActiveAndEnabled) return;
            Group(pin).alpha = 0f; // no full-size flash before the drop starts
            StartCoroutine(DropNextFrame(pin, tint));
        }

        private IEnumerator DropNextFrame(Transform pin, Color? tint)
        {
            yield return null;
            if (pin != null && !_held.Contains(pin)) StartDrop(pin, tint);
        }

        private void StartDrop(Transform pin, Color? tint)
        {
            StopDrop(pin);
            if (!isActiveAndEnabled) { ForgetScale(pin); Group(pin).alpha = 1f; return; }
            _drops[pin] = StartCoroutine(DropRoutine(pin));
            EmitRipple(pin, tint ?? ringColor);
            SplashWater(pin, splashStrength);
        }

        private void StopDrop(Transform pin)
        {
            if (_drops.TryGetValue(pin, out var co))
            {
                if (co != null) StopCoroutine(co);
                _drops.Remove(pin);
            }
        }

        private IEnumerator DropRoutine(Transform pin)
        {
            var group = Group(pin);
            Vector3 baseScale = BaseScale(pin);
            float t = 0f, duration = dropDuration / Mathf.Max(0.01f, speed);
            while (t < duration && pin != null)
            {
                float u = Mathf.Clamp01(t / duration);
                pin.localScale = baseScale * Mathf.LerpUnclamped(dropScaleFrom, 1f, EaseOutBack(u, dropOvershoot));
                group.alpha = Mathf.Clamp01(u * 3f);
                t += Time.unscaledDeltaTime;
                yield return null;
            }
            if (pin != null)
            {
                group.alpha = 1f;
                _drops.Remove(pin);
            }
            ForgetScale(pin);
        }

        // ── rings ──

        /// <summary>The echo ripple around a pin, without touching the pin itself.</summary>
        public void EmitRipple(Transform pin, Color color, Transform layer = null)
        {
            if (pin == null || pin.parent == null || !isActiveAndEnabled) return;
            Transform parent = layer != null ? layer : pin.parent;
            Vector3 localPos = layer != null ? layer.InverseTransformPoint(pin.position) : pin.localPosition;
            int sibling = layer != null ? -1 : pin.GetSiblingIndex();
            float pinRadius = PinRadius(pin);
            float radius = pinRadius > 1f ? pinRadius * radiusScale : fallbackRadius;
            float start = pinRadius > 1f ? pinRadius * startRadiusScale : radius * 0.25f;
            if (!tintByCategory) color = ringColor;
            for (int i = 0; i < Mathf.Max(1, echoes); i++)
            {
                float echoRadius = Mathf.Max(start + 1f, radius * Mathf.Pow(echoRadiusFalloff, i));
                float alpha = startAlpha * Mathf.Pow(echoDecay, i);
                if (alpha <= 0.01f) break;
                StartCoroutine(RingRoutine(parent, localPos, sibling, start, echoRadius,
                    color, alpha, i * echoInterval / Mathf.Max(0.01f, speed)));
            }
        }

        private IEnumerator RingRoutine(Transform parent, Vector3 localPos, int pinSibling, float from, float to,
            Color color, float alpha, float delay)
        {
            if (delay > 0f) yield return new WaitForSecondsRealtime(delay);
            if (parent == null) yield break;

            var go = new GameObject("PinDropRing", typeof(RectTransform), typeof(CanvasRenderer));
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            rt.localPosition = localPos;
            rt.sizeDelta = Vector2.zero;
            if (pinSibling >= 0) rt.SetSiblingIndex(Mathf.Min(pinSibling, parent.childCount - 1)); // under the pin
            var ring = go.AddComponent<RingStrokeGraphic>();
            ring.raycastTarget = false;

            float t = 0f, life = ringLifetime / Mathf.Max(0.01f, speed);
            while (t < life && rt != null)
            {
                float u = Mathf.Clamp01(t / life);
                float grow = 1f - Mathf.Pow(1f - u, expansionRate);
                ring.Radius = Mathf.Lerp(from, to, grow);
                ring.StrokeWidth = Mathf.Lerp(strokeStart, strokeEnd, u);
                ring.color = new Color(color.r, color.g, color.b, alpha * Mathf.Pow(1f - u, decay));
                t += Time.unscaledDeltaTime;
                yield return null;
            }
            if (rt != null) Destroy(rt.gameObject);
        }

        private static float PinRadius(Transform pin)
        {
            var display = pin.GetComponentInChildren<BuildingMarkerDisplay>(true);
            if (display != null && pin.parent is RectTransform space && display.TryGetCurrentVisualRadius(space, out float r) && r > 1f)
                return r;
            if (pin is RectTransform rt)
            {
                Vector2 size = rt.rect.size;
                float s = Mathf.Max(size.x * Mathf.Abs(rt.localScale.x), size.y * Mathf.Abs(rt.localScale.y)) * 0.5f;
                if (s > 1f) return s;
            }
            return 0f;
        }

        private static CanvasGroup Group(Transform pin)
        {
            var g = pin.GetComponent<CanvasGroup>();
            return g != null ? g : pin.gameObject.AddComponent<CanvasGroup>();
        }

        private static float EaseOutBack(float u, float overshoot)
        {
            float c3 = overshoot + 1f, v = u - 1f;
            return 1f + c3 * v * v * v + overshoot * v * v;
        }
    }
}
