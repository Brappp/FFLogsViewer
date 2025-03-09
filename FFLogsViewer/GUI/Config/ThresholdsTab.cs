using System.Linq;
using System.Numerics;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility.Raii;
using FFLogsViewer.Manager;
using FFLogsViewer.Model;
using ImGuiNET;

namespace FFLogsViewer.GUI.Config
{
    /// <summary>
    /// The ImGui configuration tab for Kill Thresholds.
    /// </summary>
    public class ThresholdsTab
    {
        // Field declarations.
        private int selectedEncounterId = -1;
        private string selectedEncounterName = string.Empty;
        private int minimumKills = 1;
        private bool showNotification = true;
        private bool autoKick = false;

        /// <summary>
        /// Draws the Kill Thresholds configuration tab.
        /// </summary>
        public void Draw()
        {
            var settings = Service.Configuration.KillThresholds;
            var hasChanged = false;

            // Main toggle for kill threshold checking.
            bool enableKillChecking = settings.EnableKillChecking;
            if (ImGui.Checkbox("Enable Kill Threshold Checking", ref enableKillChecking))
            {
                settings.EnableKillChecking = enableKillChecking;
                hasChanged = true;
            }

            if (enableKillChecking)
            {
                ImGui.Indent();

                // New button to force a kill threshold check.
                if (ImGui.Button("Force Check Kill Thresholds"))
                {
                    // Print a chat message to indicate the force check is running.
                    Service.ChatGui.Print("[KillThreshold] Force check initiated by user.");
                    // Call the force check method in the ThresholdManager.
                    Service.ThresholdManager.ForceCheckKillThresholds();
                }
                Util.DrawHelp("Click to manually force a kill threshold check.");

                // New setting to only check the matching encounter.
                bool checkOnlyMatchingEncounter = settings.CheckOnlyMatchingEncounter;
                if (ImGui.Checkbox("Only check thresholds for current Party Finder encounter", ref checkOnlyMatchingEncounter))
                {
                    settings.CheckOnlyMatchingEncounter = checkOnlyMatchingEncounter;
                    hasChanged = true;
                }
                Util.DrawHelp("When enabled, only applies thresholds for the encounter you're currently in or have listed in Party Finder.");

                bool checkOnPartyJoin = settings.CheckOnPartyJoin;
                if (ImGui.Checkbox("Check automatically when players join the party", ref checkOnPartyJoin))
                {
                    settings.CheckOnPartyJoin = checkOnPartyJoin;
                    hasChanged = true;
                }
                Util.DrawHelp("When enabled, the system will automatically check thresholds when party composition changes.\nA window will pop up showing results for each party member.");

                // Test button.
                if (ImGui.Button("Test Party Detection"))
                {
                    Service.ChatGui.Print("[KillThreshold] Testing party threshold detection...");
                    Service.ThresholdManager.ForceCheckKillThresholds();
                }
                Util.DrawHelp("Click to manually test threshold checking for current party members.");

                ImGui.Separator();
                ImGui.Text("Configured Thresholds:");

                // Display existing thresholds.
                if (settings.Thresholds.Count > 0)
                {
                    if (ImGui.BeginTable("ThresholdsTable", 5, ImGuiTableFlags.Borders))
                    {
                        ImGui.TableSetupColumn("Encounter", ImGuiTableColumnFlags.WidthStretch);
                        ImGui.TableSetupColumn("Min Kills", ImGuiTableColumnFlags.WidthFixed, 80 * ImGui.GetIO().FontGlobalScale);
                        ImGui.TableSetupColumn("Notify", ImGuiTableColumnFlags.WidthFixed, 60 * ImGui.GetIO().FontGlobalScale);
                        ImGui.TableSetupColumn("Auto-Kick", ImGuiTableColumnFlags.WidthFixed, 80 * ImGui.GetIO().FontGlobalScale);
                        ImGui.TableSetupColumn("##Actions", ImGuiTableColumnFlags.WidthFixed, 30 * ImGui.GetIO().FontGlobalScale);
                        ImGui.TableHeadersRow();

                        // Track if any threshold was removed to prevent index issues.
                        bool removedThreshold = false;
                        int indexToRemove = -1;

                        for (int i = 0; i < settings.Thresholds.Count; i++)
                        {
                            var threshold = settings.Thresholds[i];

                            ImGui.TableNextRow();

                            // Column: Encounter Name.
                            ImGui.TableNextColumn();
                            ImGui.Text(threshold.EncounterName);

                            // Column: Minimum Kills.
                            ImGui.TableNextColumn();
                            // Display a decrement button.
                            if (ImGui.Button($"-##kills_decr_{i}"))
                            {
                                // Decrease threshold ensuring it does not drop below 0.
                                if (threshold.MinimumKills > 0)
                                {
                                    threshold.MinimumKills--;
                                    hasChanged = true;
                                }
                            }
                            ImGui.SameLine();
                            // Display the current threshold number.
                            ImGui.Text($"{threshold.MinimumKills}");
                            ImGui.SameLine();
                            // Display an increment button.
                            if (ImGui.Button($"+##kills_incr_{i}"))
                            {
                                threshold.MinimumKills++;
                                hasChanged = true;
                            }

                            // Column: Notification toggle.
                            ImGui.TableNextColumn();
                            bool notify = threshold.ShowNotification;
                            if (ImGui.Checkbox($"##notify{i}", ref notify))
                            {
                                threshold.ShowNotification = notify;
                                hasChanged = true;
                            }

                            // Column: Auto-Kick toggle.
                            ImGui.TableNextColumn();
                            bool kick = threshold.AutoKick;
                            if (ImGui.Checkbox($"##kick{i}", ref kick))
                            {
                                if (kick)
                                {
                                    ImGui.OpenPopup($"KickWarningPopup{i}");
                                }
                                else
                                {
                                    threshold.AutoKick = false;
                                    hasChanged = true;
                                }
                            }

                            // Warning popup for auto-kick.
                            if (ImGui.BeginPopup($"KickWarningPopup{i}"))
                            {
                                ImGui.TextColored(ImGuiColors.DalamudRed, "Warning!");
                                ImGui.Text("Auto-kicking players may be against FFXIV's Terms of Service\n" +
                                          "and can be considered toxic behavior. Are you sure?");

                                if (ImGui.Button("Yes, Enable Auto-Kick"))
                                {
                                    threshold.AutoKick = true;
                                    hasChanged = true;
                                    ImGui.CloseCurrentPopup();
                                }
                                ImGui.SameLine();
                                if (ImGui.Button("Cancel"))
                                {
                                    ImGui.CloseCurrentPopup();
                                }
                                ImGui.EndPopup();
                            }

                            // Column: Actions (Delete threshold).
                            ImGui.TableNextColumn();
                            using (var font = ImRaii.PushFont(UiBuilder.IconFont))
                            {
                                // Fix for delete button - store index to remove after finishing table iteration.
                                if (ImGui.Button($"{FontAwesomeIcon.Trash.ToIconString()}##{i}"))
                                {
                                    indexToRemove = i;
                                    removedThreshold = true;
                                }
                            }
                        }

                        ImGui.EndTable();

                        // Remove threshold after the table loop to avoid issues.
                        if (removedThreshold && indexToRemove >= 0 && indexToRemove < settings.Thresholds.Count)
                        {
                            settings.Thresholds.RemoveAt(indexToRemove);
                            hasChanged = true;
                        }
                    }
                }
                else
                {
                    ImGui.TextColored(ImGuiColors.DalamudGrey, "No thresholds configured. Add one below.");
                }

                ImGui.Separator();
                ImGui.Text("Add New Threshold");

                // Select encounter.
                if (ImGui.BeginCombo("Encounter", selectedEncounterName == string.Empty ? "Select an encounter" : selectedEncounterName))
                {
                    var encountersList = Service.Configuration.Layout
                        .Where(entry => entry.Type == LayoutEntryType.Encounter)
                        .OrderBy(entry => entry.Expansion)
                        .ThenBy(entry => entry.Zone);

                    foreach (var entry in encountersList)
                    {
                        string encounterDisplay = $"{entry.Encounter} ({entry.Zone})";
                        if (ImGui.Selectable(encounterDisplay))
                        {
                            selectedEncounterId = entry.EncounterId;
                            selectedEncounterName = entry.Encounter;
                        }
                    }
                    ImGui.EndCombo();
                }

                // Minimum kills input.
                ImGui.InputInt("Minimum Kills", ref minimumKills);
                if (minimumKills < 0) minimumKills = 0;

                // Options.
                ImGui.Checkbox("Show Notification", ref showNotification);
                if (ImGui.Checkbox("Auto-Kick (Not Recommended)", ref autoKick))
                {
                    if (autoKick)
                    {
                        ImGui.OpenPopup("AddKickWarningPopup");
                    }
                }

                // Warning popup for auto-kick when adding a new threshold.
                if (ImGui.BeginPopup("AddKickWarningPopup"))
                {
                    ImGui.TextColored(ImGuiColors.DalamudRed, "Warning!");
                    ImGui.Text("Auto-kicking players may be against FFXIV's Terms of Service\n" +
                              "and can be considered toxic behavior. Are you sure?");
                    if (ImGui.Button("Yes, Enable Auto-Kick"))
                    {
                        autoKick = true;
                        ImGui.CloseCurrentPopup();
                    }
                    ImGui.SameLine();
                    if (ImGui.Button("Cancel"))
                    {
                        autoKick = false;
                        ImGui.CloseCurrentPopup();
                    }
                    ImGui.EndPopup();
                }

                // Button to add a new threshold entry.
                ImGui.BeginDisabled(selectedEncounterId == -1);
                if (ImGui.Button("Add Threshold"))
                {
                    settings.Thresholds.Add(new KillThreshold
                    {
                        EncounterId = selectedEncounterId,
                        EncounterName = selectedEncounterName,
                        MinimumKills = minimumKills,
                        ShowNotification = showNotification,
                        AutoKick = autoKick
                    });

                    // Reset form values.
                    selectedEncounterId = -1;
                    selectedEncounterName = string.Empty;
                    minimumKills = 1;
                    showNotification = true;
                    autoKick = false;

                    hasChanged = true;
                }
                ImGui.EndDisabled();

                ImGui.Unindent();
            }

            if (hasChanged)
            {
                Service.Configuration.Save();
            }
        }
    }
}
