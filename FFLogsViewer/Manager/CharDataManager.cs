using System;
using System.Collections.Generic;
using System.Linq;
using FFLogsViewer.Model;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using Lumina.Excel.Sheets;

namespace FFLogsViewer.Manager
{
    public class CharDataManager
    {
        // Use a valid C# initialization for an empty list:
        public CharData DisplayedChar = new();
        public List<CharData> PartyMembers = new(); // Changed from [] to new() for valid C#

        public bool IsCurrPartyAnAlliance;
        public string[] ValidWorlds;

        private uint? currentAllianceIndex;

        /// <summary>
        /// Updates the PartyMembers list from TeamManager.
        /// </summary>
        /// <param name="forceLocalPlayerParty">If true, resets alliance index logic.</param>
        public void UpdatePartyMembers(bool forceLocalPlayerParty = true)
        {
            if (forceLocalPlayerParty)
            {
                this.currentAllianceIndex = null;
            }

            Service.TeamManager.UpdateTeamList();
            var localPLayer = Service.ClientState.LocalPlayer;
            var currPartyMembers = Service.TeamManager.TeamList
                .Where(teamMember => teamMember.AllianceIndex == this.currentAllianceIndex)
                .ToList();

            this.IsCurrPartyAnAlliance = this.currentAllianceIndex != null;

            // the alliance is empty, force local player party
            if (this.IsCurrPartyAnAlliance && currPartyMembers.Count == 0)
            {
                this.currentAllianceIndex = null;
                this.UpdatePartyMembers(true);
                return;
            }

            if (!Service.Configuration.Style.IsLocalPlayerInPartyView && !this.IsCurrPartyAnAlliance)
            {
                var index = currPartyMembers.FindIndex(member =>
                    $"{member.FirstName} {member.LastName}" == localPLayer?.Name.TextValue
                    && member.World == localPLayer.HomeWorld.ValueNullable?.Name);
                if (index >= 0)
                {
                    currPartyMembers.RemoveAt(index);
                }
            }

            // Add or update members
            foreach (var partyMember in currPartyMembers)
            {
                var member = this.PartyMembers.FirstOrDefault(x =>
                    x.FirstName == partyMember.FirstName &&
                    x.LastName == partyMember.LastName &&
                    x.WorldName == partyMember.World);

                if (member == null)
                {
                    // Add new member
                    this.PartyMembers.Add(new CharData(
                        partyMember.FirstName,
                        partyMember.LastName,
                        partyMember.World,
                        partyMember.JobId)
                    );
                }
                else
                {
                    // update existing member
                    member.JobId = partyMember.JobId;
                }
            }

            // remove members that are no longer in party
            this.PartyMembers.RemoveAll(x => !currPartyMembers.Any(y =>
                y.FirstName == x.FirstName &&
                y.LastName == x.LastName &&
                y.World == x.WorldName));

            // reorder PartyMembers to match the party order
            this.PartyMembers = this.PartyMembers.OrderBy(charData =>
                currPartyMembers.FindIndex(member =>
                    member.FirstName == charData.FirstName &&
                    member.LastName == charData.LastName &&
                    member.World == charData.WorldName)).ToList();

            this.FetchLogs();
        }

