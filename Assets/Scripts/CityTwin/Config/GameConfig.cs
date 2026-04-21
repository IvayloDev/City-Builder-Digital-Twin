using System;
using System.Collections.Generic;
using UnityEngine;
using CityTwin.Core;

namespace CityTwin.Config
{
    /// <summary>Loaded game config (instance-held, no statics).</summary>
    [Serializable]
    public class GameConfig
    {
        public MetaData Meta;
        public SessionData Session;
        public BudgetData Budget;
        public ScoringData Scoring;
        public AccessibilityData Accessibility;
        public OscData Osc;
        public BuildingDefinition[] Buildings;
        public MapData Map;
        public TooltipsData Tooltips;
        public TutorialData Tutorial;
        public InactivityData Inactivity;
        public EndMessageData[] EndMessages;
        public Dictionary<string, Dictionary<string, string>> Localization;

        /// <summary>Map layout: hubs (nodes) and roads (edges). Like HTML generateMap. Optional obstacles mark placement inactive.</summary>
        [Serializable]
        public class MapData
        {
            public MapNodeData[] nodes;
            public MapEdgeData[] edges;
            public MapObstacleData[] obstacles;
        }

        [Serializable]
        public class MapNodeData
        {
            public float x;
            public float y;
            public float population = 50000f;
        }

        [Serializable]
        public class MapEdgeData
        {
            public int from;
            public int to;
            public float length;
        }

        [Serializable]
        public class MapObstacleData
        {
            public float x;
            public float y;
            public float radius;
            public string type;
        }

        [Serializable]
        public class MetaData
        {
            public string version = "1.0.0";
            public string defaultLanguage = "EN";
        }

        [Serializable]
        public class SessionData
        {
            public int gameplaySeconds = 270;
            public int maxPlayers = 4;
        }

        [Serializable]
        public class BudgetData
        {
            public string mode = "PerQuadrant";
            public int startingBudget = 1000;
        }

        [Serializable]
        public class ScoringData
        {
            public float qolCapPerMetric = 20f;
            public float epsilonDistance = 0.1f;
            /// <summary>Divides raw population (e.g. 80000) so the formula produces gradual metric values. Default 1000 → 80000 becomes 80.</summary>
            public float populationScale = 1000f;
        }

        [Serializable]
        public class AccessibilityData
        {
            public float walkingDistance = 0.5f;
            public float snapToTransitMaxDistance = 0.5f;
            /// <summary>Max distance from building to road segment to count as "connected" (HTML: 200).</summary>
            public float roadConnectRange = 200f;
            /// <summary>Radius around hub to count buildings for proximity score (HTML: 200).</summary>
            public float zoneRadius = 200f;
            /// <summary>Default connection radius for buildings that don't specify one (HTML: 500).</summary>
            public float defaultConnectionRadius = 500f;
        }

        [Serializable]
        public class OscData
        {
            public OscSourceData[] sources;
        }

        [Serializable]
        public class OscSourceData
        {
            public string id;
            public int listenPort;
            public string expectedSenderIp;
        }

        [Serializable]
        public class TooltipsData
        {
            public string[] introKeys;
        }

        [Serializable]
        public class EndMessageData
        {
            public int min;
            public int max;
            public string titleKey;
            public string bodyKey;
        }

        [Serializable]
        public class TutorialData
        {
            public TutorialStepData[] steps;
        }

        [Serializable]
        public class TutorialStepData
        {
            public string textKey;
            public float durationSeconds = 5f;
        }

        [Serializable]
        public class InactivityData
        {
            public float timeoutSeconds = 30f;
            public string textKey = "ui.inactivity";
        }
    }
}
