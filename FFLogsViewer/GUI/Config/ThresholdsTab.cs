using System.Linq;
using System.Numerics;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility.Raii;
using FFLogsViewer.Manager;
using FFLogsViewer.Model;
using ImGuiNET;

namespace FFLogsViewer.GUI.Config;

public class ThresholdsTab
{
    private int selectedEncounterId = -1;
    private string selectedEncounterName = string.Empty;
    private int minimumKills = 1;
    private bool showNotification = true;
    private bool autoKick = false;

    public void Draw()
    {
        var settings = Service.Configuration.KillThresholds;
        var hasChanged = false;

        // Main toggle
        bool enableKillChecking = settings.EnableKillChecking;
        if (ImGui.Checkbox("Enable Kill Threshold Checking", ref enableKillChecking))
        {
            settings.EnableKillChecking = enableKillChecking;
            hasChanged = true;
        }

        if (enableKillChecking)
        {
            ImGui.Indent();

            bool checkOnPartyJoin = settings.CheckOnPartyJoin;
            if (ImGui.Checkbox("Check automatically when players join the party", ref checkOnPartyJoin))
            {
                settings.CheckOnPartyJoin = checkOnPartyJoin;
                hasChanged = true;
            }

            bool checkOnlyIfPartyLeader = settings.CheckOnlyIfPartyLeader;
            if (ImGui.Checkbox("Only check if you are the party leader", ref checkOnlyIfPartyLeader))
            {
                settings.CheckOnlyIfPartyLeader = checkOnlyIfPartyLeader;
                hasChanged = true;
            }

            Util.DrawHelp("This prevents notifications when you join other people's parties.");

            ImGui.Separator();
            ImGui.Text("Configured Thresholds:");

            // Display existing thresholds
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

                    for (int i = 0; i < settings.Thresholds.Count; i++)
                    {
                        var threshold = settings.Thresholds[i];

                        ImGui.TableNextRow();

                        ImGui.TableNextColumn();
                        ImGui.Text(threshold.EncounterName);

                        ImGui.TableNextColumn();
                        int kills = threshold.MinimumKills;
                        if (ImGui.InputInt($"##kills{i}", ref kills, 1, 5))
                        {
                            if (kills < 0) kills = 0;
                            threshold.MinimumKills = kills;
                            hasChanged = true;
                        }

                        ImGui.TableNextColumn();
                        bool notify = threshold.ShowNotification;
                        if (ImGui.Checkbox($"##notify{i}", ref notify))
                        {
                            threshold.ShowNotification = notify;
                            hasChanged = true;
                        }

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

                        // Warning popup for auto-kick
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

                        ImGui.TableNextColumn();
                        using (var font = ImRaii.PushFont(UiBuilder.IconFont))
                        {
                            if (ImGui.Button(FontAwesomeIcon.Trash.ToIconString()))
                            {
                                settings.Thresholds.RemoveAt(i);
                                hasChanged = true;
                            }
                        }
                    }

                    ImGui.EndTable();
                }
            }
            else
            {
                ImGui.TextColored(ImGuiColors.DalamudGrey, "No thresholds configured. Add one below.");
            }

            ImGui.Separator();
            ImGui.Text("Add New Threshold");

            // Select encounter
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

            // Minimum kills input
            ImGui.InputInt("Minimum Kills", ref minimumKills);
            if (minimumKills < 0) minimumKills = 0;

            // Options
            ImGui.Checkbox("Show Notification", ref showNotification);
            if (ImGui.Checkbox("Auto-Kick (Not Recommended)", ref autoKick))
            {
                if (autoKick)
                {
                    ImGui.OpenPopup("AddKickWarningPopup");
                }
            }

            // Warning popup for auto-kick
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

            // Add button
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

                // Reset form
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