        /// <summary>
        /// This method is **new**. It looks up (or optionally creates) a <see cref="CharData"/>
        /// that matches the given firstName, lastName, and world. 
        /// </summary>
        /// <param name="firstName">Player's first name.</param>
        /// <param name="lastName">Player's last name.</param>
        /// <param name="world">Player's world name.</param>
        /// <returns>A matching <see cref="CharData"/> if found or created; otherwise null.</returns>
        public CharData? GetCharData(string firstName, string lastName, string world)
        {
            // Search existing party members first:
            var match = this.PartyMembers.FirstOrDefault(m =>
                m.FirstName.Equals(firstName, StringComparison.OrdinalIgnoreCase)
                && m.LastName.Equals(lastName, StringComparison.OrdinalIgnoreCase)
                && m.WorldName.Equals(world, StringComparison.OrdinalIgnoreCase));

            if (match != null)
            {
                // Already in our PartyMembers list
                return match;
            }

            // If not in party, you can decide whether to:
            //   - Return null (e.g., threshold checking only for known party)
            //   - Or create a new CharData for out-of-party checks

            // For demonstration, let's create a new CharData and fetch logs:
            var newChar = new CharData(firstName, lastName, world, 0);

            // Optionally fetch logs now or lazily later:
            newChar.FetchLogs();

            // Optionally add to PartyMembers if you want to keep track of them
            // even if they weren't in the immediate party list.
            this.PartyMembers.Add(newChar);

            return newChar;
        }

        public CharDataManager()
        {
            var worlds = Service.DataManager.GetExcelSheet<World>().Where(Util.IsWorldValid);
            if (worlds == null)
            {
                throw new InvalidOperationException("Sheets weren't ready.");
            }

            this.ValidWorlds = worlds.Select(world => world.Name.ToString()).ToArray();
        }

        public static string GetRegionCode(string worldName)
        {
            var worldSheet = Service.DataManager.GetExcelSheet<World>();

            if (!Util.TryGetFirst(
                    worldSheet,
                    x => x.Name.ToString().Equals(worldName, StringComparison.InvariantCultureIgnoreCase),
                    out var world)
                || !Util.IsWorldValid(world))
            {
                return string.Empty;
            }

            return Util.GetRegionCode(world);
        }

        public static unsafe string? FindPlaceholder(string text)
        {
            try
            {
                var placeholder = Framework.Instance()->GetUIModule()->GetPronounModule()->ResolvePlaceholder(text, 0, 0);
                if (placeholder != null && placeholder->IsCharacter())
                {
                    var character = (Character*)placeholder;
                    var world = Util.GetWorld(character->HomeWorld);
                    if (Util.IsWorldValid(world) && placeholder->Name != null)
                    {
                        var name = $"{placeholder->NameString}@{world.Name}";
                        return name;
                    }
                }
            }
            catch (Exception ex)
            {
                Service.PluginLog.Error(ex, "Error while resolving placeholder.");
                return null;
            }

            return null;
        }

        public static void OpenCharInBrowser(string name)
        {
            var charData = new CharData();
            if (charData.ParseTextForChar(name))
            {
                Util.OpenFFLogsLink(charData);
            }
        }

        /// <summary>
        /// Fetch logs for all relevant characters—either the PartyMembers or the single DisplayedChar.
        /// </summary>
        /// <param name="ignoreErrors">If true, fetch even if some errors were previously encountered.</param>
        public void FetchLogs(bool ignoreErrors = false)
        {
            if (Service.MainWindow.IsPartyView)
            {
                foreach (var partyMember in this.PartyMembers)
                {
                    if (partyMember.IsInfoSet() && (ignoreErrors
                                                    || partyMember.CharError == null
                                                    || (partyMember.CharError != CharacterError.CharacterNotFoundFFLogs
                                                        && partyMember.CharError != CharacterError.HiddenLogs)))
                    {
                        partyMember.FetchLogs();
                    }
                }
            }
            else
            {
                if (this.DisplayedChar.IsInfoSet())
                {
                    this.DisplayedChar.FetchLogs();
                }
            }
        }

        public void Reset()
        {
            if (Service.MainWindow.IsPartyView)
            {
                this.PartyMembers.Clear();
                this.currentAllianceIndex = null;
                this.IsCurrPartyAnAlliance = false;
            }
            else
            {
                this.DisplayedChar = new CharData();
            }
        }

        public void SwapAlliance()
        {
            this.currentAllianceIndex = Service.TeamManager.GetNextAllianceIndex(this.currentAllianceIndex);
            this.UpdatePartyMembers(false);
        }
    }
}
