using System;
using System.Collections.Generic;
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
        private bool autoRefresh = true;
        private float timeSinceLastRefresh = 0;
        private const float RefreshInterval = 5.0f; // Refresh every 5 seconds when auto-refresh is on

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
        }

        /// <summary>
        /// Clears all stored player kill counts.
        /// </summary>
        public void ClearKillCounts()
        {
            playerKillCounts.Clear();
        }

        public override void Draw()
        {
            // Auto-refresh logic
            if (autoRefresh)
            {
                timeSinceLastRefresh += ImGui.GetIO().DeltaTime;
                if (timeSinceLastRefresh >= RefreshInterval)
                {
                    timeSinceLastRefresh = 0;
                    RefreshThresholdChecks();
                }
            }

            DrawSettingsBar();
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
                RefreshThresholdChecks();
            }

            ImGui.SameLine();
            ImGui.Checkbox("Auto-refresh", ref autoRefresh);

            if (autoRefresh)
            {
                ImGui.SameLine();
                // Show countdown to next refresh
                float timeRemaining = RefreshInterval - timeSinceLastRefresh;
                ImGui.Text($"(Next: {timeRemaining:F1}s)");
            }

            ImGui.SameLine();
            if (ImGui.Button("Clear"))
            {
                ClearKillCounts();
            }

            ImGui.SameLine();
            // Help text
            Util.DrawHelp("This window shows which party members pass or fail your configured kill thresholds.\n\n" +
                         "Use the 'Check Now' button to manually refresh the data.\n" +
                         "Enable 'Auto-refresh' to automatically check every 5 seconds.\n\n" +
                         "Click the ▶ next to a player's name to see their detailed kill counts.\n" +
                         "The 'Kick' button allows you to remove players who don't meet your requirements.");
        }

        private void RefreshThresholdChecks()
        {
            // Use the ThresholdManager to check all party members
            Service.ThresholdManager.ForceCheckKillThresholds(this);
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

                    // Check if player fails any thresholds
                    bool passesAllThresholds = true;
                    foreach (var threshold in Service.Configuration.KillThresholds.Thresholds)
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
                        foreach (var threshold in Service.Configuration.KillThresholds.Thresholds)
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

        public override void OnClose()
        {
            // Optional: Clear data when window is closed
            // ClearKillCounts();
        }
    }
}
