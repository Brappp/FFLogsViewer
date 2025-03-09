using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using FFLogsViewer.Model;
using ImGuiNET;

namespace FFLogsViewer.GUI.Config
{
    /// <summary>
    /// A window that displays party member threshold check results.
    /// </summary>
    public class ThresholdCheckWindow : Window
    {
        private Dictionary<string, Dictionary<int, int>> playerKillCounts = new();
        private Dictionary<string, bool> expandedThresholds = new();
        private bool autoRefresh = false; // Default to false
        private float timeSinceLastRefresh = 0;
        private const float RefreshInterval = 60.0f; // Increased to 60 seconds
        private HashSet<string> checkedPlayers = new HashSet<string>(); // Track who we've already checked
        private bool showThresholdSettings = true; // Controls visibility of the threshold settings section

        public ThresholdCheckWindow()
            : base("Kill Threshold Check##FFLogsThresholdCheckWindow")
        {
            this.SizeConstraints = new WindowSizeConstraints
            {
                MinimumSize = new Vector2(450, 300),
                MaximumSize = new Vector2(1000, 800)
            };

            this.RespectCloseHotkey = true;
            this.Flags = ImGuiWindowFlags.AlwaysAutoResize;
        }

        /// <summary>
        /// Brings the window to the front and ensures it gets focus.
        /// </summary>
        public void BringToFront()
        {
            // Set ImGui window focus flag
            ImGui.SetNextWindowFocus();

            // Force the window to the front and make sure it's visible
            this.Flags &= ~ImGuiWindowFlags.NoFocusOnAppearing;

            // Log this action
            Service.PluginLog.Debug("[KillThreshold] BringToFront called on ThresholdCheckWindow");
        }

        /// <summary>
        /// Updates the player kill counts shown in the window.
        /// </summary>
        /// <param name="playerName">Full player name (first + last).</param>
        /// <param name="world">Player's world.</param>
        /// <param name="encounterId">Encounter ID.</param>
        /// <param name="killCount">Number of kills for that encounter.</param>
        public void UpdatePlayerKillCount(string playerName, string world, int encounterId, int killCount)
        {
            string playerKey = $"{playerName}@{world}";

            if (!playerKillCounts.ContainsKey(playerKey))
            {
                playerKillCounts[playerKey] = new Dictionary<int, int>();
            }

            playerKillCounts[playerKey][encounterId] = killCount;

            // Add to the set of checked players
            checkedPlayers.Add(playerKey);
        }

        /// <summary>
        /// Clears all stored player kill counts.
        /// </summary>
        public void ClearKillCounts()
        {
            playerKillCounts.Clear();
            checkedPlayers.Clear();
        }

        public override void Draw()
        {
            // Only perform auto-refresh if enabled and at a much lower frequency
            if (autoRefresh)
            {
                timeSinceLastRefresh += ImGui.GetIO().DeltaTime;
                if (timeSinceLastRefresh >= RefreshInterval)
                {
                    timeSinceLastRefresh = 0;
                    // Only refresh for players that haven't been checked yet
                    RefreshThresholdChecksForNewPlayers();
                }
            }

            DrawSettingsBar();
            ImGui.Separator();

            // Display configured thresholds
            DrawConfiguredThresholds();
            ImGui.Separator();

            if (Service.Configuration.KillThresholds.Thresholds.Count == 0)
            {
                ImGui.TextColored(ImGuiColors.DalamudYellow, "No kill thresholds configured.");
                ImGui.TextWrapped("Add kill thresholds in the Configuration -> Kill Thresholds tab.");
                if (ImGui.Button("Open Configuration"))
                {
                    Service.ConfigWindow.IsOpen = true;
                    Service.ConfigWindow.ThresholdsTab.Draw();
                }
                return;
            }

            if (playerKillCounts.Count == 0)
            {
                ImGui.Text("No party members checked yet.");
                ImGui.Text("Press the 'Check Now' button above to run a threshold check.");
                return;
            }

            DrawThresholdResults();
        }

