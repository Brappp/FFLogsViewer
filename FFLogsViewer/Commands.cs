using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Reflection;
using Dalamud.Game.Command;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;

namespace FFLogsViewer
{
    public class Commands
    {
        // Define command strings used to identify the different commands.
        private const string CommandName = "/fflogs";
        private const string SettingsCommandName = "/fflogsconfig";
        private const string ParseCommandName = "/ffparse";
        private const string ThresholdCheckCommandName = "/ffthreshold";

        // Constructor registers all commands with the command manager.
        public Commands()
        {
            Service.CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
            {
                HelpMessage = "Toggle the main window.\n" +
                              "         If given \"party\" or \"p\" as argument, open the party view and refresh the party state.\n" +
                              "         If given anything else, open the single view and search for a character name.\n" +
                              "         Support all player character placeholders (<t>, <1>, <mo>, etc.).",
                ShowInHelp = true,
            });

            Service.CommandManager.AddHandler(SettingsCommandName, new CommandInfo(OnCommand)
            {
                HelpMessage = "Toggle the config window.",
                ShowInHelp = true,
            });

            Service.CommandManager.AddHandler(ParseCommandName, new CommandInfo(OnCommand)
            {
                HelpMessage = "Get a player's best parse and kill count for a specific encounter.\n" +
                              "Usage: /ffparse [name] [world] [encounterId] <metric>\n" +
                              "Example: /ffparse \"Player Name\" Gilgamesh 87 rdps",
                ShowInHelp = true,
            });

            Service.CommandManager.AddHandler(ThresholdCheckCommandName, new CommandInfo(OnCommand)
            {
                HelpMessage = "Check kill thresholds for party members.\n" +
                              "Usage: /ffthreshold - Opens the threshold check window\n" +
                              "       /ffthreshold window - Opens the threshold check window\n" +
                              "       /ffthreshold check \"Name\" \"World\" - Check specific player\n" +
                              "       /ffthreshold target - Check current target\n" +
                              "       /ffthreshold party - Check all party members\n" +
                              "       /ffthreshold debug - Show debug info",
                ShowInHelp = true,
            });
        }

        // Unregisters all command handlers.
        public static void Dispose()
        {
            Service.CommandManager.RemoveHandler(CommandName);
            Service.CommandManager.RemoveHandler(SettingsCommandName);
            Service.CommandManager.RemoveHandler(ParseCommandName);
            Service.CommandManager.RemoveHandler(ThresholdCheckCommandName);
        }

        // Main command handler that routes commands based on the input command string.
        private static void OnCommand(string command, string args)
        {
            // Remove any leading or trailing whitespace from the arguments.
            var trimmedArgs = args.Trim();
            switch (command)
            {
                case CommandName when trimmedArgs.Equals("config", StringComparison.OrdinalIgnoreCase):
                case SettingsCommandName:
                    Service.ConfigWindow.Toggle();
                    break;
                case CommandName when string.IsNullOrEmpty(trimmedArgs):
                    Service.MainWindow.Toggle();
                    break;
                case CommandName:
                    Service.MainWindow.Open();
                    if (trimmedArgs.Equals("p", StringComparison.OrdinalIgnoreCase)
                        || trimmedArgs.Equals("party", StringComparison.OrdinalIgnoreCase))
                    {
                        Service.MainWindow.IsPartyView = true;
                        Service.CharDataManager.UpdatePartyMembers();
                        break;
                    }
                    Service.CharDataManager.DisplayedChar.FetchCharacter(trimmedArgs);
                    break;
                case ParseCommandName:
                    HandleParseCommand(args);
                    break;
                case ThresholdCheckCommandName:
                    HandleThresholdCommand(args);
                    break;
            }
        }

        // Handles the /ffparse command by validating and parsing the input arguments,
        // then fetching and displaying parse data.
        private static void HandleParseCommand(string args)
        {
            // Validate that the arguments string is not null, empty, or only whitespace.
            if (string.IsNullOrWhiteSpace(args))
            {
                Service.ChatGui.PrintError("Please provide player name, world, and encounter ID.");
                Service.ChatGui.PrintError("Example: /ffparse \"Player Name\" Gilgamesh 87 rdps");
                return;
            }

            // Split the arguments by quotes to extract the player name.
            var argParts = args.Split('"');
            if (argParts.Length < 3)
            {
                Service.ChatGui.PrintError("Name must be in quotes. Example: /ffparse \"Player Name\" Gilgamesh 87");
                return;
            }

            // The second element should be the player's name.
            string playerName = argParts[1].Trim();
            // The remaining arguments are processed after the quoted name.
            var remainingArgs = argParts[2].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);

