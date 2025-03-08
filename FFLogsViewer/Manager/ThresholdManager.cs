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
        /// Opens the threshold check window.
        /// </summary>
        /// <param name="runCheck">If true, runs a check immediately upon opening.</param>
        public void OpenThresholdCheckWindow(bool runCheck = false)
        {
            // Find the window in WindowSystem instead of creating a new one
            var windowSystem = Service.Interface.UiBuilder.Windows;
            thresholdCheckWindow = windowSystem.Windows.FirstOrDefault(w => w is ThresholdCheckWindow) as ThresholdCheckWindow;

            if (thresholdCheckWindow == null)
            {
                Service.PluginLog.Warning("Could not find ThresholdCheckWindow in WindowSystem");
                // This should not happen as window is registered in FFLogsViewer constructor
                return;
            }

            thresholdCheckWindow.IsOpen = true;
            Service.ChatGui.Print("[KillThreshold] Opening threshold check window.");

            if (runCheck)
            {
                Service.ChatGui.Print("[KillThreshold] Running threshold check...");
                ForceCheckKillThresholds(thresholdCheckWindow);
            }
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
                var windowSystem = Service.Interface.UiBuilder.Windows;
                windowToUpdate = windowSystem.Windows.FirstOrDefault(w => w is ThresholdCheckWindow) as ThresholdCheckWindow;
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

                // Check each threshold for this player
                foreach (KillThreshold threshold in Service.Configuration.KillThresholds.Thresholds)
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
