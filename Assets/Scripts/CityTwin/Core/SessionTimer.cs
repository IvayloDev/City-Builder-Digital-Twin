using System;
using UnityEngine;
using CityTwin.Config;

namespace CityTwin.Core
{
    /// <summary>Per-instance session timer: intro then gameplay then end. No statics.</summary>
    public class SessionTimer : MonoBehaviour
    {
        [SerializeField] private int introSeconds = 90;
        [SerializeField] private int gameplaySeconds = 270;

        private float _remainingSeconds;
        private Phase _phase = Phase.Intro;
        private bool _running;

        public enum Phase { Intro, Gameplay, End }
        public Phase CurrentPhase => _phase;
        public float RemainingSeconds => _remainingSeconds;
        public bool IsRunning => _running;

        public event Action<Phase> OnPhaseChanged;
        public event Action OnTimerEnded;

        public void SetFromConfig(GameConfig config)
        {
            if (config?.Session == null) return;
            introSeconds = config.Session.introSeconds;
            gameplaySeconds = config.Session.gameplaySeconds;
        }

        public void StartSession()
        {
            _phase = Phase.Intro;
            _remainingSeconds = introSeconds;
            _running = true;
            OnPhaseChanged?.Invoke(_phase);
        }

        public void Stop()
        {
            _running = false;
        }

        private void Update()
        {
            if (!_running) return;
            _remainingSeconds -= Time.deltaTime;
            if (_remainingSeconds <= 0)
            {
                if (_phase == Phase.Intro)
                {
                    _phase = Phase.Gameplay;
                    _remainingSeconds = gameplaySeconds;
                    OnPhaseChanged?.Invoke(_phase);
                }
                else
                {
                    _phase = Phase.End;
                    _running = false;
                    OnPhaseChanged?.Invoke(_phase);
                    OnTimerEnded?.Invoke();
                }
            }
        }

        public string FormatTime()
        {
            int total = Mathf.Max(0, Mathf.FloorToInt(_remainingSeconds));
            int m = total / 60;
            int s = total % 60;
            return $"{m:D2}:{s:D2}";
        }
    }
}
