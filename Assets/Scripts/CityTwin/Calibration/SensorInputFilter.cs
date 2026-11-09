using System;
using System.Collections.Generic;
using UnityEngine;

namespace CityTwin.Calibration
{
    /// <summary>Per-sensor jitter filter settings, tuned on the F9 camera tab and saved with the sensor's calibration.</summary>
    [Serializable]
    public class SensorFilterSettings
    {
        public const float MaxRadiusCm = 5f;
        public const float MaxSmoothingSeconds = 0.5f;

        public bool enabled = true;
        [Tooltip("A resting tile stays put until the camera reports it further away than this (cm on the table).")]
        public float radiusCm = 0.6f;
        [Tooltip("Time constant of the glide to the new position once a tile really moves (seconds). 0 = jump straight there.")]
        public float smoothingSeconds = 0.06f;
        [Tooltip("Consecutive samples outside the radius needed to count as movement. 2 rejects single-frame spikes for one camera frame of latency.")]
        public int confirmSamples = 2;

        public void Sanitize()
        {
            radiusCm = Mathf.Clamp(radiusCm, 0f, MaxRadiusCm);
            smoothingSeconds = Mathf.Clamp(smoothingSeconds, 0f, MaxSmoothingSeconds);
            confirmSamples = Mathf.Clamp(confirmSamples, 1, 5);
        }
    }

    /// <summary>
    /// Deadband + time smoothing for one tracked tile. While resting, samples inside the radius change nothing, so
    /// jitter is removed completely and adds no lag. Samples outside the radius switch to a time-based glide that
    /// catches up with the camera fully, leaving no permanent offset, then settles and rests again.
    /// Positions are in table uv when the sensor is calibrated, otherwise raw sensor coordinates (distances are then approximate).
    /// TUIO may send nothing while a tile is still, so call <see cref="Advance"/> every frame to finish a glide.
    /// </summary>
    public sealed class SensorInputFilter
    {
        public const float TableWidthCm = 300f;
        private const float MinSettleSeconds = 0.12f;
        private const float SettleTimeConstants = 5f;
        private const float MinSettleRadiusCm = 0.01f;

        private bool _seeded;
        private bool _moving;
        private Vector2 _output;
        private Vector2 _target;
        private Vector2 _settleAnchor;
        private int _outside;
        private float _lastAdvance;
        private float _settleSince = -1f;

        /// <summary>Null or disabled settings pass samples straight through.</summary>
        public SensorFilterSettings Settings { get; set; }
        public Vector2 Output => _output;
        public bool IsMoving => _moving;

        public void Reset()
        {
            _seeded = false;
            _moving = false;
            _outside = 0;
            _settleSince = -1f;
        }

        /// <summary>Feed a new camera sample; returns the filtered position.</summary>
        public Vector2 Push(Vector2 sample, float now)
        {
            var s = Settings;
            if (s == null || !s.enabled || !_seeded)
            {
                Reset();
                _seeded = true;
                _output = _target = sample;
                return sample;
            }

            _target = sample;
            if (!_moving)
            {
                if (DistanceCm(sample, _output) <= s.radiusCm) { _outside = 0; return _output; }
                if (++_outside < Mathf.Max(1, s.confirmSamples)) return _output;
                _outside = 0;
                _moving = true;
                _lastAdvance = now;
                _settleSince = -1f;
            }
            Advance(now);
            return _output;
        }

        /// <summary>Continue a glide without a new sample. Returns true when the output changed.</summary>
        public bool Advance(float now)
        {
            if (!_moving) return false;
            var s = Settings;
            float tau = s != null ? s.smoothingSeconds : 0f;
            float dt = Mathf.Max(0f, now - _lastAdvance);
            _lastAdvance = now;

            Vector2 previous = _output;
            float alpha = tau <= 1e-4f ? 1f : 1f - Mathf.Exp(-dt / tau);
            _output += alpha * (_target - _output);

            // Rest once the camera position has stayed put long enough for the glide to arrive (5 time constants = under 1% left).
            float settleRadius = Mathf.Max(s != null ? s.radiusCm : 0f, MinSettleRadiusCm);
            if (_settleSince < 0f || DistanceCm(_target, _settleAnchor) > settleRadius)
            {
                _settleAnchor = _target;
                _settleSince = now;
            }
            else if (now - _settleSince >= Mathf.Max(MinSettleSeconds, SettleTimeConstants * tau))
            {
                _moving = false;
            }

            return _output != previous;
        }

        public static float DistanceCm(Vector2 a, Vector2 b) => Vector2.Distance(a, b) * TableWidthCm;
    }

    /// <summary>Measures how far a resting tile's reported position wanders, to pick a filter radius from real data.</summary>
    public sealed class SensorJitterMeter
    {
        public const float WindowSeconds = 1.5f;
        public const int MinSamples = 10;

        private readonly List<(Vector2 pos, float time)> _samples = new List<(Vector2, float)>();

        public int SampleCount => _samples.Count;

        public void Clear() => _samples.Clear();

        public void Add(Vector2 position, float now)
        {
            _samples.Add((position, now));
            int expired = 0;
            while (expired < _samples.Count && now - _samples[expired].time > WindowSeconds) expired++;
            if (expired > 0) _samples.RemoveRange(0, expired);
        }

        /// <summary>Largest distance from the window's mean position, in cm.</summary>
        public float PeakCm
        {
            get
            {
                if (_samples.Count == 0) return 0f;
                Vector2 mean = Vector2.zero;
                foreach (var s in _samples) mean += s.pos;
                mean /= _samples.Count;
                float peak = 0f;
                foreach (var s in _samples) peak = Mathf.Max(peak, SensorInputFilter.DistanceCm(s.pos, mean));
                return peak;
            }
        }
    }
}