        private void DrawSettingsBar()
        {
            if (ImGui.Button("Check Now"))
            {
                // Full refresh - check all party members regardless of whether they've been checked
                RefreshThresholdChecks();
            }

            ImGui.SameLine();
            if (ImGui.Checkbox("Auto-check for new party members", ref autoRefresh))
            {
                // Reset timer when toggling to avoid immediate refresh
                timeSinceLastRefresh = 0;
            }

            ImGui.SameLine();
            if (ImGui.Button("Clear"))
            {
                ClearKillCounts();
            }

            ImGui.SameLine();
            if (ImGui.Checkbox("Show Thresholds", ref showThresholdSettings))
            {
                // Toggle the threshold settings visibility
            }

            ImGui.SameLine();
            // Help text
            Util.DrawHelp("This window shows which party members pass or fail your configured kill thresholds.\n\n" +
                         "Use the 'Check Now' button to manually refresh the data for all party members.\n" +
                         "Enable 'Auto-check' to automatically check only newly joined party members.\n\n" +
                         "Click the ▶ next to a player's name to see their detailed kill counts.\n" +
                         "The 'Kick' button allows you to remove players who don't meet your requirements.");
        }

        /// <summary>
        /// Draws the configured thresholds that are being checked against.
        /// </summary>
        private void DrawConfiguredThresholds()
        {
            // Only draw if there are thresholds and the user wants to see them
            if (!showThresholdSettings || Service.Configuration.KillThresholds.Thresholds.Count == 0)
                return;

            if (ImGui.CollapsingHeader("Current Kill Thresholds", ImGuiTreeNodeFlags.DefaultOpen))
            {
                // Check if we're only checking the matching encounter
                bool isFiltering = Service.Configuration.KillThresholds.CheckOnlyMatchingEncounter;
                int? currentEncounterId = Service.Configuration.KillThresholds.CurrentEncounterId;

                if (isFiltering && currentEncounterId.HasValue)
                {
                    ImGui.TextColored(ImGuiColors.HealerGreen, $"Only checking thresholds for current encounter (ID: {currentEncounterId})");

                    // Find the encounter name
                    var matchingThreshold = Service.Configuration.KillThresholds.Thresholds
                        .FirstOrDefault(t => t.EncounterId == currentEncounterId.Value);

                    if (matchingThreshold != null)
                    {
                        ImGui.TextColored(ImGuiColors.HealerGreen, $"Current encounter: {matchingThreshold.EncounterName}");
                    }
                }

                ImGui.Indent(10);

                // Draw a table to show the configured thresholds
                if (ImGui.BeginTable("ConfiguredThresholdsTable", 4, ImGuiTableFlags.Borders))
                {
                    // Set up the table headers
                    ImGui.TableSetupColumn("Encounter", ImGuiTableColumnFlags.WidthStretch);
                    ImGui.TableSetupColumn("Min Kills", ImGuiTableColumnFlags.WidthFixed, 80 * ImGuiHelpers.GlobalScale);
                    ImGui.TableSetupColumn("Notify", ImGuiTableColumnFlags.WidthFixed, 60 * ImGuiHelpers.GlobalScale);
                    ImGui.TableSetupColumn("Auto-Kick", ImGuiTableColumnFlags.WidthFixed, 80 * ImGuiHelpers.GlobalScale);
                    ImGui.TableHeadersRow();

                    // Calculate which thresholds to show
                    var thresholdsToShow = Service.Configuration.KillThresholds.Thresholds;

                    // If we're filtering, show only matching thresholds, or all if none match
                    if (isFiltering && currentEncounterId.HasValue)
                    {
                        var matchingThresholds = thresholdsToShow
                            .Where(t => t.EncounterId == currentEncounterId.Value)
                            .ToList();

                        if (matchingThresholds.Count > 0)
                        {
                            thresholdsToShow = matchingThresholds;
                        }
                    }

                    // Draw each threshold
                    foreach (var threshold in thresholdsToShow)
                    {
                        ImGui.TableNextRow();

                        // Determine if this threshold is currently active
                        bool isActive = !isFiltering || !currentEncounterId.HasValue ||
                                      threshold.EncounterId == currentEncounterId.Value;

                        // Use appropriate colors
                        using var color = ImRaii.PushColor(
                            ImGuiCol.Text,
                            isActive ? ImGuiColors.DalamudWhite : ImGuiColors.DalamudGrey);

                        // Encounter name
                        ImGui.TableNextColumn();
                        ImGui.Text(threshold.EncounterName);

                        // Minimum kills
                        ImGui.TableNextColumn();
                        ImGui.Text(threshold.MinimumKills.ToString());

                        // Show notifications
                        ImGui.TableNextColumn();
                        ImGui.Text(threshold.ShowNotification ? "Yes" : "No");

                        // Auto-kick
                        ImGui.TableNextColumn();
                        ImGui.Text(threshold.AutoKick ? "Yes" : "No");
                    }

                    ImGui.EndTable();
                }

                // Add a button to open config directly
                if (ImGui.Button("Edit Thresholds"))
                {
                    Service.ConfigWindow.IsOpen = true;
                    Service.ConfigWindow.ThresholdsTab.Draw();
                }

                ImGui.Unindent(10);
            }
        }

