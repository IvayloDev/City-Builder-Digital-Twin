using System;
using System.Collections.Generic;
using UnityEngine;

namespace CityTwin.Core
{
    /// <summary>Per-instance quadrant/player state: budget, score, placed tiles. No statics.</summary>
    [Serializable]
    public class QuadrantState
    {
        public int Budget;
        public float QolScore;
        public List<TilePlacement> PlacedTiles = new List<TilePlacement>();

        [Serializable]
        public struct TilePlacement
        {
            public string TileId;
            public string BuildingId;
            public Vector2 Position;
            public float Rotation;
        }
    }
}
