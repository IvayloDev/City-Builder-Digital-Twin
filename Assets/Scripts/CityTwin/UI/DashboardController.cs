using UnityEngine;
using UnityEngine.UI;
using CityTwin.Core;
using CityTwin.Simulation;

namespace CityTwin.UI
{
    /// <summary>Per-instance dashboard: metric bars, QOL, timer, budget. Bind to SimulationEngine, SessionTimer, and budget source. No statics.</summary>
    public class DashboardController : MonoBehaviour
    {
        [Header("Data sources")]
        [SerializeField] private SimulationEngine simulationEngine;
        [SerializeField] private SessionTimer sessionTimer;
        [SerializeField] private GameInstanceCoordinator coordinator;

        [Header("Top bar")]
        [SerializeField] private UnityEngine.UI.Text timerText;
        [SerializeField] private UnityEngine.UI.Text budgetText;
        [SerializeField] private UnityEngine.UI.Text qolText;
        [SerializeField] private UnityEngine.UI.Text accessText;

        [Header("Metric bars (fill 0-1)")]
        [SerializeField] private Image environmentFill;
        [SerializeField] private Image economyFill;
        [SerializeField] private Image healthSafetyFill;
        [SerializeField] private Image cultureEduFill;
        [SerializeField] private Image accessibilityFill;

        [Tooltip("Scale raw metrics to fill (e.g. 0.05 = 1/20).")]
        [SerializeField] private float metricFillScale = 0.05f;
        [Tooltip("Smooth metric bar changes (0 = instant).")]
        [SerializeField] private float metricSmoothTime = 0.3f;

        private float _displayQol;
        private float _displayEnv, _displayEco, _displaySaf, _displayCul, _displayAcc;

        private void Awake()
        {
            if (simulationEngine == null) simulationEngine = GetComponentInChildren<SimulationEngine>(true);
            if (sessionTimer == null) sessionTimer = GetComponentInChildren<SessionTimer>(true);
            if (coordinator == null) coordinator = GetComponentInChildren<GameInstanceCoordinator>(true);
        }

        private void OnEnable()
        {
            if (simulationEngine != null)
                simulationEngine.OnMetricsChanged += RefreshMetrics;
        }

        private void OnDisable()
        {
            if (simulationEngine != null)
                simulationEngine.OnMetricsChanged -= RefreshMetrics;
        }

        private void Update()
        {
            if (sessionTimer != null && timerText != null)
                timerText.text = sessionTimer.FormatTime();
            if (coordinator != null && budgetText != null)
                budgetText.text = coordinator.Budget.ToString();
        }

        private void RefreshMetrics()
        {
            if (simulationEngine == null) return;
            float dt = metricSmoothTime > 0 ? Time.deltaTime / metricSmoothTime : 1f;
            _displayQol = Mathf.Lerp(_displayQol, simulationEngine.Qol, dt);
            _displayEnv = Mathf.Lerp(_displayEnv, simulationEngine.Environment, dt);
            _displayEco = Mathf.Lerp(_displayEco, simulationEngine.Economy, dt);
            _displaySaf = Mathf.Lerp(_displaySaf, simulationEngine.HealthSafety, dt);
            _displayCul = Mathf.Lerp(_displayCul, simulationEngine.CultureEdu, dt);
            _displayAcc = Mathf.Lerp(_displayAcc, simulationEngine.Accessibility, dt);
            if (qolText != null) qolText.text = Mathf.RoundToInt(_displayQol).ToString();
            if (accessText != null) accessText.text = $"{Mathf.RoundToInt(_displayAcc)}%";
            if (environmentFill != null) environmentFill.fillAmount = Mathf.Clamp01(_displayEnv * metricFillScale);
            if (economyFill != null) economyFill.fillAmount = Mathf.Clamp01(_displayEco * metricFillScale);
            if (healthSafetyFill != null) healthSafetyFill.fillAmount = Mathf.Clamp01(_displaySaf * metricFillScale);
            if (cultureEduFill != null) cultureEduFill.fillAmount = Mathf.Clamp01(_displayCul * metricFillScale);
            if (accessibilityFill != null) accessibilityFill.fillAmount = Mathf.Clamp01(_displayAcc * metricFillScale);
        }
    }
}