            // Ensure that there are at least two additional arguments for world and encounter ID.
            if (remainingArgs.Length < 2)
            {
                Service.ChatGui.PrintError("Missing world or encounter ID. Example: /ffparse \"Player Name\" Gilgamesh 87");
                return;
            }

            string world = remainingArgs[0];

            // Validate that the encounter ID is a valid number.
            if (!int.TryParse(remainingArgs[1], out int encounterId))
            {
                Service.ChatGui.PrintError("Encounter ID must be a number.");
                return;
            }

            // Use the provided metric or default to "rdps" if not specified.
            string metric = remainingArgs.Length >= 3 ? remainingArgs[2] : "rdps";

            // Split the player name into first and last name.
            var nameParts = playerName.Split(' ');
            if (nameParts.Length < 2)
            {
                Service.ChatGui.PrintError("Player name must include first and last name.");
                return;
            }
            string firstName = nameParts[0];
            // Use LINQ's Skip method to join the remaining parts as the last name.
            string lastName = string.Join(" ", nameParts.Skip(1));

            Service.ChatGui.Print("Fetching parse data from FFLogs...");

            // Run the fetch operation asynchronously.
            Task.Run(async () =>
            {
                // Fetch the best parse and kill count for the given player and encounter.
                var (bestParse, kills) = await Service.FFLogsClient.FetchEncounterParseAsync(
                    firstName, lastName, world, encounterId, metric: metric);

                // If no data was retrieved, print an error message.
                if (bestParse == null && kills == null)
                {
                    Service.ChatGui.PrintError("Failed to retrieve parse data. Check the character name, world, and encounter ID.");
                    return;
                }

                // Build the message to display to the user.
                var message = new SeStringBuilder()
                    .AddText(playerName)
                    .AddText($" @ {world} - Encounter #{encounterId} ({metric})")
                    .AddText("\n")
                    .AddText("Best Parse: ");

                // If a best parse exists, format it with an appropriate UI color.
                if (bestParse.HasValue)
                {
                    // Convert parse color to a ushort for the UI foreground method.
                    message.AddUiForeground((ushort)GetParseColor(bestParse.Value))
                           .AddText($"{bestParse.Value:F1}%")
                           .AddUiForegroundOff();
                }
                else
                {
                    message.AddText("No parse available");
                }

                // Append the kill count to the message.
                message.AddText(" | Kills: ")
                       .AddText(kills.HasValue ? kills.Value.ToString() : "0");

                // Print the built message to the chat.
                Service.ChatGui.Print(message.Build());
            });
        }

        // Handles the /ffthreshold command, supporting various subcommands.
        private static void HandleThresholdCommand(string args)
        {
            // Split the command arguments by whitespace.
            var argParts = args.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);

            // If no subcommand is provided, open the window by default
            if (argParts.Length == 0)
            {
                Service.ThresholdManager.OpenThresholdCheckWindow(true);
                return;
            }

            // Even if threshold checking is disabled, still allow opening the window
            if (!Service.Configuration.KillThresholds.EnableKillChecking &&
                !(argParts.Length > 0 && (argParts[0].ToLower() == "ui" || argParts[0].ToLower() == "window" || argParts[0].ToLower() == "gui")))
            {
                Service.ChatGui.PrintError("Kill threshold checking is disabled. Enable it in the FFLogs Viewer settings (Kill Thresholds tab).");

                // Offer to open settings
                Service.ChatGui.Print("[KillThreshold] Type '/ffthreshold window' to open the window anyway, or '/fflogsconfig' to open settings.");
                return;
            }

            // Process the subcommand.
            switch (argParts[0].ToLower())
            {
                case "window":
                case "gui":
                case "ui":
                    // Open the threshold check window
                    Service.ThresholdManager.OpenThresholdCheckWindow(true);
                    break;

                case "check":
                    if (argParts.Length < 3)
                    {
                        Service.ChatGui.PrintError("Usage: /ffthreshold check \"First Last\" World");
                        return;
                    }

                    string playerName = argParts[1];
                    string world = argParts[2];

                    if (playerName.StartsWith("\"") && !playerName.EndsWith("\""))
                    {
                        int nameEndIndex = -1;
                        for (int i = 2; i < argParts.Length; i++)
                        {
                            if (argParts[i].EndsWith("\""))
                            {
                                nameEndIndex = i;
                                break;
                            }
                        }
                        if (nameEndIndex == -1)
                        {
                            Service.ChatGui.PrintError("Player name must be in quotes.");
                            return;
                        }
                        StringBuilder sb = new StringBuilder();
                        for (int i = 1; i <= nameEndIndex; i++)
                        {
                            if (i > 1) sb.Append(' ');
                            sb.Append(argParts[i]);
                        }
                        playerName = sb.ToString().Trim('"');
                        if (nameEndIndex + 1 < argParts.Length)
                        {
                            world = argParts[nameEndIndex + 1];
                        }
                        else
                        {
                            Service.ChatGui.PrintError("Missing world name.");
                            return;
                        }
                    }

                    var nameParts = playerName.Split(' ', 2);
                    if (nameParts.Length < 2)
                    {
                        Service.ChatGui.PrintError("Player name must include first and last name.");
                        return;
                    }

                    Service.ChatGui.Print($"Checking thresholds for {playerName}@{world}...");
                    // Also open the window for this check
                    Service.ThresholdManager.OpenThresholdCheckWindow(false);
                    Service.ThresholdManager.CheckPlayerKills(nameParts[0], nameParts[1], world);
                    break;

                case "target":
                    var target = Service.TargetManager.Target;
                    if (target == null)
                    {
                        Service.ChatGui.PrintError("No target selected.");
                        return;
                    }

                    var nameProperty = target.GetType().GetProperty("Name", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (nameProperty == null)
                    {
                        Service.ChatGui.PrintError("Unable to access target's name property.");
                        return;
                    }
                    object nameValue = nameProperty.GetValue(target);
                    string targetName = nameValue?.ToString() ?? "";
                    if (string.IsNullOrEmpty(targetName))
                    {
                        Service.ChatGui.PrintError("Target name is empty.");
                        return;
                    }
                    var playerNameParts = targetName.Split(' ');
                    if (playerNameParts.Length < 2)
                    {
                        Service.ChatGui.PrintError("Target player's name does not have both first and last names.");
                        return;
                    }
                    string targetFirstName = playerNameParts[0];
                    string targetLastName = playerNameParts[1];

                    var homeWorldProperty = target.GetType().GetProperty("HomeWorld", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (homeWorldProperty == null)
                    {
                        Service.ChatGui.PrintError("Unable to access target's home world property.");
                        return;
                    }
                    object homeWorldValue = homeWorldProperty.GetValue(target);
                    string targetWorld = homeWorldValue?.ToString() ?? "";
                    Service.ChatGui.Print($"Checking thresholds for {targetFirstName} {targetLastName}@{targetWorld}...");

                    // Also open the window for this check
                    Service.ThresholdManager.OpenThresholdCheckWindow(false);
                    Service.ThresholdManager.CheckPlayerKills(targetFirstName, targetLastName, targetWorld);
                    break;

                case "party":
                    Service.ChatGui.Print("Checking thresholds for all party members...");

                    // Open the window and check thresholds
                    Service.ThresholdManager.OpenThresholdCheckWindow(true);
                    break;

                case "debug":
                    Service.ChatGui.Print("------ Kill Threshold Debug Info ------");
                    Service.ChatGui.Print($"Feature enabled: {Service.Configuration.KillThresholds.EnableKillChecking}");
                    Service.ChatGui.Print($"Check on party join: {Service.Configuration.KillThresholds.CheckOnPartyJoin}");
                    Service.ChatGui.Print($"Check only if party leader: {Service.Configuration.KillThresholds.CheckOnlyIfPartyLeader}");
                    Service.ChatGui.Print($"Number of configured thresholds: {Service.Configuration.KillThresholds.Thresholds.Count}");
                    foreach (var threshold in Service.Configuration.KillThresholds.Thresholds)
                    {
                        Service.ChatGui.Print($"  - {threshold.EncounterName}: Min kills {threshold.MinimumKills}, Notify: {threshold.ShowNotification}, AutoKick: {threshold.AutoKick}");
                    }
                    Service.ChatGui.Print("------ Current Party Info ------");
                    Service.TeamManager.UpdateTeamList();
                    if (Service.TeamManager.TeamList.Count == 0)
                    {
                        Service.ChatGui.Print("Not in a party");
                    }
                    else
                    {
                        Service.ChatGui.Print($"Party size: {Service.TeamManager.TeamList.Count}");
                        foreach (var member in Service.TeamManager.TeamList)
                        {
                            Service.ChatGui.Print($"  - {member.FirstName} {member.LastName}@{member.World}");
                        }
                    }
                    break;

                default:
                    Service.ChatGui.PrintError("Unknown subcommand. Use: window, check, target, party, or debug");
                    break;
            }
        }

        private static uint GetParseColor(float parse)
        {
            return parse switch
            {
                >= 100 => 0xE6CC80,    // Gold
                >= 99 => 0xE268A8,    // Pink
                >= 95 => 0xFF8000,    // Orange
                >= 75 => 0xA335EE,    // Purple
                >= 50 => 0x0070DD,    // Blue
                >= 25 => 0x1EFF00,    // Green
                _ => 0x666666     // Gray
            };
        }
    }
}
