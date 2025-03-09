using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFLogsViewer.API;
using FFLogsViewer.GUI.Config;
using FFLogsViewer.GUI.Main;
using FFLogsViewer.Manager;
using FFLogsViewer.Model;

namespace FFLogsViewer;

// ReSharper disable once UnusedType.Global
public sealed class FFLogsViewer : IDalamudPlugin
{
    private readonly WindowSystem windowSystem;
    private readonly FFLogsViewerProvider ffLogsViewerProvider;
    private HashSet<string> currentPartyMembers = new(); // Removed readonly to allow reassignment
    private bool isInitialPartyCheck = true;
    private readonly object partyLock = new();
    private DateTime lastCheckTime = DateTime.MinValue;
    private ThresholdCheckWindow thresholdCheckWindow;

    public FFLogsViewer(IDalamudPluginInterface pluginInterface)
    {
        pluginInterface.Create<Service>();

        Service.Configuration = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Service.Configuration.Initialize();

        Service.Commands = new Commands();
        Service.CharDataManager = new CharDataManager();
        Service.GameDataManager = new GameDataManager();
        Service.OpenWithManager = new OpenWithManager();
        Service.HistoryManager = new HistoryManager();
        Service.TeamManager = new TeamManager();
        Service.FFLogsClient = new FFLogsClient();

        Service.MainWindow = new MainWindow();
        Service.ConfigWindow = new ConfigWindow();

        this.windowSystem = new WindowSystem("FFLogsViewer");
        this.windowSystem.AddWindow(Service.ConfigWindow);
        this.windowSystem.AddWindow(Service.MainWindow);

        // Create the threshold check window and add it to the WindowSystem
        this.thresholdCheckWindow = new ThresholdCheckWindow();
        this.windowSystem.AddWindow(this.thresholdCheckWindow);

        // Create ThresholdManager and pass reference to the window
        Service.ThresholdManager = new ThresholdManager(Service.TeamManager, Service.FFLogsClient);

        // Set the reference to the ThresholdCheckWindow in the ThresholdManager
        if (Service.ThresholdManager is ThresholdManager manager)
        {
            manager.SetThresholdCheckWindow(this.thresholdCheckWindow);
        }

        ContextMenu.Enable();

        this.ffLogsViewerProvider = new FFLogsViewerProvider(pluginInterface, new FFLogsViewerAPI());

        Service.Interface.UiBuilder.OpenMainUi += OpenMainUi;
        Service.Interface.UiBuilder.OpenConfigUi += OpenConfigUi;

        // Fix for Draw delegate signature mismatch
        Service.Interface.UiBuilder.Draw += DrawUI;

        // Hook into Framework updates to detect party joins
        HookPartyJoinEvents();
    }

    // Create a wrapper method for Draw that matches the expected signature
    private void DrawUI()
    {
        this.windowSystem.Draw();
    }

    public void Dispose()
    {
        this.ffLogsViewerProvider.Dispose();
        Commands.Dispose();
        Service.OpenWithManager.Dispose();

        ContextMenu.Disable();

        // Unhook Framework update
        Service.Framework.Update -= OnFrameworkUpdate;

        Service.Interface.UiBuilder.OpenMainUi -= OpenMainUi;
        Service.Interface.UiBuilder.OpenConfigUi -= OpenConfigUi;
        Service.Interface.UiBuilder.Draw -= DrawUI; // Use the wrapper method here too

        this.windowSystem.RemoveAllWindows();
    }

    private static void OpenMainUi()
    {
        Service.MainWindow.IsOpen = true;
    }

    private static void OpenConfigUi()
    {
        Service.ConfigWindow.IsOpen = true;
    }

    private void HookPartyJoinEvents()
    {
        // Register for Framework updates to check party members periodically
        Service.Framework.Update += OnFrameworkUpdate;
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        // Skip if kill threshold checking is disabled
        if (!Service.Configuration.KillThresholds.EnableKillChecking ||
            !Service.Configuration.KillThresholds.CheckOnPartyJoin)
            return;

        // Only check periodically (every ~2 seconds)
        var currentTime = DateTime.Now;
        if ((currentTime - lastCheckTime).TotalMilliseconds < 2000)
            return;

        lastCheckTime = currentTime;

        // Skip if not playing
        if (Service.ClientState.LocalPlayer == null)
            return;

        // Update team list to get current party members
        Service.TeamManager.UpdateTeamList();

        // Skip if not in a party
        if (Service.TeamManager.TeamList.Count <= 1)
            return;

        lock (partyLock)
        {
            // Create a set of current party member identifiers
            var newPartyMembers = new HashSet<string>(
                Service.TeamManager.TeamList.Select(m => $"{m.FirstName}|{m.LastName}|{m.World}")
            );

            // Skip the initial check to avoid false positives
            if (isInitialPartyCheck)
            {
                Service.PluginLog.Debug("[KillThreshold] Initial party state recorded. Members: " + newPartyMembers.Count);
                isInitialPartyCheck = false;
                currentPartyMembers.Clear();
                currentPartyMembers.UnionWith(newPartyMembers);
                return;
            }

            // If party size changed, trigger a check
            if (currentPartyMembers.Count != newPartyMembers.Count)
            {
                Service.PluginLog.Debug($"[KillThreshold] Party size changed from {currentPartyMembers.Count} to {newPartyMembers.Count}");

                // If new members have joined
                if (newPartyMembers.Count > currentPartyMembers.Count)
                {
                    // Find new members (in new list but not in our tracking list)
                    var newMembers = newPartyMembers.Except(currentPartyMembers).ToList();

                    if (newMembers.Count > 0)
                    {
                        Service.PluginLog.Debug($"[KillThreshold] New members detected: {newMembers.Count}");
                        Service.ChatGui.Print($"[KillThreshold] New party members detected. Running kill threshold check.");

                        // Trigger a full party check - will show window and results
                        Service.ThresholdManager.ForceCheckKillThresholds();
                    }
                }
            }

            // Update our tracking set
            currentPartyMembers.Clear();
            currentPartyMembers.UnionWith(newPartyMembers);
        }
    }

    /// <summary>
    /// Command to manually check a party member against kill thresholds
    /// </summary>
    public void CheckPartyMember(string firstName, string lastName, string worldName)
    {
        Service.ThresholdManager.CheckPlayerKills(firstName, lastName, worldName);
    }

    /// <summary>
    /// Reset party tracking (useful after teleporting or changing instances)
    /// </summary>
    public void ResetPartyTracking()
    {
        lock (partyLock)
        {
            Service.PluginLog.Debug("[KillThreshold] Resetting party tracking state");
            isInitialPartyCheck = true;
            currentPartyMembers.Clear();

            // Force an update to set the new state
            Service.TeamManager.UpdateTeamList();
            if (Service.TeamManager.TeamList != null && Service.TeamManager.TeamList.Count > 0)
            {
                var members = Service.TeamManager.TeamList.Select(m => $"{m.FirstName}|{m.LastName}|{m.World}");
                foreach (var member in members)
                {
                    currentPartyMembers.Add(member);
                }
            }
        }
    }
}
