
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.GamePad;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Memory;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using ECommons;
using ECommons.Automation;
using ECommons.Automation.UIInput;
using ECommons.DalamudServices;
using ECommons.Gamepad;
using ECommons.Logging;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Graphics;
using FFXIVClientStructs.FFXIV.Client.System.Input;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using static ECommons.UIHelpers.AddonMasterImplementations.AddonMaster;
using static FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.VertexShader;
using static FFXIVClientStructs.FFXIV.Component.GUI.AtkUIColorHolder.Delegates;
using Module = ECommons.Module;
namespace EasyStables;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    public string Name => "EasyStables";

    private const string CommandName = "/easystables";

    public Configuration Configuration { get; init; }

    public readonly WindowSystem WindowSystem = new("EasyStables");

    private bool isEnabled = true;

    /* only manages whether the inventory is open through clicking on a chocobo to not break manual inventory open while the stable is open */
    private bool stablesOpen = false;
    private int currentPageIdx = 0;

    /* whether a synthetic click was automatically fired by the plugin */
    private bool syntheticClickFired = false;
    private bool isStablesConditionGood = true;

    private long delayMs = 1000;

    private long timeToDoStuffInStables = 0;
    private long timeToDoStuffInInventory = 0;
    private long timeToDoStuffInContextMenu = 0;

    internal IAddonLifecycle lifeCycle { get; init; }
    [PluginService] internal IChatGui ChatGui { get; private set; }

    public Plugin(IDalamudPluginInterface dalamud, ICommandManager commmandManager, IPluginLog log, INotificationManager notificationManager, IAddonLifecycle addonLifecycle)
    {
        ECommonsMain.Init(dalamud, this, Module.All);
        lifeCycle = addonLifecycle;
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Enable / Disable EasyStables"
        });

        // Use /xllog to open the log window in-game

        resetTimers();

        addonLifecycle.RegisterListener(AddonEvent.PreOpen, "HousingChocoboList", HookStablesOpen);

        // addonLifecycle.RegisterListener(AddonEvent.PreClose, "HousingChocoboList", HookStablesPreClose);
        addonLifecycle.RegisterListener(AddonEvent.PostClose, "HousingChocoboList", HookStablesClose);

        addonLifecycle.RegisterListener(AddonEvent.PostDraw, "HousingChocoboList", SyncWithGameState);
        addonLifecycle.RegisterListener(AddonEvent.PostDraw, "InventoryGrid", SearchInventoryForFood);

        //addonLifecycle.RegisterListener(AddonEvent.PreReceiveEvent, "HousingChocoboList", PreEventHook);
        // addonLifecycle.RegisterListener(AddonEvent.PostReceiveEvent, "HousingChocoboList", PostEventHook);

        addonLifecycle.RegisterListener(AddonEvent.PostDraw, "SelectString", CheckIfStableIsClean);

        addonLifecycle.RegisterListener(AddonEvent.PostDraw, "ContextMenu", ContextMenuDraw);
    }
    public void Dispose()
    {
        isEnabled = false;
        ECommonsMain.Dispose();
        lifeCycle.UnregisterListener(AddonEvent.PreOpen, "HousingChocoboList", HookStablesOpen);
        lifeCycle.UnregisterListener(AddonEvent.PostClose, "HousingChocoboList", HookStablesClose);
        lifeCycle.UnregisterListener(AddonEvent.PostDraw, "HousingChocoboList", SyncWithGameState);
         
        lifeCycle.UnregisterListener(AddonEvent.PostDraw, "InventoryGrid", SearchInventoryForFood);

        lifeCycle.UnregisterListener(AddonEvent.PostDraw, "SelectString", CheckIfStableIsClean);

        lifeCycle.UnregisterListener(AddonEvent.PostDraw, "ContextMenu", ContextMenuDraw);

        CommandManager.RemoveHandler(CommandName);
    }

    private void resetTimers()
    {
        timeToDoStuffInStables = 0;
        timeToDoStuffInInventory = 0;
        timeToDoStuffInContextMenu = 0;
    }

    private unsafe void ContextMenuDraw(AddonEvent type, AddonArgs args)
    {
        if (timeToDoStuffInContextMenu == 0)
        {
            timeToDoStuffInContextMenu = Environment.TickCount64 + delayMs;
            return;
        }
        else if (Environment.TickCount64 < timeToDoStuffInContextMenu)
        {
            return;
        }

        var addonAddress = args.Addon.Address;

        var contextMenuAddon = (AddonContextMenu*)addonAddress;

        ECommons.Automation.Callback.Fire((AtkUnitBase*)contextMenuAddon, true, 0, 0, 0, 0, 0);
        this.resetTimers();
    }

    private unsafe void CheckIfStableIsClean(AddonEvent type, AddonArgs args)
    {
        var addonAddress = args.Addon.Address;

        var stablesAddon = (AddonSelectString*)addonAddress;
        var textNode = stablesAddon->GetNodeById(2);
        if (textNode == null)
        {
            Log.Error($"null textNode??");
            return;
        }
        var castedTextNode = textNode->GetAsAtkTextNode();
        if (textNode == null)
        {
            Log.Error($"null castedTextNode??");
            return;
        }

        var cString = castedTextNode->GetText();
        if (cString == null)
        {
            Log.Error($"null cString??");
            return;
        }
        var cSharpString = new String(cString);
        if (cSharpString.Contains("Stable Cleanliness") && cSharpString.Contains("Stable Cleanliness: Good") == false)
        {
            ByteColor red = new ByteColor();
            red.A = 255;
            red.R = 255;
            red.G = 0;
            red.B = 0;
            castedTextNode->TextColor = red;
            isStablesConditionGood = false;
        }
    }

    //private unsafe void HookStablesPreClose(AddonEvent type, AddonArgs args)
    //{
    //    if (justFedChocobo == true)
    //    {
    //        var addonAddress = args.Addon.Address;
    //        var stablesAddon = (AddonChocoboBreedTraining*)addonAddress;
    //        var stablesListBeforeFeedingBefore = stablesListIdxBeforeFeeding;
    //        stablesListIdxBeforeFeeding = getSelectedStablesListIdx(stablesAddon);
    //        Log.Information($"stablesListIdxBeforeFeeding before {stablesListBeforeFeedingBefore} -> {stablesListIdxBeforeFeeding}", stablesListIdxBeforeFeeding);
    //    }
    //}

    //private unsafe void PostEventHook(AddonEvent type, AddonArgs args)
    //{
    //    if (args is AddonReceiveEventArgs evt)
    //    {
    //        var atkEvent = (AtkEvent*)evt.AtkEvent;

    //        var addonAddress = args.Addon.Address;

    //        var stablesAddon = (AddonChocoboBreedTraining*)addonAddress;

    //        var stablesList = stablesAddon->GetNodeById(3);
    //        if (stablesList == null)
    //        {
    //            Log.Error($"stables list is null??");
    //            return;
    //        }

    //        var target = (AtkEventTarget*)stablesList;
    //        if (atkEvent->State.EventType == AtkEventType.ListItemClick && atkEvent->Target == stablesList && syntheticClickFired == false)
    //        {
    //            // the user is taking control over the stables list! don't override user behaviour:
    //        }
    //    }
    //}

    //private unsafe void LogGameEvent(AtkEvent* atkEvent)
    //{
    //    PluginLog.Information($"""
    //            Logging atkEvent: {((int)atkEvent).ToString("X")}
    //        """);

    //    PluginLog.Information($"""
    //            atkEvent->Node->NodeId: {(atkEvent->Node == null ? "-" : atkEvent->Node->NodeId)}
    //            atkEvent->Target: {((int)(atkEvent->Target)).ToString("X")},
    //            atkEvent->Listener: {((int)(atkEvent->Listener)).ToString("X")},
    //            atkEvent->Param: {atkEvent->Param}
    //            atkEvent->NextEvent: {((int)(atkEvent->NextEvent)).ToString("X")},
    //            atkEvent->StateEventType: {atkEvent->State.EventType}
    //            atkEvent->StateFlags: {atkEvent->State.StateFlags}
    //            """);
    //    if (atkEvent->NextEvent != null)
    //    {
    //        LogGameEvent(atkEvent->NextEvent);
    //    }
    //}

    //private unsafe void PreEventHook(AddonEvent type, AddonArgs args)
    //{
    //    if (args is AddonReceiveEventArgs evt)
    //    {
    //        var atkEvent = (AtkEvent*)evt.AtkEvent;

    //        var addonAddress = args.Addon.Address;

    //        var stablesAddon = (AddonChocoboBreedTraining*)addonAddress;

    //        var stablesList = stablesAddon->GetNodeById(3);
    //        if (stablesList == null)
    //        {
    //            Log.Error($"stables list is null??");
    //            return;
    //        }
    //        if (evt.AtkEventType != (byte)AtkEventType.ListItemClick)
    //        {
    //            return;
    //        }
    //        var target = (AtkEventTarget*)stablesList;
    //        var data = MemoryHelper.ReadRaw(evt.AtkEventData, 40);
    //        PluginLog.Information($"""
    //            atkEvent->Node->NodeId: {(atkEvent->Node == null ? "-" : atkEvent->Node->NodeId)}
    //            atkEvent->Target: {((int)(atkEvent->Target)).ToString("X")},
    //            atkEvent->Listener: {((int)(atkEvent->Listener)).ToString("X")},
    //            atkEvent->Param: {atkEvent->Param}
    //            atkEvent->NextEvent: {((int)(atkEvent->NextEvent)).ToString("X")},
    //            AtkEventType: {evt.AtkEventType}
    //            atkEvent->StateEventType: {atkEvent->State.EventType}
    //            atkEvent->StateFlags: {atkEvent->State.StateFlags}
    //            data: {data.ToHexString()}
    //            CursorTarget: {(stablesAddon->CursorTarget == null ? "-" : stablesAddon->CursorTarget->NodeId)}
    //            """);
    //        //LogGameEvent(atkEvent);
    //        PluginLog.Information($"""
    //            atkEventData->ListItemRenderer: {((int)(((AtkEventData.AtkListItemData*)evt.AtkEventData))->ListItemRenderer).ToString("X")}  
    //            atkEventData->ListItem: {((int)(((AtkEventData.AtkListItemData*)evt.AtkEventData))->ListItem).ToString("X")}
    //            atkEventData->SelectedIndex: {((int)(((AtkEventData.AtkListItemData*)evt.AtkEventData))->HoveredItemIndex3)}
    //            atkEventData->MouseButtonId: {((int)(((AtkEventData.AtkListItemData*)evt.AtkEventData))->MouseButtonId)}
    //            atkEventData->MouseModifier: {((int)(((AtkEventData.AtkListItemData*)evt.AtkEventData))->MouseModifier)}
    //            """);
    //    }
    //}

    public unsafe void syntheticStablesListClick(AddonChocoboBreedTraining* addon, int stablesListToSelect)
    {
        // Log.Information($"syntheticStablesListClick ${stablesListToSelect}", stablesListToSelect); 
        var stablesList = addon->GetNodeById(3);
        if (stablesList == null)
        {
            Log.Error($"stables list is null??");
            return;
        }

        var stablesListComponent = stablesList->GetAsAtkComponentList();
        if (stablesListComponent == null)
        {
            Log.Error($"stables list component is null??");
            return;
        }

        var target = (AtkEventTarget*)stablesList;
        var listener = (AtkEventListener*)addon;
        if (listener == null)
        {
            Log.Error($"listener is null??");
            return;
        }

        var eventType = AtkEventType.ListItemClick; // I thought this is AtkEventType.ButtonClick, but no
        var syntheticEvent = stackalloc AtkEvent[1]
        {
                new AtkEvent()
                {
                    Node = null, // OK
                    Target = target, // ListComponentNode OK
                    Listener = listener, // HousingChocoboList OK
                    Param = 2, // OK
                    NextEvent = null, // OK
                    State = new AtkEventState
                    {
                        EventType = eventType, // OK
                        StateFlags = AtkEventStateFlags.Unk3, // OK
                    }
                }
            };
        var atkEventData = new AtkEventData()
        {
            ListItemData = new AtkEventData.AtkListItemData()
            {
                ListItemRenderer = (AtkComponentListItemRenderer*)stablesListComponent->UldManager.NodeList[stablesListToSelect + 1]->GetAsAtkComponentListItemRenderer(), // replace with stablesListToSelect 
                ListItem = null,
                SelectedIndex = stablesListToSelect,
                //UnkListField15C = 0, // this is private for some reason, but it's 0 anyways
                HoveredItemIndex3 = (short)stablesListToSelect,
                MouseButtonId = 0,
                MouseModifier = AtkEventData.AtkMouseData.ModifierFlag.None
            }
        };
        var eventData = stackalloc AtkEventData[1] { atkEventData };
        syntheticClickFired = true;
        addon->ReceiveEvent(eventType, 2, syntheticEvent, eventData);
        setSelectedStablesListIdx(addon, stablesListToSelect); // need to set this manually; UI still does not update properly. but this works
        syntheticClickFired = false;

        this.resetTimers();
    }

    private void HookStablesClose(AddonEvent type, AddonArgs args)
    {
        // this is called in between feeding chocobos!
        stablesOpen = false;
    }

    private unsafe void HookStablesOpen(AddonEvent type, AddonArgs args)
    {
        // this is called in between feeding chocobos!
        stablesOpen = true;
        resetTimers();
    }

    private unsafe bool IsChocoboCapped(AtkTextNode* rank)
    {
        var color = rank->TextColor.RGBA;

        return color != 0xFFC5E1EE; // uncapped is 0xFF378EF0
    }

    private unsafe bool IsChocoboReady(AtkTextNode* training)
    {
        return training->GetText() == "Ready";
    }

    private unsafe int getSelectedStablesListIdx(AddonChocoboBreedTraining* addon)
    {
        var stablesList = addon->GetNodeById(3);
        if (stablesList == null)
        {
            Log.Information($"stables list is null??");
            return -1;
        }
        var component = stablesList->GetAsAtkComponentList();
        if (component == null)
        {
            Log.Information($"getSelectedStablesListIdx: cannot cast stables list??");
            return -1;
        }
        return component->SelectedItemIndex;
    }

    private unsafe void setSelectedStablesListIdx(AddonChocoboBreedTraining* addon, int selectedItemIndex)
    {
        var stablesList = addon->GetNodeById(3);
        if (stablesList == null)
        {
            Log.Information($"stables list is null??");
            return;
        }
        var component = stablesList->GetAsAtkComponentList();
        if (component == null)
        {
            Log.Information($"setSelectedStablesListIdx: cannot cast stables list??");
            return;
        }
        component->SelectedItemIndex = selectedItemIndex;
    }

    private unsafe void SearchInventoryForFood(AddonEvent type, AddonArgs args)
    {
        if (!isEnabled || !stablesOpen)
        {
            // don't break non-automated inventory open
            return;
        }

        if (timeToDoStuffInInventory == 0)
        {
            timeToDoStuffInInventory = Environment.TickCount64 + delayMs;
            return;
        }
        else if (Environment.TickCount64 < timeToDoStuffInInventory)
        {
            return;
        }

        // inspired by artisan: https://github.com/PunishXIV/Artisan/blob/9eb581257f96186c42b6652599c1ab40a501a3f0/Artisan/Tasks/TaskSelectRetainer.cs#L260
        var inventories = new List<InventoryType>
        {
            InventoryType.Inventory1,
            InventoryType.Inventory2,
            InventoryType.Inventory3,
            InventoryType.Inventory4
        };

        foreach (var inv in inventories)
        {
            var container = InventoryManager.Instance()->GetInventoryContainer(inv);
            if (container == null)
            {
                Log.Error($"null inventory??");
                continue;
            }
            for (int i = 0; i < container->Size; i++)
            {
                var item = container->GetInventorySlot(i);
                if (item == null)
                {
                    continue;
                }
                if (item->GetItemId() != 8165) // Krakka root
                {
                    continue;
                }

                var quantity = item->Quantity;
                var ag = AgentInventoryContext.Instance();
                // Open context menu -> see ContextMenuOpen
                ag->OpenForItemSlot(inv, i, 0, AgentModule.Instance()->GetAgentByInternalId(AgentId.Inventory)->GetAddonId());
                return;
            }
        }
    }

    unsafe void ClickChocobo(AddonChocoboBreedTraining* stablesAddon, int chocoboToFeed, int offsetInArray = 2)
    {
        var chocoboList = stablesAddon->GetNodeById(18);
        if (chocoboList == null)
        {
            Log.Error($"stables list is null??");
            return;
        }

        var chocoboListComponent = chocoboList->GetAsAtkComponentList();
        if (chocoboListComponent == null)
        {
            Log.Error($"chocoboListComponent component is null??");
            return;
        }

        var target = (AtkEventTarget*)chocoboList;
        var listener = (AtkEventListener*)stablesAddon;
        if (listener == null)
        {
            Log.Error($"listener is null??");
            return;
        }

        var syntheticListItemClick = stackalloc AtkEvent[1]
        {
            new AtkEvent()
            {
                Node = null, // OK
                Target = target, // ListComponentNode OK
                Listener = listener, // HousingChocoboList OK
                Param = 3, // OK
                NextEvent = null, // OK
                State = new AtkEventState
                {
                    EventType = AtkEventType.ListItemClick, // OK
                    StateFlags = AtkEventStateFlags.Unk3, // OK
                }
            }
        };
        var atkEventData = new AtkEventData()
        {
            ListItemData = new AtkEventData.AtkListItemData()
            {
                ListItemRenderer = (AtkComponentListItemRenderer*)chocoboListComponent->UldManager.NodeList[chocoboToFeed]->GetAsAtkComponentListItemRenderer(), 
                ListItem = null,
                SelectedIndex = chocoboToFeed - offsetInArray,
                //UnkListField15C = 0, // this is private for some reason, but it's 0 anyways
                HoveredItemIndex3 = (short)(chocoboToFeed - offsetInArray),
                MouseButtonId = 0,
                MouseModifier = AtkEventData.AtkMouseData.ModifierFlag.None
            }
        };
        var eventData = stackalloc AtkEventData[1] { atkEventData };

        syntheticClickFired = true;
        stablesAddon->ReceiveEvent(AtkEventType.ListItemClick, 3, syntheticListItemClick, eventData);
        syntheticClickFired = false;

        this.resetTimers();
    }

    private unsafe void SyncWithGameState(AddonEvent type, AddonArgs args)
    {
        if (timeToDoStuffInStables == 0)
        {
            timeToDoStuffInStables = Environment.TickCount64 + delayMs;
            return;
        } else if (Environment.TickCount64 < timeToDoStuffInStables)
        {
            return;
        }

        if (!args.Addon.IsVisible || !args.Addon.IsReady)
        {
            this.resetTimers();
            return;
        }

        if (!isEnabled)
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

        if (isStablesConditionGood == false)
        {

            ChatGui.PrintError("[EasyStables] Stables are not clean!");
            stablesAddon->Close(true);
            return;
        }

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

            if (chocoboNameTextNode->GetText() == "" && chocoboOwnerTextNode->GetText() == "")
            {
                // idk what this is, don't touch it
                continue;
            }

            bool isCapped = IsChocoboCapped(chocoboRankTextNode);
            bool isReady = IsChocoboReady(trainingTextNode);

            if (!isCapped && isReady) {
                // the chocobo is not capped and ready, let's click it to start training:
                Log.Information($"Want to feed Chocobo #{i} {chocoboNameTextNode->GetText()}@{chocoboOwnerTextNode->GetText()} capped: {isCapped} ready in: {trainingTextNode->GetText()}", i, chocoboNameTextNode->GetText(), chocoboOwnerTextNode->GetText(), trainingTextNode->GetText());

                // this should open the inventory, leading us to the other callback.
                ClickChocobo(stablesAddon, i);

                // delay next op:
                this.resetTimers();

                stablesOpen = true;
                // we need to check this page again at next sync:
                anyChocoboFed = true;
                return; // TODO: optimize this?
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
                this.resetTimers();
            } else if (nextPageIdx == stablesListComponent->ListLength)
            {
                Log.Information($"Nothing left to feed, closing window");
                // reset state before closing:
                setSelectedStablesListIdx(stablesAddon, 0);
                this.resetTimers();
                stablesAddon->Close(true);
            }
        } else
        {
            // continue feeding on this page through next sync
        }
    }

    private void OnCommand(string command, string args)
    {
        if (command.ToLower() != "/easystables") return;

        isEnabled = !isEnabled;
        ChatGui.Print(this.isEnabled ? "Easy Stables: Enabled" : "Easy Stables: Disabled");
    }
   
}
