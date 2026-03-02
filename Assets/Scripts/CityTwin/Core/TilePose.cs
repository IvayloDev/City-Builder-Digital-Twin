using UnityEngine;

namespace CityTwin.Core
{
    /// <summary>Unified tile pose from OSC: position, rotation, building id, source/quadrant, and stable tile id for removal.</summary>
    public struct TilePose
    {
        public Vector2 Position;
        public float Rotation;
        public string BuildingId;
        public int SourceId;
        /// <summary>Stable id from TileTrackingManager; use for OnTileRemoved mapping.</summary>
        public string TileId;

        public TilePose(Vector2 position, float rotation, string buildingId, int sourceId, string tileId = null)
        {
            Position = position;    
            Rotation = rotation;
            BuildingId = buildingId;
            SourceId = sourceId;
            TileId = tileId;
        }
    }
}
