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
    /// Guided capture for one sensor, independent of input devices and rendering so it can be tested. It first waits
    /// for an empty table (no marker for <see cref="ClearSeconds"/>), then walks a list of table targets: a tile held
    /// still on the current target for <see cref="HoldSeconds"/> stores its raw position, and the session waits for
    /// <see cref="Next"/> before showing the next target. The <see cref="SensorMapping"/> is refitted after every
    /// capture from the 4th on.
    /// Tiles are followed by position, not by TUIO session id: reacTIVision can hand the same physical tile a new
    /// session id every frame. After a capture the tile has to move off that spot before it can be captured again,
    /// so a single tile can be walked from target to target without ever filling two targets from one place.
    /// </summary>
    public sealed class SensorCalibrationSession
    {
        public const float ClearSeconds = 1f;
        public const float HoldSeconds = 2f;
        /// <summary>A stored tile left still this long (counted from when it came to rest) moves on to the next target by itself.</summary>
        public const float AutoAdvanceSeconds = 5f;
        public const float StableRawRadius = 0.006f;
        public const float RearmRawDistance = 0.03f;

        private readonly List<Vector2> _targets;
        private readonly SensorTargetState[] _states;
        private readonly Vector2[] _captured;
        private readonly SensorMapping _template;

        // Where the last capture was taken: ignored until the tile there has moved away or been lifted.
        private bool _blocked;
        private Vector2 _blockedAt;

        private bool _tracking;
        private Vector2 _anchor;
        private Vector2 _sum;
        private int _sumCount;
        private float _stableSince;
        private float _emptySince = -1f;
        private float _restingSince = -1f; // stored tile still at rest since; -1 = it moved, wait for Next

        public SensorCalibrationSession(IReadOnlyList<Vector2> targetsTableUv, SensorMapping template)
        {
            _targets = new List<Vector2>(targetsTableUv);
            _states = new SensorTargetState[_targets.Count];
            _captured = new Vector2[_targets.Count];
            _template = template;
            IsClearingTable = true;
        }

        public IReadOnlyList<Vector2> Targets => _targets;
        public int CurrentIndex { get; private set; }
        public bool IsComplete => CurrentIndex >= _targets.Count;
        /// <summary>Waiting for every tile to be lifted off the table before the first target is shown.</summary>
        public bool IsClearingTable { get; private set; }
        /// <summary>The current target is stored; <see cref="Next"/> moves on.</summary>
        public bool AwaitingNext { get; private set; }
        /// <summary>The only tile seen is still where a capture was taken: it has to be moved to the cross.</summary>
        public bool WaitingForMove { get; private set; }
        /// <summary>How long the table has been empty while clearing, 0..1 of <see cref="ClearSeconds"/>.</summary>
        public float ClearProgress { get; private set; }
        /// <summary>While stored: seconds until the session moves on by itself, or -1 if the tile moved (Next needed).</summary>
        public float AutoAdvanceIn { get; private set; } = -1f;
        public int CapturedCount { get; private set; }
        /// <summary>A tile that can be captured for the current target is on the table.</summary>
        public bool HasMarker { get; private set; }
        public Vector2 LiveRaw { get; private set; }
        /// <summary>How far the current hold is towards auto-capture, 0..1 of <see cref="HoldSeconds"/>.</summary>
        public float Progress { get; private set; }
        public bool HasFit { get; private set; }
        public SensorMapping Fit { get; private set; }
        public SensorFitReport Report { get; private set; }

        public SensorTargetState StateOf(int index) => _states[index];
        /// <summary>Raw camera position stored for a target (meaningful when its state is Captured).</summary>
        public Vector2 CapturedRaw(int index) => _captured[index];

        /// <summary>Call every frame with the markers currently seen.</summary>
        public void Feed(IReadOnlyList<SensorMarker> markers, float now)
        {
            HasMarker = false;
            WaitingForMove = false;
            Progress = 0f;
            if (IsComplete) return;

            if (IsClearingTable)
            {
                if (markers.Count > 0) { _emptySince = -1f; ClearProgress = 0f; return; }
                if (_emptySince < 0f) _emptySince = now;
                ClearProgress = Mathf.Clamp01((now - _emptySince) / ClearSeconds);
                if (ClearProgress >= 1f)
                {
                    IsClearingTable = false;
                    _blocked = false;
                    ResetHold();
                }
                return;
            }

            bool parkedSeen = false, found = false;
            SensorMarker best = default;
            for (int i = 0; i < markers.Count; i++)
            {
                var m = markers[i];
                if (IsParked(m.Raw)) { parkedSeen = true; continue; }
                if (!found || m.FirstSeen > best.FirstSeen) { best = m; found = true; }
            }
            if (_blocked && !IsNearBlocked(markers)) _blocked = false; // the tile moved away or was lifted

            // Stored: nothing more to take for this target until Next, or until the tile has rested AutoAdvanceSeconds.
            if (AwaitingNext)
            {
                if (_restingSince >= 0f && IsNear(markers, _blockedAt, StableRawRadius * 2f))
                {
                    AutoAdvanceIn = Mathf.Max(0f, AutoAdvanceSeconds - (now - _restingSince));
                    if (AutoAdvanceIn <= 0f) Advance();
                }
                else
                {
                    _restingSince = -1f;
                    AutoAdvanceIn = -1f;
                }
                return;
            }

            if (!found)
            {
                WaitingForMove = parkedSeen;
                _tracking = false;
                return;
            }

            HasMarker = true;
            LiveRaw = best.Raw;
            if (!_tracking || Vector2.Distance(best.Raw, _anchor) > StableRawRadius)
            {
                _tracking = true;
                _anchor = best.Raw;
                _sum = Vector2.zero;
                _sumCount = 0;
                _stableSince = now;
            }
            _sum += best.Raw;
            _sumCount++;

            Progress = Mathf.Clamp01((now - _stableSince) / HoldSeconds);
            if (Progress >= 1f) Capture(_sum / _sumCount);
        }

        // A tile still sitting where the last capture, or any stored target, was taken: it hasn't been moved yet.
        private bool IsParked(Vector2 raw)
        {
            if (_blocked && Vector2.Distance(raw, _blockedAt) <= RearmRawDistance) return true;
            for (int i = 0; i < _targets.Count; i++)
                if (_states[i] == SensorTargetState.Captured && Vector2.Distance(raw, _captured[i]) <= RearmRawDistance)
                    return true;
            return false;
        }

        private bool IsNearBlocked(IReadOnlyList<SensorMarker> markers) => IsNear(markers, _blockedAt, RearmRawDistance);

        private static bool IsNear(IReadOnlyList<SensorMarker> markers, Vector2 at, float distance)
        {
            for (int i = 0; i < markers.Count; i++)
                if (Vector2.Distance(markers[i].Raw, at) <= distance) return true;
            return false;
        }

        /// <summary>Capture the current target right away with the marker's latest position. False if no usable marker is visible.</summary>
        public bool CaptureNow()
        {
            if (IsComplete || IsClearingTable || AwaitingNext || !HasMarker) return false;
            Capture(_sumCount > 0 ? _sum / _sumCount : LiveRaw);
            return true;
        }

        /// <summary>Move on from a stored target. False while the current target has not been stored yet.</summary>
        public bool Next()
        {
            if (IsComplete || IsClearingTable || !AwaitingNext) return false;
            Advance();
            return true;
        }

        /// <summary>Skip a target the camera can't see or a tile can't reach (drops a capture already stored on it).</summary>
        public void Skip()
        {
            if (IsComplete || IsClearingTable) return;
            if (_states[CurrentIndex] == SensorTargetState.Captured) CapturedCount--; // the tile stays blocked: it must move first
            _states[CurrentIndex] = SensorTargetState.Skipped;
            Refit();
            Advance();
        }

        /// <summary>
        /// Undo: on a stored target that is waiting for Next, forget that capture and retake it; otherwise go back one
        /// target and forget its capture. False if there is nothing to undo.
        /// </summary>
        public bool Back()
        {
            if (IsClearingTable) return false;
            if (!AwaitingNext)
            {
                if (CurrentIndex <= 0) return false;
                CurrentIndex--;
            }
            if (_states[CurrentIndex] == SensorTargetState.Captured) CapturedCount--;
            _states[CurrentIndex] = SensorTargetState.Pending;
            AwaitingNext = false;
            _blocked = false;
            _restingSince = -1f;
            AutoAdvanceIn = -1f;
            ResetHold();
            Refit();
            return true;
        }

        private void Capture(Vector2 raw)
        {
            _captured[CurrentIndex] = raw;
            _states[CurrentIndex] = SensorTargetState.Captured;
            CapturedCount++;
            AwaitingNext = true;
            _blocked = true;
            _blockedAt = raw;
            _restingSince = _tracking ? _stableSince : -1f; // CaptureNow on a tile not yet settled: Next only
            AutoAdvanceIn = _restingSince >= 0f ? AutoAdvanceSeconds - HoldSeconds : -1f;
            ResetHold();
            Refit();
        }

        private void Advance()
        {
            CurrentIndex++;
            AwaitingNext = false;
            _restingSince = -1f;
            AutoAdvanceIn = -1f;
            ResetHold();
        }

        private void ResetHold()
        {
            _tracking = false;
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
