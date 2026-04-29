using System;
using UnityEngine;

namespace CityTwin.Config
{
    /// <summary>JSON DTO for building (lowercase for JsonUtility).</summary>
    [Serializable]
    public class BuildingDto
    {
        public string id;
        public string category;
        public string impactSize;
        public float importance;
        public int price;
        public BaseValuesDto baseValues;
        public float connectionRadius;
        public string localizationKey;

        [Serializable]
        public class BaseValuesDto
        {
            public float environment;
            public float economy;
            public float healthSafety;
            public float cultureEdu;
        }
    }

    /// <summary>Wrapper for JsonUtility array parsing.</summary>
    [Serializable]
    public class GameConfigRoot
    {
        public GameConfig.MetaData meta;
        public GameConfig.SessionData session;
        public GameConfig.BudgetData budget;
        public GameConfig.ScoringData scoring;
        public GameConfig.AccessibilityData accessibility;
        public GameConfig.OscData osc;
        public BuildingDto[] buildings;
        public GameConfig.MapData map;
        public GameConfig.TooltipsData tooltips;
        public GameConfig.StopsData stops;
        public GameConfig.TutorialData tutorial;
        public GameConfig.InactivityData inactivity;
        public GameConfig.EndMessageData[] endMessages;
    }
}