        /// <summary>
        /// Refreshes threshold checks for all party members.
        /// </summary>
        private void RefreshThresholdChecks()
        {
            // Use the ThresholdManager to check all party members
            Service.ThresholdManager.ForceCheckKillThresholds(this);
        }

        /// <summary>
        /// Checks only party members who haven't been checked yet.
        /// </summary>
        private void RefreshThresholdChecksForNewPlayers()
        {
            // Update the team list to get current party members
            Service.TeamManager.UpdateTeamList();

            // Check if there are any new party members to process
            bool hasNewMembers = false;
            foreach (var member in Service.TeamManager.TeamList)
            {
                string playerKey = $"{member.FirstName} {member.LastName}@{member.World}";
                if (!checkedPlayers.Contains(playerKey))
                {
                    hasNewMembers = true;
                    break;
                }
            }

            // Only do the refresh if we have new members
            if (hasNewMembers)
            {
                Service.PluginLog.Information("[KillThreshold] Found new party members, running check");
                RefreshThresholdChecks();
            }
        }

        private void DrawThresholdResults()
        {
            if (ImGui.BeginTable("ThresholdResultsTable", 3, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
            {
                ImGui.TableSetupColumn("Player", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthFixed, 100 * ImGuiHelpers.GlobalScale);
                ImGui.TableSetupColumn("Actions", ImGuiTableColumnFlags.WidthFixed, 80 * ImGuiHelpers.GlobalScale);
                ImGui.TableHeadersRow();

                // For each player with data
                foreach (var playerEntry in playerKillCounts)
                {
                    string playerKey = playerEntry.Key;
                    var killCounts = playerEntry.Value;

                    // Split the playerKey into name and world
                    string[] playerParts = playerKey.Split('@');
                    if (playerParts.Length != 2) continue;

                    string playerName = playerParts[0];
                    string world = playerParts[1];

                    // Determine which thresholds to check against
                    var thresholdsToCheck = Service.Configuration.KillThresholds.Thresholds;

                    // If filtering is enabled and we have a current encounter, filter the thresholds
                    if (Service.Configuration.KillThresholds.CheckOnlyMatchingEncounter &&
                        Service.Configuration.KillThresholds.CurrentEncounterId.HasValue)
                    {
                        int currentId = Service.Configuration.KillThresholds.CurrentEncounterId.Value;
                        var matchingThresholds = thresholdsToCheck
                            .Where(t => t.EncounterId == currentId)
                            .ToList();

                        if (matchingThresholds.Count > 0)
                        {
                            thresholdsToCheck = matchingThresholds;
                        }
                    }

                    // Check if player fails any thresholds
                    bool passesAllThresholds = true;
                    foreach (var threshold in thresholdsToCheck)
                    {
                        if (killCounts.TryGetValue(threshold.EncounterId, out int kills))
                        {
                            if (kills < threshold.MinimumKills)
                            {
                                passesAllThresholds = false;
                                break;
                            }
                        }
                        else
                        {
                            // No data for this encounter, assume failure
                            passesAllThresholds = false;
                            break;
                        }
                    }

                    ImGui.TableNextRow();

                    // Column 1: Player name
                    ImGui.TableNextColumn();
                    bool isExpanded = expandedThresholds.TryGetValue(playerKey, out bool expanded) && expanded;
                    if (ImGui.TreeNodeEx(playerName, isExpanded ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None))
                    {
                        expandedThresholds[playerKey] = true;

                        // Show detailed threshold results for this player
                        foreach (var threshold in thresholdsToCheck)
                        {
                            int kills = killCounts.TryGetValue(threshold.EncounterId, out int k) ? k : 0;
                            bool passesThreshold = kills >= threshold.MinimumKills;

                            using (var color = ImRaii.PushColor(ImGuiCol.Text, passesThreshold ? ImGuiColors.HealerGreen : ImGuiColors.DalamudRed))
                            {
                                ImGui.Text($"{threshold.EncounterName}: {kills}/{threshold.MinimumKills} kills");
                            }
                        }

                        ImGui.TreePop();
                    }
                    else
                    {
                        expandedThresholds[playerKey] = false;
                        ImGui.SameLine();
                        ImGui.Text($"@{world}");
                    }

                    // Column 2: Status 
                    ImGui.TableNextColumn();
                    if (passesAllThresholds)
                    {
                        ImGui.TextColored(ImGuiColors.HealerGreen, "PASS");
                    }
                    else
                    {
                        ImGui.TextColored(ImGuiColors.DalamudRed, "FAIL");
                    }

                    // Column 3: Actions
                    ImGui.TableNextColumn();
                    if (!passesAllThresholds)
                    {
                        if (ImGui.Button($"Kick##Kick{playerKey}"))
                        {
                            // Show confirmation popup
                            ImGui.OpenPopup($"KickConfirm{playerKey}");
                        }

                        // Confirmation popup
                        string popupId = $"KickConfirm{playerKey}";
                        if (ImGui.BeginPopup(popupId))
                        {
                            ImGui.TextColored(ImGuiColors.DalamudRed, "Are you sure?");
                            ImGui.TextWrapped("Kicking players based on logs may be against the FFXIV Terms of Service.");

                            if (ImGui.Button("Yes, Kick"))
                            {
                                string[] nameParts = playerName.Split(' ', 2);
                                if (nameParts.Length == 2)
                                {
                                    Service.CommandManager.ProcessCommand($"/kick {nameParts[0]} {nameParts[1]}");
                                    Service.ChatGui.Print($"[KillThreshold] Kicked {playerName} for not meeting kill requirements.");
                                }
                                ImGui.CloseCurrentPopup();
                            }

                            ImGui.SameLine();
                            if (ImGui.Button("Cancel"))
                            {
                                ImGui.CloseCurrentPopup();
                            }

                            ImGui.EndPopup();
                        }
                    }
                    else
                    {
                        ImGui.TextDisabled("-");
                    }
                }

                ImGui.EndTable();
            }
        }

        public override void OnOpen()
        {
            Service.PluginLog.Debug("[KillThreshold] ThresholdCheckWindow.OnOpen called");
            BringToFront(); // Ensure window gets focus when opened
        }

        public override void OnClose()
        {
            Service.PluginLog.Debug("[KillThreshold] ThresholdCheckWindow.OnClose called");
            // Optional: Clear data when window is closed
            // ClearKillCounts();
        }
    }
}
