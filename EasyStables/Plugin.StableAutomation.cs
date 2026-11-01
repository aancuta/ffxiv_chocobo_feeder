using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace EasyStables;

public sealed partial class Plugin
{
    private unsafe int remainingTrainingTimeToMs(AtkTextNode* trainingTextNode)
    {
        Regex numberRegex = new Regex(@"\d+");
        var match = numberRegex.Match(trainingTextNode->GetText().ToString());
        if (match.Success)
        {
            var nextFeedTime = trainingTextNode->GetText().ToString(); // 42m for example
                                                                       // strip the "m" and convert to int
            var nextFeedTimeInt = int.Parse(match.Value);
            return (nextFeedTimeInt + 1) * 60 * 1000; // convert to milliseconds; add a minute
        }
        return -1;
    }

    private unsafe void SyncWithGameState(AddonEvent type, AddonArgs args)
    {
        if (!args.Addon.IsVisible || !args.Addon.IsReady)
        {
            this.resetTimers();
            return;
        }

        if (timerNotReadyOrNeedsStart(ref timeToDoStuffInStables, Configuration.userDelayMs))
        {
            return;
        }

        var addonAddress = args.Addon.Address;

        var stablesAddon = (AddonChocoboBreedTraining*)addonAddress; // has ID 1
        if (stablesAddon == null || !stablesAddon->IsFullyLoaded())
        {
            Log.Error("$null addon??");
            timeToDoStuffInStables = 0;
            return;
        }

        if (closeStablesAtNextTick == true)
        {
            stablesAddon->Close(true);
            closeStablesAtNextTick = false;
            return;
        }

        //if (isStablesConditionGood == false)
        //{

        //    ChatGui.PrintError("[EasyStables] Stables are not clean!");
        //    stablesAddon->Close(true);
        //    return;
        //}

        // only click if the list idx is different:
        var currentStablesListIdx = getSelectedStablesListIdx(stablesAddon);
        if (currentStablesListIdx != currentPageIdx) {
            syntheticStablesListClick(stablesAddon, currentPageIdx);
        }

        // the root has multiple children:
        // #22 Window Component Node [+11]
        // #5 Res Node [+9] <- this is the parent of the parent of the chocobo list
        // #3 Listcomponent node [+25] <- this contains the stables list
        var stablesList = stablesAddon->GetNodeById(3);
        if (stablesList == null)
        {
            Log.Error($"null stables list??");
            return;
        }
        var stablesListComponent = stablesList->GetAsAtkComponentList();

        var innerChocoboListRaw = stablesAddon->GetNodeById(18);
        if (innerChocoboListRaw == null)
        {
            Log.Error($"null inner chocobo list??");
            return;
        }

        var innerChocoboListAsAtkComponentList = innerChocoboListRaw->GetAsAtkComponentList();
        if (innerChocoboListAsAtkComponentList == null) {
            Log.Error($"cannot cast inner chocobo list??");
            return;
        }

        bool anyChocoboFed = false;

        var allChocobos = innerChocoboListAsAtkComponentList->UldManager.NodeList;
        for (int i = 2; i < innerChocoboListAsAtkComponentList->UldManager.NodeListCount; ++i)
        {
            // [#41015] ListItemRenderer Component Node [+20]
            var currentChocoboRaw = allChocobos[i];
            if (currentChocoboRaw == null)
            {
                Log.Error($"null chocobo??");
                break;
            }
            var currentChocobo = currentChocoboRaw->GetAsAtkComponentListItemRenderer();
            if (currentChocobo == null)
            {
                continue;
            }
            // [#2] Res Node [+8]
            var rowRaw = currentChocobo->GetNodeById(2);
            if (rowRaw == null)
            {
                Log.Error($"null row??");
                break;
            }

            // [#18] Text Node < whole row TextNode - ignore?
            // [#15] Res Node - time left until training ready / "Ready"
            // [#14] Base Component Node - dps
            // [#13] Base Component Node - heal
            // [#12] Base Component Node - tank
            // [#9] Res Node [+2] - rank < colored differently if capped!
            // [#6] Res Node [+2] - owner
            // [#3] Res Node [+2] - name
            var chocoboNameRaw = currentChocobo->GetNodeById(4);
            var chocoboOwnerRaw = currentChocobo->GetNodeById(7);
            var chocoboRankRaw = currentChocobo->GetNodeById(10);
            var trainingRaw = currentChocobo->GetNodeById(16);
            var wholeRowTextNode = currentChocobo->GetNodeById(18);
            if (chocoboNameRaw == null || chocoboOwnerRaw == null || chocoboRankRaw == null || trainingRaw == null)
            {
                Log.Error($"cannot split row??");
                break;
            }
            var chocoboNameTextNode = chocoboNameRaw->GetAsAtkTextNode();
            var chocoboOwnerTextNode = chocoboOwnerRaw->GetAsAtkTextNode();
            var chocoboRankTextNode = chocoboRankRaw->GetAsAtkTextNode();
            var trainingTextNode = trainingRaw->GetAsAtkTextNode();

            if (chocoboNameTextNode->GetText().ToString().Equals("") && chocoboOwnerTextNode->GetText().ToString().Equals(""))
            {
                // idk what this is, don't touch it
                continue;
            }

            string chocoboIdentifier = $"{chocoboNameTextNode->GetText()}@{chocoboOwnerTextNode->GetText()}";

            bool isCapped = IsChocoboCapped(chocoboRankTextNode);
            bool isReady = IsChocoboReady(trainingTextNode);
            if (!isReady)
            {
                var remainingTime = remainingTrainingTimeToMs(trainingTextNode);
                // C#'s Dictionary::Add is moronic and throws when the key is already present. Just override:
                chocoboTimeToFeed[chocoboIdentifier] = remainingTime;
            }
            chocoboRanks[chocoboIdentifier] = chocoboRankTextNode->GetText().ToString();

            if (isCapped)
            {
                // TODO: edge case - the chocobo can cap on a different rank. cappedChocobos and fedChocobos need to be converted to Dict<identifier -> rank> so we can check it here.
                bool newCap = cappedChocobos.Add(chocoboIdentifier); // don't send a message if we already sent it before
                bool didFeed = fedChocobos.Contains(chocoboIdentifier);
                if (newCap && didFeed) {
                    SendToBark("birbcapped", cappedChocoboString(chocoboIdentifier));
                }
            }

            if ((!isCapped && isReady)) {
                // the chocobo is not capped and ready, let's click it to start training:
                Log.Information($"Want to feed Chocobo #{i} {chocoboIdentifier} capped: {isCapped} ready in: {trainingTextNode->GetText()}", i, chocoboNameTextNode->GetText(), chocoboOwnerTextNode->GetText(), trainingTextNode->GetText());

                // this should open the inventory, leading us to the other callback.
                ClickChocobo(stablesAddon, i);

                fedChocobos.Add(chocoboIdentifier);

                // delay next op:
                this.resetTimers();

                // we need to check this page again at next sync:
                // TODO: optimize this?
                anyChocoboFed = true;
                return;
            }
        }

        if (currentStablesListIdx < 0)
        {
            Log.Error($"cannot fetch selected stables index??");
            return;
        }
        //Log.Information($"currentListIdx: {currentStablesListIdx}", currentStablesListIdx);
        //Log.Information($"ListLength: {stablesListComponent->ListLength}", stablesListComponent->ListLength);
        var nextPageIdx = currentStablesListIdx + 1;
        if (anyChocoboFed == false)
        {
            if (nextPageIdx < stablesListComponent->ListLength)
            {
                // try the next page through next sync
                currentPageIdx = nextPageIdx;
            } else if (nextPageIdx == stablesListComponent->ListLength)
            {
                Log.Information($"Nothing left to feed, closing window");
                // for some reason, the state does not reset when first opening the window next time, leading to a poisoned state.
                // simulate a click to go to the first page in a more "orthodox" manner; note: no, setSelectedStablesListIdx does not work.
                syntheticStablesListClick(stablesAddon, 0);
                closeStablesAtNextTick = true;

                var randomDelayMin = (Configuration.birdTimerDelayMin + Random.Shared.NextDouble() * (Configuration.birdTimerDelayMax - Configuration.birdTimerDelayMin));
                timeToDoStuffInStableCleanliness = Environment.TickCount64 + (long)(randomDelayMin * 1000 * 60);

                if (chocoboTimeToFeed.Count > 0)
                {
                    var msUntilNextFeed = chocoboTimeToFeed.Values.Max();
                    timeToDoStuffInStableCleanliness += msUntilNextFeed;
                    chocoboTimeToFeed.Clear();
                } else {
                    // if there are no chocobos to feed next, try again in one hour. timeToDoStuffInStableCleanliness was already incremented above.
                }
            }
            this.resetTimers();
        } else
        {
            // continue feeding on this page through next sync
        }
    }
}
