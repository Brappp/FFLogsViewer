using System;
using System.Linq;
using FFLogsViewer.Model;
using Dalamud.Interface;
using Dalamud.Logging;
using FFLogsViewer.GUI.Config;

namespace FFLogsViewer.Manager
{
    /// <summary>
    /// Manages kill threshold checks.
    /// </summary>
    public class ThresholdManager
    {
        private ThresholdCheckWindow? thresholdCheckWindow;

        /// <summary>
        /// Initializes a new instance of the <see cref="ThresholdManager"/> class.
        /// </summary>
        public ThresholdManager()
        {
            // The window will be created on-demand
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="ThresholdManager"/> class with additional parameters.
        /// This overload is provided to satisfy calls that pass two arguments.
        /// </summary>
        /// <param name="arg1">First argument (not used).</param>
        /// <param name="arg2">Second argument (not used).</param>
        public ThresholdManager(object arg1, object arg2)
            : this()
        {
            // You can store or use the arguments if needed.
        }

        /// <summary>
        /// Sets the reference to the ThresholdCheckWindow.
        /// </summary>
        /// <param name="window">The ThresholdCheckWindow instance.</param>
        public void SetThresholdCheckWindow(ThresholdCheckWindow window)
        {
            thresholdCheckWindow = window;
            Service.PluginLog.Information("[KillThreshold] ThresholdCheckWindow reference set");
        }

        /// <summary>
        /// Opens the threshold check window.
        /// </summary>
        /// <param name="runCheck">If true, runs a check immediately upon opening.</param>
        public void OpenThresholdCheckWindow(bool runCheck = false)
        {
            // Check if we have a reference to the window
            if (thresholdCheckWindow == null)
            {
                // Try to use the service reference as fallback
                if (Service.ThresholdCheckWindow != null)
                {
                    thresholdCheckWindow = Service.ThresholdCheckWindow;
                    Service.PluginLog.Information("[KillThreshold] Retrieved window reference from Service");
                }
                else
                {
                    Service.PluginLog.Warning("[KillThreshold] ThresholdCheckWindow reference not set");
                    Service.ChatGui.Print("[KillThreshold] Could not open threshold window. Try reopening the plugin.");
                    return;
                }
            }

            // Ensure window gets focus
            thresholdCheckWindow.IsOpen = true;
            thresholdCheckWindow.BringToFront();

            Service.PluginLog.Information($"[KillThreshold] Window IsOpen status: {thresholdCheckWindow.IsOpen}");
            Service.ChatGui.Print("[KillThreshold] Opening threshold check window.");

            if (runCheck)
            {
                Service.ChatGui.Print("[KillThreshold] Running threshold check...");
                ForceCheckKillThresholds(thresholdCheckWindow);
            }
        }

        /// <summary>
        /// Attempts to determine the current encounter from Party Finder or content
        /// </summary>
        /// <returns>The encounter ID if found, otherwise null</returns>
        private int? DetermineCurrentEncounter()
        {
            // For now, this is a placeholder. In a full implementation, you would:
            // 1. Check if in an instance and determine which one
            // 2. Check Party Finder listings if player is recruiting
            // 3. Check what PF the player has joined

            // TODO: Implement proper detection of current encounter

            // For testing, return null (which means check all thresholds)
            return Service.Configuration.KillThresholds.CurrentEncounterId;
        }

        /// <summary>
        /// Forces a check of all kill thresholds, iterating over every member in the current party.
        /// </summary>
        public void ForceCheckKillThresholds(ThresholdCheckWindow? windowToUpdate = null)
        {
            // Only notify in chat if not updating a window directly
            if (windowToUpdate == null)
            {
                Service.ChatGui.Print("[KillThreshold] Force check initiated.");
                Service.PluginLog.Information("Force kill threshold check triggered.");
            }

            // Find the window if not provided
            if (windowToUpdate == null)
            {
                windowToUpdate = thresholdCheckWindow;

                // If we still don't have a reference, try the service
                if (windowToUpdate == null)
                {
                    windowToUpdate = Service.ThresholdCheckWindow;
                    if (windowToUpdate != null)
                    {
                        Service.PluginLog.Information("[KillThreshold] Retrieved window from Service for check");
                    }
                }

                // Make sure to open the window if we found one
                if (windowToUpdate != null)
                {
                    windowToUpdate.IsOpen = true;
                    windowToUpdate.BringToFront();
                }
            }

            // If updating a window, clear existing data
            windowToUpdate?.ClearKillCounts();

            // Ensure kill thresholds are configured.
            if (Service.Configuration.KillThresholds == null ||
                Service.Configuration.KillThresholds.Thresholds == null ||
                Service.Configuration.KillThresholds.Thresholds.Count == 0)
            {
                Service.ChatGui.Print("[KillThreshold] No kill thresholds configured.");
                return;
            }

            // Try to determine current encounter if we're checking only the matching encounter
            int? currentEncounterId = null;
            var thresholdsToCheck = Service.Configuration.KillThresholds.Thresholds;

            if (Service.Configuration.KillThresholds.CheckOnlyMatchingEncounter)
            {
                currentEncounterId = DetermineCurrentEncounter();

                // If we found a current encounter, filter the thresholds
                if (currentEncounterId.HasValue)
                {
                    thresholdsToCheck = Service.Configuration.KillThresholds.Thresholds
                        .Where(t => t.EncounterId == currentEncounterId.Value)
                        .ToList();

                    if (thresholdsToCheck.Count == 0)
                    {
                        Service.ChatGui.Print($"[KillThreshold] No threshold configured for current encounter (ID: {currentEncounterId})");
                        // Fall back to checking all thresholds
                        thresholdsToCheck = Service.Configuration.KillThresholds.Thresholds;
                    }
                    else
                    {
                        Service.ChatGui.Print($"[KillThreshold] Checking {thresholdsToCheck.Count} threshold(s) for current encounter: {thresholdsToCheck[0].EncounterName}");
                    }
                }
            }

            // Attempt to update the party list from your team or party manager.
            Service.TeamManager.UpdateTeamList();

            // Retrieve the current party members.
            var partyMembers = Service.TeamManager.TeamList;
            if (partyMembers == null || partyMembers.Count == 0)
            {
                Service.ChatGui.Print("[KillThreshold] No party members found or not currently in a party.");
                Service.PluginLog.Information("No party members found; skipping kill threshold checks.");
                return;
            }

            // Count how many members pass/fail
            int passCount = 0;
            int failCount = 0;

            // Loop through each party member
            foreach (var member in partyMembers)
            {
                bool hasFailedAnyThreshold = false;
                string playerFullName = $"{member.FirstName} {member.LastName}";

                // Check each applicable threshold for this player
                foreach (KillThreshold threshold in thresholdsToCheck)
                {
                    // Retrieve kill count for this encounter
                    int currentKills = GetKillCountForEncounter(
                        member.FirstName,
                        member.LastName,
                        member.World,
                        threshold.EncounterId);

                    // Update the window with this data if we have one
                    windowToUpdate?.UpdatePlayerKillCount(playerFullName, member.World, threshold.EncounterId, currentKills);

                    // Log the check details
                    Service.PluginLog.Information(
                        $"[KillThreshold] Checking {threshold.EncounterName} for {playerFullName}@{member.World}: " +
                        $"current kills = {currentKills}, minimum required = {threshold.MinimumKills}.");

                    // If the current kills are less than required, send a warning
                    if (currentKills < threshold.MinimumKills)
                    {
                        hasFailedAnyThreshold = true;

                        if (threshold.ShowNotification && windowToUpdate == null)
                        {
                            Service.ChatGui.Print(
                                $"[KillThreshold] {playerFullName}@{member.World} has only {currentKills}/{threshold.MinimumKills} kills " +
                                $"for {threshold.EncounterName}.");
                        }

                        // Auto-kick functionality if enabled
                        if (threshold.AutoKick)
                        {
                            if (windowToUpdate == null)
                            {
                                Service.ChatGui.Print(
                                    $"[KillThreshold] Auto-kick triggered for {playerFullName}@{member.World} " +
                                    $"(insufficient kills for {threshold.EncounterName})");
                            }

                            if (threshold.AutoKick)
                            {
                                // Only execute the kick command if auto-kick is enabled
                                Service.CommandManager.ProcessCommand($"/kick {member.FirstName} {member.LastName}");
                            }
                        }
                    }
                }

                // Track overall pass/fail for summary
                if (hasFailedAnyThreshold)
                {
                    failCount++;
                }
                else
                {
                    passCount++;
                    if (windowToUpdate == null)
                    {
                        Service.ChatGui.Print(
                            $"[KillThreshold] {playerFullName}@{member.World} meets all thresholds.");
                    }
                }
            }

            // Print summary to chat (only if not updating a window directly)
            if (windowToUpdate == null)
            {
                Service.ChatGui.Print(
                    $"[KillThreshold] Check complete: {passCount} members pass, {failCount} members fail requirements.");

                // Open the threshold check window if people have failed
                if (failCount > 0)
                {
                    Service.ChatGui.Print(
                        $"[KillThreshold] Opening threshold check window to display results.");
                    OpenThresholdCheckWindow(false); // Don't run check again, we just did it
                }
                else
                {
                    // Still open window even if all pass
                    OpenThresholdCheckWindow(false);
                }
            }
        }

        /// <summary>
        /// Sets the current encounter ID for targeted threshold checking
        /// </summary>
        /// <param name="encounterId">The encounter ID</param>
        public void SetCurrentEncounter(int encounterId)
        {
            // This would be called by your PF detection or duty detection code
            Service.Configuration.KillThresholds.CurrentEncounterId = encounterId;

            // Get the encounter name for logging
            string encounterName = "Unknown";
            var threshold = Service.Configuration.KillThresholds.Thresholds.FirstOrDefault(t => t.EncounterId == encounterId);
            if (threshold != null)
            {
                encounterName = threshold.EncounterName;
            }
            else
            {
                // Try to find it in the layout
                var layoutEntry = Service.Configuration.Layout.FirstOrDefault(l => l.EncounterId == encounterId);
                if (layoutEntry != null)
                {
                    encounterName = layoutEntry.Encounter;
                }
            }

            Service.PluginLog.Information($"[KillThreshold] Current encounter set to: {encounterName} (ID: {encounterId})");
        }

        /// <summary>
        /// Clears the current encounter ID
        /// </summary>
        public void ClearCurrentEncounter()
        {
            Service.Configuration.KillThresholds.CurrentEncounterId = null;
            Service.PluginLog.Information("[KillThreshold] Current encounter cleared");
        }

        /// <summary>
        /// Retrieves the current kill count for a given encounter for the specified player.
        /// You must ensure your CharDataManager can fetch data for them by first/last/world.
        /// </summary>
        /// <param name="firstName">Player's first name.</param>
        /// <param name="lastName">Player's last name.</param>
        /// <param name="world">Player's world name.</param>
        /// <param name="encounterId">The encounter ID.</param>
        /// <returns>The number of kills for that encounter. Returns 0 if not found or data unavailable.</returns>
        private int GetKillCountForEncounter(string firstName, string lastName, string world, int encounterId)
        {
            // Acquire a CharData object from your manager, using whichever method is appropriate in your codebase.
            var charData = Service.CharDataManager.GetCharData(firstName, lastName, world);
            if (charData != null && charData.Encounters != null)
            {
                foreach (var encounter in charData.Encounters)
                {
                    if (encounter.Id == encounterId)
                    {
                        return encounter.Kills ?? 0;
                    }
                }
            }

            return 0;
        }

        /// <summary>
        /// Overload of OnPlayerJoinedParty that takes three arguments.
        /// Calls the full threshold check (which checks every party member).
        /// </summary>
        /// <param name="arg1">First argument.</param>
        /// <param name="arg2">Second argument.</param>
        /// <param name="arg3">Third argument.</param>
        /// <returns>A string message indicating the check was performed.</returns>
        public string OnPlayerJoinedParty(object arg1, object arg2, object arg3)
        {
            if (!Service.Configuration.KillThresholds.EnableKillChecking ||
                !Service.Configuration.KillThresholds.CheckOnPartyJoin)
                return "Kill threshold checking disabled.";

            ForceCheckKillThresholds();
            return "Kill thresholds checked due to party join.";
        }

        /// <summary>
        /// Overload of CheckPlayerKills that takes three arguments.
        /// It also calls the full threshold check, thus iterating over every party member.
        /// </summary>
        /// <param name="arg1">First argument.</param>
        /// <param name="arg2">Second argument.</param>
        /// <param name="arg3">Third argument.</param>
        /// <returns>A string message indicating the check was performed.</returns>
        public string CheckPlayerKills(object arg1, object arg2, object arg3)
        {
            ForceCheckKillThresholds();
            return "Kill thresholds checked.";
        }
    }
}
