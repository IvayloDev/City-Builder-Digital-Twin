using System;
using UnityEngine;

namespace CityTwin.Core
{
    /// <summary>Building definition DTO matching game_config.json. No statics.</summary>
    [Serializable]
    public class BuildingDefinition
    {
        public string Id;
        public string Category;
        public string ImpactSize;
        public float Importance;
        public int Price;
        public MetricValues BaseValues;
        /// <summary>Max distance to hub for this building to affect it. 0 = use config default (500).</summary>
        public float ConnectionRadius;
        public string LocalizationKey;

        [Serializable]
        public class MetricValues
        {
            public float environment;
            public float economy;
            public float healthSafety;
            public float cultureEdu;
        }
    }
}
