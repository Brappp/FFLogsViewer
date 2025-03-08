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
    private readonly HashSet<string> currentPartyMembers = new();
    private bool isInitialPartyCheck = true;
    private readonly object partyLock = new();
    private DateTime lastCheckTime = DateTime.MinValue;

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
        Service.ThresholdManager = new ThresholdManager(Service.TeamManager, Service.FFLogsClient);

        Service.MainWindow = new MainWindow();
        Service.ConfigWindow = new ConfigWindow();
        this.windowSystem = new WindowSystem("FFLogsViewer");
        this.windowSystem.AddWindow(Service.ConfigWindow);
        this.windowSystem.AddWindow(Service.MainWindow);

        ContextMenu.Enable();

        this.ffLogsViewerProvider = new FFLogsViewerProvider(pluginInterface, new FFLogsViewerAPI());

        Service.Interface.UiBuilder.OpenMainUi += OpenMainUi;
        Service.Interface.UiBuilder.OpenConfigUi += OpenConfigUi;
        Service.Interface.UiBuilder.Draw += this.windowSystem.Draw;

        // Hook into Framework updates to detect party joins
        HookPartyJoinEvents();
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
        Service.Interface.UiBuilder.Draw -= this.windowSystem.Draw;
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
        if (!Service.Configuration.KillThresholds.EnableKillChecking)
            return;

        // Only check periodically (every ~2 seconds)
        var currentTime = DateTime.Now;
        if ((currentTime - lastCheckTime).TotalMilliseconds < 2000)
            return;

        lastCheckTime = currentTime;

        // Skip if not in party or not in an instance
        if (Service.ClientState.LocalPlayer == null)
            return;

        // Check if we're in an instance (not in overworld)
        bool isInInstance = Service.Condition[ConditionFlag.BoundByDuty] ||
                            Service.Condition[ConditionFlag.BetweenAreas] ||
                            Service.Condition[ConditionFlag.InCombat];

        if (!isInInstance)
            return;

        // Update team list to get current party members
        Service.TeamManager.UpdateTeamList();

        lock (partyLock)
        {
            // Create a set of current party member identifiers
            var newPartyMembers = new HashSet<string>(
                Service.TeamManager.TeamList.Select(m => $"{m.FirstName}|{m.LastName}|{m.World}")
            );

            // Skip the initial check to avoid false positives
            if (isInitialPartyCheck)
            {
                isInitialPartyCheck = false;
                currentPartyMembers.Clear();
                currentPartyMembers.UnionWith(newPartyMembers);
                return;
            }

            // Find new members (in new list but not in our tracking list)
            var newMembers = newPartyMembers.Except(currentPartyMembers).ToList();

            // Process new members
            foreach (var newMember in newMembers)
            {
                var parts = newMember.Split('|');
                if (parts.Length == 3)
                {
                    // Trigger threshold check for new party member
                    Service.ThresholdManager.OnPlayerJoinedParty(parts[0], parts[1], parts[2]);
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
            isInitialPartyCheck = true;
            currentPartyMembers.Clear();
        }
    }
}
