using UnityEngine;
using TMPro;

namespace CityTwin.UI
{
    /// <summary>
    /// Single tutorial speech bubble. Holds a TMP reference for the controller to set text.
    /// Attach to each child of the TutorialSequenceController master GameObject.
    /// </summary>
    public class TutorialPopup : MonoBehaviour
    {
        [SerializeField] private TextMeshProUGUI label;

        public TextMeshProUGUI Label => label;

        public void SetText(string text)
        {
            if (label != null)
                label.text = text;
        }

        public void Show()
        {
            gameObject.SetActive(true);
        }

        public void Hide()
        {
            gameObject.SetActive(false);
        }
    }
}
