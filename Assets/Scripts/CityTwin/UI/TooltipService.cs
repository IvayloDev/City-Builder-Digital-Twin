using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using CityTwin.Core;
using CityTwin.Config;
using CityTwin.Localization;
using CityTwin.Simulation;

namespace CityTwin.UI
{
    /// <summary>Intro sequence, runtime tips, end band messages. JSON-driven; no statics.</summary>
    public class TooltipService : MonoBehaviour
    {
        [SerializeField] private GameConfigLoader configLoader;
        [SerializeField] private LocalizationService localization;
        [SerializeField] private SessionTimer sessionTimer;
        [SerializeField] private SimulationEngine simulationEngine;
        [Header("UI")]
        [SerializeField] private Text statusBarText;
        [SerializeField] private Text endTitleText;
        [SerializeField] private Text endBodyText;
        [SerializeField] private GameObject endPanel;

        private int _introKeyIndex;
        private float _nextIntroTime;

        private void Awake()
        {
            if (configLoader == null) configLoader = GetComponentInChildren<GameConfigLoader>(true);
            if (localization == null) localization = GetComponentInChildren<LocalizationService>(true);
            if (sessionTimer == null) sessionTimer = GetComponentInChildren<SessionTimer>(true);
            if (simulationEngine == null) simulationEngine = GetComponentInChildren<SimulationEngine>(true);
        }

        private void OnEnable()
        {
            if (sessionTimer != null)
            {
                sessionTimer.OnPhaseChanged += OnPhaseChanged;
                sessionTimer.OnTimerEnded += OnTimerEnded;
            }
        }

        private void OnDisable()
        {
            if (sessionTimer != null)
            {
                sessionTimer.OnPhaseChanged -= OnPhaseChanged;
                sessionTimer.OnTimerEnded -= OnTimerEnded;
            }
        }

        private void Update()
        {
            if (sessionTimer != null && sessionTimer.CurrentPhase == SessionTimer.Phase.Intro && sessionTimer.IsRunning)
            {
                if (Time.time >= _nextIntroTime && configLoader?.Config?.Tooltips?.introKeys != null && _introKeyIndex < configLoader.Config.Tooltips.introKeys.Length)
                {
                    string key = configLoader.Config.Tooltips.introKeys[_introKeyIndex++];
                    if (statusBarText != null && localization != null)
                        statusBarText.text = localization.GetString(key);
                    _nextIntroTime = Time.time + 8f;
                }
            }
        }

        private void OnPhaseChanged(SessionTimer.Phase phase)
        {
            if (phase == SessionTimer.Phase.Gameplay && statusBarText != null && localization != null)
                statusBarText.text = localization.GetString("ui.timer");
        }

        private void OnTimerEnded()
        {
            if (endPanel != null) endPanel.SetActive(true);
            int qol = simulationEngine != null ? Mathf.RoundToInt(simulationEngine.Qol) : 0;
            var cfg = configLoader?.Config?.EndMessages;
            if (cfg != null && endTitleText != null && endBodyText != null && localization != null)
            {
                foreach (var msg in cfg)
                {
                    if (qol >= msg.min && qol <= msg.max)
                    {
                        endTitleText.text = localization.GetString(msg.titleKey);
                        endBodyText.text = localization.GetString(msg.bodyKey);
                        break;
                    }
                }
            }
        }
    }
}
