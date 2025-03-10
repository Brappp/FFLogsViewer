using System;
using System.Collections.Generic;

namespace FFLogsViewer.Model
{
    /// <summary>
    /// Represents a single kill threshold configuration for a specific encounter.
    /// </summary>
    [Serializable]
    public class KillThreshold
    {
        public int EncounterId { get; set; }
        public string EncounterName { get; set; } = string.Empty;
        public int MinimumKills { get; set; } = 1;
        public bool IsEnabled { get; set; } = true;
        public bool ShowNotification { get; set; } = true;
        public bool AutoKick { get; set; } = false; // Default to false for safety
    }

    /// <summary>
    /// Holds the global kill-threshold-related settings and a list of thresholds.
    /// </summary>
    [Serializable]
    public class KillThresholdSettings
    {
        public bool EnableKillChecking { get; set; } = false;
        public bool CheckOnPartyJoin { get; set; } = true;
        public bool CheckOnlyIfPartyLeader { get; set; } = true;
        public bool CheckOnlyMatchingEncounter { get; set; } = true; // New setting for targeted checking
        public List<KillThreshold> Thresholds { get; set; } = new();

        /// <summary>
        /// Current encounter ID from Party Finder or content the player is in.
        /// This will be set dynamically based on game state.
        /// </summary>
        [NonSerialized]
        public int? CurrentEncounterId = null;
    }
}
