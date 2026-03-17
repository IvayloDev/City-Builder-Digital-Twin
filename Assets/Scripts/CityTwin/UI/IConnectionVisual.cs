using UnityEngine;

namespace CityTwin.UI
{
    /// <summary>
    /// Contract for any visual representation of a building-to-hub connection line.
    /// Implement on a MonoBehaviour placed on the connection prefab.
    /// Swap implementations (Image, LineRenderer, particles) without touching HubConnectionRenderer.
    /// </summary>
    public interface IConnectionVisual
    {
        void UpdateEndpoints(Vector2 from, Vector2 to);
        void SetActive(bool active);
    }
}
