using UnityEngine;
using UnityEngine.SceneManagement;

namespace CityTwin.Core
{
    /// <summary>
    /// Used in Boot scene: loads the Game scene after a short delay.
    /// Config is loaded per Game Instance, not in Boot. No statics.
    /// </summary>
    public class Bootstrap : MonoBehaviour
    {
        [Tooltip("Scene name to load after Boot (e.g. Game).")]
        [SerializeField] private string gameSceneName = "Game";

        private void Start()
        {
            if (SceneManager.GetActiveScene().name == "Boot")
                Invoke(nameof(LoadGameScene), 0.1f);
        }

        private void LoadGameScene()
        {
            SceneManager.LoadScene(gameSceneName);
        }
    }
}
