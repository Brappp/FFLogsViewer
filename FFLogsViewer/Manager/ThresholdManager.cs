using System;
using System.Threading.Tasks;
using Dalamud.Game.Gui.Toast;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Interface.ImGuiNotification;
using FFLogsViewer.Model;

namespace FFLogsViewer.Manager;

public class ThresholdManager
{
    private readonly TeamManager teamManager;
    private readonly FFLogsClient ffLogsClient;

    public ThresholdManager(TeamManager teamManager, FFLogsClient ffLogsClient)
    {
        this.teamManager = teamManager;
        this.ffLogsClient = ffLogsClient;
    }

    public async Task CheckPlayerKills(string firstName, string lastName, string worldName)
    {
        // Log that we're attempting to check a player
        Service.PluginLog.Information($"Checking kill thresholds for {firstName} {lastName}@{worldName}");

        if (!Service.Configuration.KillThresholds.EnableKillChecking)
        {
            Service.PluginLog.Information("Kill threshold checking is disabled");
            return;
        }

        // Check if we should only check when party leader
        if (Service.Configuration.KillThresholds.CheckOnlyIfPartyLeader)
        {
            bool isPartyLeader = await IsPartyLeader();
            if (!isPartyLeader)
            {
                Service.PluginLog.Information("Skipping check because user is not party leader");
                return;
            }
        }

        // Debug: Log the number of configured thresholds
        Service.PluginLog.Information($"Checking against {Service.Configuration.KillThresholds.Thresholds.Count} configured thresholds");

        // If no thresholds are configured, notify the user
        if (Service.Configuration.KillThresholds.Thresholds.Count == 0)
        {
            var notification = new Notification
            {
                Title = "FFLogs Kill Threshold",
                Content = "No kill thresholds are configured. Go to Settings > Kill Thresholds to set them up.",
                Type = NotificationType.Info
            };

            Service.NotificationManager.AddNotification(notification);
            return;
        }

        bool anyThresholdsFailed = false;

        foreach (var threshold in Service.Configuration.KillThresholds.Thresholds)
        {
            if (!threshold.IsEnabled)
                continue;

            Service.PluginLog.Information($"Checking {firstName} {lastName} against threshold for {threshold.EncounterName} (min kills: {threshold.MinimumKills})");

            try
            {
                var (_, kills) = await ffLogsClient.FetchEncounterParseAsync(
                    firstName, lastName, worldName, threshold.EncounterId);

                // Log the result
                Service.PluginLog.Information($"Result: {kills ?? 0} kills (threshold: {threshold.MinimumKills})");

                // If no kills data or kills below threshold
                int actualKills = kills ?? 0;
                if (actualKills < threshold.MinimumKills)
                {
                    anyThresholdsFailed = true;

                    if (threshold.ShowNotification)
                    {
                        ShowBelowThresholdNotification(firstName, lastName, worldName, threshold, actualKills);
                    }

                    if (threshold.AutoKick)
                    {
                        await KickPlayer(firstName, lastName);
                    }
                }
            }
            catch (Exception ex)
            {
                Service.PluginLog.Error(ex, $"Error checking kill threshold for {firstName} {lastName} on encounter {threshold.EncounterName}");

                // Show error notification
                var notification = new Notification
                {
                    Title = "FFLogs Kill Threshold Error",
                    Content = $"Error checking {firstName} {lastName}: {ex.Message}",
                    Type = NotificationType.Error
                };

                Service.NotificationManager.AddNotification(notification);
            }
        }

        // If all thresholds passed, optionally show a success notification
        if (!anyThresholdsFailed && Service.Configuration.KillThresholds.Thresholds.Count > 0)
        {
            Service.PluginLog.Information($"{firstName} {lastName} passed all kill thresholds");

            // Optional: Show success notification
            /*
            var successNotification = new Notification
            {
                Title = "FFLogs Kill Threshold",
                Content = $"{firstName} {lastName} meets all kill requirements",
                Type = NotificationType.Success
            };
            
            Service.NotificationManager.AddNotification(successNotification);
            */
        }
    }

    private async Task<bool> IsPartyLeader()
    {
        // This is simplified and could be improved with actual game state checking
        // In a production plugin, you'd want to check if the player is actually the party leader
        return true;
    }

    private void ShowBelowThresholdNotification(string firstName, string lastName, string worldName,
        KillThreshold threshold, int actualKills)
    {
        // Create in-game notification
        var notification = new Notification
        {
            Title = "FFLogs Kill Threshold Alert",
            Content = $"{firstName} {lastName}@{worldName} has only {actualKills} kills of {threshold.EncounterName} (minimum: {threshold.MinimumKills})",
            Type = NotificationType.Warning,
            Minimized = false
        };

        Service.NotificationManager.AddNotification(notification);

        // Log the notification
        Service.PluginLog.Warning($"Kill threshold not met: {firstName} {lastName}@{worldName} has only {actualKills} kills of {threshold.EncounterName} (minimum: {threshold.MinimumKills})");

        // Also show an in-game toast
        try
        {
            var toastMessage = new SeStringBuilder()
                .AddUiForeground(45) // Yellow
                .AddText($"[FFLogs] {firstName} {lastName} has only {actualKills}/{threshold.MinimumKills} kills of {threshold.EncounterName}")
                .AddUiForegroundOff()
                .Build();

            Service.ToastGui.ShowQuest(toastMessage);
        }
        catch (Exception ex)
        {
            Service.PluginLog.Error(ex, "Error showing toast notification");
        }
    }

    private async Task KickPlayer(string firstName, string lastName)
    {
        string fullName = $"{firstName} {lastName}";

        // Log that we're trying to kick a player
        Service.PluginLog.Information($"Attempting to kick player: {fullName}");

        // This could be implemented using chat commands
        // But for safety reasons, let's not implement the actual kicking code

        // Instead, just display a notification that we would kick them
        var notification = new Notification
        {
            Title = "FFLogs Auto-Kick",
            Content = $"Would kick {fullName} (actual kick disabled for safety)",
            Type = NotificationType.Info
        };

        Service.NotificationManager.AddNotification(notification);
    }

    public void OnPlayerJoinedParty(string firstName, string lastName, string worldName)
    {
        // Log that a player joined and we're checking them
        Service.PluginLog.Information($"Player joined party: {firstName} {lastName}@{worldName}");

        if (!Service.Configuration.KillThresholds.EnableKillChecking ||
            !Service.Configuration.KillThresholds.CheckOnPartyJoin)
        {
            Service.PluginLog.Information("Automatic checking on party join is disabled");
            return;
        }

        // Run the check asynchronously
        Task.Run(async () =>
        {
            await CheckPlayerKills(firstName, lastName, worldName);
        }).ContinueWith(t =>
        {
            if (t.IsFaulted && t.Exception != null)
            {
                Service.PluginLog.Error(t.Exception, "Error in threshold check");
            }
        });
    }
}
