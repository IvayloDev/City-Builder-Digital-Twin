using UnityEngine;
using TMPro;

namespace CityTwin.UI
{
    /// <summary>
    /// Owns the end-of-session screen UI: the overlay panel, title/body text,
    /// and the restart status line used by RestartFlowController.
    /// Other components (TooltipService, RestartFlowController) drive this via its public API
    /// instead of holding direct references to the UI fields.
    /// </summary>
    public class EndScreenController : MonoBehaviour
    {
        [SerializeField] private DashboardController _dashboardController;
        
        [Header("End Screen UI")]
        [Tooltip("Root GameObject of the end screen overlay. Toggled on when the session ends, off on restart.")]
        [SerializeField] private GameObject endPanel;
        [SerializeField] private TextMeshProUGUI endTitleText;
        [SerializeField] private TextMeshProUGUI endBodyText;
        [SerializeField] private TextMeshProUGUI QOLText;

        [Header("Restart Flow UI")]
        [Tooltip("Text shown during the restart flow (remove-tiles prompt, then countdown).")]
        [SerializeField] private TextMeshProUGUI restartStatusText;

        public bool IsVisible => endPanel != null && endPanel.activeSelf;

        /// <summary>Activate the overlay and fill in the final title/body text. Clears any prior restart status.</summary>
        public void Show(string title, string body)
        {
            if (endPanel != null) endPanel.SetActive(true);
            if (endTitleText != null) endTitleText.text = title ?? string.Empty;
            if (endBodyText != null) endBodyText.text = body ?? string.Empty;
            
            //Set QOL Score
            QOLText.text = Mathf.RoundToInt(_dashboardController.DisplayQol).ToString();
            
            SetRestartStatus(string.Empty);
        }

        /// <summary>Hide the overlay and clear the restart status.</summary>
        public void Hide()
        {
            if (endPanel != null) endPanel.SetActive(false);
            SetRestartStatus(string.Empty);
        }

        /// <summary>Update the restart status line (e.g. "Please remove all tiles" / "Restarting in 3...").</summary>
        public void SetRestartStatus(string message)
        {
            if (restartStatusText != null) restartStatusText.text = message ?? string.Empty;
        }
    }
}
