using System.Collections.Generic;
using UnityEngine;

namespace CityTwin.Calibration
{
    /// <summary>A marker the camera currently reports, in raw sensor coordinates.</summary>
    public struct SensorMarker
    {
        public int SessionId;
        public Vector2 Raw;
        public float FirstSeen;
        public int ClassId;
        public string BuildingId;
    }

    public enum SensorTargetState { Pending, Captured, Skipped }

    /// <summary>
    /// Guided capture for one sensor, independent of input devices and rendering so it can be tested: walks a list of
    /// table targets, captures the raw position of a tile once it has rested on the current target, and refits the
    /// <see cref="SensorMapping"/> after every capture from the 4th on. Any marker id works; the newest one on the table is used.
    /// </summary>
    public sealed class SensorCalibrationSession
    {
        public const float StableSeconds = 1f;
        public const float StableRawRadius = 0.006f;
        public const float RearmRawDistance = 0.03f;

        private readonly List<Vector2> _targets;
        private readonly SensorTargetState[] _states;
        private readonly Vector2[] _captured;
        private readonly SensorMapping _template;
        // Sessions already used for a capture, with where: ignored until lifted or moved, so one tile can't fill two targets.
        private readonly Dictionary<int, Vector2> _spent = new Dictionary<int, Vector2>();
        private readonly Dictionary<int, int> _capturedBy = new Dictionary<int, int>();

        private int _trackedSession = -1;
        private Vector2 _anchor;
        private Vector2 _sum;
        private int _sumCount;
        private float _stableSince;

        public SensorCalibrationSession(IReadOnlyList<Vector2> targetsTableUv, SensorMapping template)
        {
            _targets = new List<Vector2>(targetsTableUv);
            _states = new SensorTargetState[_targets.Count];
            _captured = new Vector2[_targets.Count];
            _template = template;
        }

        public IReadOnlyList<Vector2> Targets => _targets;
        public int CurrentIndex { get; private set; }
        public bool IsComplete => CurrentIndex >= _targets.Count;
        public int CapturedCount { get; private set; }
        public bool HasMarker { get; private set; }
        public Vector2 LiveRaw { get; private set; }
        /// <summary>How far the current hold is towards auto-capture, 0..1.</summary>
        public float Progress { get; private set; }
        public bool HasFit { get; private set; }
        public SensorMapping Fit { get; private set; }
        public SensorFitReport Report { get; private set; }

        public SensorTargetState StateOf(int index) => _states[index];

        /// <summary>Call every frame with the markers currently seen.</summary>
        public void Feed(IReadOnlyList<SensorMarker> markers, float now)
        {
            HasMarker = false;
            Progress = 0f;
            if (IsComplete) return;

            bool found = false;
            SensorMarker best = default;
            for (int i = 0; i < markers.Count; i++)
            {
                var m = markers[i];
                if (_spent.TryGetValue(m.SessionId, out Vector2 at))
                {
                    if (Vector2.Distance(m.Raw, at) <= RearmRawDistance) continue;
                    _spent.Remove(m.SessionId);
                }
                if (!found || m.FirstSeen > best.FirstSeen) { best = m; found = true; }
            }
            if (!found) { _trackedSession = -1; return; }

            HasMarker = true;
            LiveRaw = best.Raw;
            if (best.SessionId != _trackedSession || Vector2.Distance(best.Raw, _anchor) > StableRawRadius)
            {
                _trackedSession = best.SessionId;
                _anchor = best.Raw;
                _sum = Vector2.zero;
                _sumCount = 0;
                _stableSince = now;
            }
            _sum += best.Raw;
            _sumCount++;

            Progress = Mathf.Clamp01((now - _stableSince) / StableSeconds);
            if (Progress >= 1f) Capture(best.SessionId, _sum / _sumCount);
        }

        /// <summary>Capture the current target right away with the marker's latest position. False if no marker is visible.</summary>
        public bool CaptureNow()
        {
            if (IsComplete || !HasMarker) return false;
            Capture(_trackedSession, _sumCount > 0 ? _sum / _sumCount : LiveRaw);
            return true;
        }

        /// <summary>Skip a target the camera can't see or a tile can't reach.</summary>
        public void Skip()
        {
            if (IsComplete) return;
            _states[CurrentIndex] = SensorTargetState.Skipped;
            Advance();
        }

        /// <summary>Go back one target and forget its capture. False if already on the first.</summary>
        public bool Back()
        {
            if (CurrentIndex <= 0) return false;
            CurrentIndex--;
            if (_states[CurrentIndex] == SensorTargetState.Captured) CapturedCount--;
            _states[CurrentIndex] = SensorTargetState.Pending;
            if (_capturedBy.TryGetValue(CurrentIndex, out int session))
            {
                _spent.Remove(session);
                _capturedBy.Remove(CurrentIndex);
            }
            ResetHold();
            Refit();
            return true;
        }

        private void Capture(int session, Vector2 raw)
        {
            _captured[CurrentIndex] = raw;
            _states[CurrentIndex] = SensorTargetState.Captured;
            _capturedBy[CurrentIndex] = session;
            _spent[session] = raw;
            CapturedCount++;
            Advance();
            Refit();
        }

        private void Advance()
        {
            CurrentIndex++;
            ResetHold();
        }

        private void ResetHold()
        {
            _trackedSession = -1;
            _sumCount = 0;
            Progress = 0f;
        }

        private void Refit()
        {
            var raw = new List<Vector2>();
            var table = new List<Vector2>();
            for (int i = 0; i < _targets.Count; i++)
            {
                if (_states[i] != SensorTargetState.Captured) continue;
                raw.Add(_captured[i]);
                table.Add(_targets[i]);
            }
            HasFit = SensorMapping.TryFit(raw, table, _template, out SensorMapping fit, out SensorFitReport report);
            Fit = HasFit ? fit : null;
            Report = HasFit ? report : default;
        }
    }

    public static class SensorTargetLayout
    {
        /// <summary>
        /// Pick well-spread points: start from the one furthest from the centroid, then repeatedly take the candidate
        /// furthest from everything chosen. Result is ordered row by row so the walk around the table stays short.
        /// </summary>
        public static List<Vector2> PickSpread(IReadOnlyList<Vector2> candidates, int count)
        {
            var picked = new List<Vector2>();
            if (candidates == null || candidates.Count == 0 || count <= 0) return picked;

            Vector2 centroid = Vector2.zero;
            for (int i = 0; i < candidates.Count; i++) centroid += candidates[i];
            centroid /= candidates.Count;

            int first = 0;
            for (int i = 1; i < candidates.Count; i++)
                if ((candidates[i] - centroid).sqrMagnitude > (candidates[first] - centroid).sqrMagnitude) first = i;
            picked.Add(candidates[first]);

            while (picked.Count < count && picked.Count < candidates.Count)
            {
                int best = -1;
                float bestDistance = -1f;
                for (int i = 0; i < candidates.Count; i++)
                {
                    float nearest = float.MaxValue;
                    for (int p = 0; p < picked.Count; p++)
                        nearest = Mathf.Min(nearest, (candidates[i] - picked[p]).sqrMagnitude);
                    if (nearest > bestDistance) { bestDistance = nearest; best = i; }
                }
                if (bestDistance <= 0f) break;
                picked.Add(candidates[best]);
            }

            const float rowHeight = 0.02f;
            picked.Sort((a, b) =>
            {
                int rows = Mathf.RoundToInt(b.y / rowHeight).CompareTo(Mathf.RoundToInt(a.y / rowHeight));
                return rows != 0 ? rows : a.x.CompareTo(b.x);
            });
            return picked;
        }
    }
}
