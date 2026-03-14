
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
    private bool isInventoryOpenThroughStables = false;

    /* whether a synthetic click was automatically fired by the plugin */
    private bool syntheticClickFired = false;
    // private bool isStablesConditionGood = true; // TODO

    /* whether the plugin controls the currently selected stables list (feed a chocobo on the second list -> first list opens when stables reopen; this corrects that) */
    private bool isSelectedStablesListControlledByPlugin = false;
    /* used in sync with isSelectedStablesListControlledByPlugin to remember the last selected list before feeding in order to restore it */
    private int stablesListIdxBeforeFeeding = 0;

    /*  */
    private bool justFedChocobo = false;

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

        addonLifecycle.RegisterListener(AddonEvent.PreOpen, "HousingChocoboList", HookStablesOpen);

        addonLifecycle.RegisterListener(AddonEvent.PreClose, "HousingChocoboList", HookStablesPreClose);
        addonLifecycle.RegisterListener(AddonEvent.PostClose, "HousingChocoboList", HookStablesClose);

        addonLifecycle.RegisterListener(AddonEvent.PostDraw, "HousingChocoboList", SyncWithGameState);
        addonLifecycle.RegisterListener(AddonEvent.PostDraw, "InventoryGrid", SearchInventoryForFood);

        addonLifecycle.RegisterListener(AddonEvent.PreReceiveEvent, "HousingChocoboList", PreEventHook);
        addonLifecycle.RegisterListener(AddonEvent.PostReceiveEvent, "HousingChocoboList", PostEventHook);
    }

    private unsafe void HookStablesPreClose(AddonEvent type, AddonArgs args)
    {
        if (justFedChocobo == true)
        {
            var addonAddress = args.Addon.Address;
            var stablesAddon = (AddonChocoboBreedTraining*)addonAddress;
            var stablesListBeforeFeedingBefore = stablesListIdxBeforeFeeding;
            stablesListIdxBeforeFeeding = getSelectedStablesListIdx(stablesAddon);
            Log.Information($"stablesListIdxBeforeFeeding before {stablesListBeforeFeedingBefore} -> {stablesListIdxBeforeFeeding}", stablesListIdxBeforeFeeding);
        }
    }

    private unsafe void PostEventHook(AddonEvent type, AddonArgs args)
    {
        if (args is AddonReceiveEventArgs evt)
        {
            var atkEvent = (AtkEvent*)evt.AtkEvent;

            var addonAddress = args.Addon.Address;

            var stablesAddon = (AddonChocoboBreedTraining*)addonAddress;

            var stablesList = stablesAddon->GetNodeById(3);
            if (stablesList == null)
            {
                Log.Error($"stables list is null??");
                return;
            }

            var target = (AtkEventTarget*)stablesList;
            if (atkEvent->State.EventType == AtkEventType.ListItemClick && atkEvent->Target == stablesList && syntheticClickFired == false)
            {
                // the user is taking control over the stables list! don't override user behaviour:
                Log.Information("isSelectedStablesListControlledByPlugin = false");
                isSelectedStablesListControlledByPlugin = false;
            }
        }
    }

    private unsafe void PreEventHook(AddonEvent type, AddonArgs args)
    {

        if (args is AddonReceiveEventArgs evt)
        {
            var atkEvent = (AtkEvent*)evt.AtkEvent;

            var addonAddress = args.Addon.Address;

            var stablesAddon = (AddonChocoboBreedTraining*)addonAddress;

            var stablesList = stablesAddon->GetNodeById(3);
            if (stablesList == null)
            {
                Log.Error($"stables list is null??");
                return;
            }

            //var target = (AtkEventTarget*)stablesList;

            //var data = MemoryHelper.ReadRaw(evt.AtkEventData, 40);

            //PluginLog.Information($"""
            //    atkEvent->Node->NodeId: {(atkEvent->Node == null ? "-" : atkEvent->Node->NodeId)}
            //    atkEvent->Target: {((int)(atkEvent->Target)).ToString("X")},
            //    atkEvent->Listener: {((int)(atkEvent->Listener)).ToString("X")},
            //    atkEvent->Param: {atkEvent->Param}
            //    atkEvent->NextEvent: {((int)(atkEvent->NextEvent)).ToString("X")},
            //    AtkEventType: {evt.AtkEventType}
            //    atkEvent->StateEventType: {atkEvent->State.EventType}
            //    atkEvent->StateFlags: {atkEvent->State.StateFlags}
            //    data: {data.ToHexString()}
            //    CursorTarget: {(stablesAddon->CursorTarget == null ? "-" : stablesAddon->CursorTarget->NodeId)}
            //    """);

            //PluginLog.Information($"""
            //    atkEventData->ListItemRenderer: {((int)(((AtkEventData.AtkListItemData*)evt.AtkEventData))->ListItemRenderer).ToString("X")}
            //    atkEventData->ListItem: {((int)(((AtkEventData.AtkListItemData*)evt.AtkEventData))->ListItem).ToString("X")}
            //    atkEventData->SelectedIndex: {((int)(((AtkEventData.AtkListItemData*)evt.AtkEventData))->HoveredItemIndex3)}
            //    atkEventData->MouseButtonId: {((int)(((AtkEventData.AtkListItemData*)evt.AtkEventData))->MouseButtonId)}
            //    atkEventData->MouseModifier: {((int)(((AtkEventData.AtkListItemData*)evt.AtkEventData))->MouseModifier)}
            //    """);
        }
    }

    public unsafe void syntheticStablesListClick(AddonChocoboBreedTraining* addon, int stablesListToSelect)
    {
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
        Log.Information($"isSelectedStablesListControlledByPlugin = true");
        isSelectedStablesListControlledByPlugin = true;
        addon->ReceiveEvent(eventType, 2, syntheticEvent, eventData);
        setSelectedStablesListIdx(addon, stablesListToSelect); // need to set this manually; UI still does not update properly. but this works
        syntheticClickFired = false;
    }

    public void Dispose()
    {
        isEnabled = false;
        ECommonsMain.Dispose();
        lifeCycle.UnregisterListener(AddonEvent.PreOpen, "HousingChocoboList", HookStablesOpen);
        lifeCycle.UnregisterListener(AddonEvent.PostClose, "HousingChocoboList", HookStablesClose);

        lifeCycle.UnregisterListener(AddonEvent.PostDraw, "HousingChocoboList", SyncWithGameState);
        lifeCycle.UnregisterListener(AddonEvent.PostDraw, "InventoryGrid", SearchInventoryForFood);

        CommandManager.RemoveHandler(CommandName);
    }

    private void HookStablesClose(AddonEvent type, AddonArgs args)
    {
        // this is called in between feeding chocobos!
        isInventoryOpenThroughStables = false;
        if (justFedChocobo == false)
        {
            isSelectedStablesListControlledByPlugin = false;
        }
    }

    private unsafe void HookStablesOpen(AddonEvent type, AddonArgs args)
    {
        // this is called in between feeding chocobos!
        isInventoryOpenThroughStables = true;
        if (justFedChocobo == false)
        {
            isSelectedStablesListControlledByPlugin = false;
        }
        justFedChocobo = false;
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
        if (!isEnabled || !isInventoryOpenThroughStables)
        {
            // don't break non-automated inventory open
            return;
        }

        var addonAddress = args.Addon.Address;

        var stablesAddon = (AddonChocoboBreedTraining*)addonAddress; // has ID 1
        if (stablesAddon == null)
        {
            Log.Information("null addon??");
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
                ag->OpenForItemSlot(inv, i, 0, AgentModule.Instance()->GetAgentByInternalId(AgentId.Inventory)->GetAddonId());
                var contextMenu = (AtkUnitBase*)Svc.GameGui.GetAddonByName("ContextMenu", 1).Address;
                Log.Information("justFedChocobo = true");
                justFedChocobo = true;
                isSelectedStablesListControlledByPlugin = true;
                ECommons.Automation.Callback.Fire(contextMenu, true, 0, 0, 0, 0, 0);
            }
        }
    }

    unsafe void ClickChocobo(AtkComponentListItemRenderer* chocobo, AtkComponentList* outerListListener, int idx)
    {
        var owner = chocobo->OwnerNode;

        var atkEvent = new AtkEvent()
        {
            Node = (AtkResNode*)outerListListener,
            Target = (AtkEventTarget*)owner,
            Listener = (AtkEventListener*)outerListListener,
            Param = 0,
            State = new AtkEventState
            {
                EventType = AtkEventType.ButtonClick,
                StateFlags = AtkEventStateFlags.None,
            }
        };

        var castedOwner = owner->GetAsAtkComponentButton();
        if (castedOwner == null)
        {
            return;
        }
        // TODO
    }

    private unsafe void SyncWithGameState(AddonEvent type, AddonArgs args)
    {
        if (!args.Addon.IsVisible || !args.Addon.IsReady) return;

        if (!isEnabled)
        {
            return;
        }

        var addonAddress = args.Addon.Address;

        var stablesAddon = (AddonChocoboBreedTraining*)addonAddress; // has ID 1 
        if (stablesAddon == null || !stablesAddon->IsFullyLoaded())
        {
            Log.Error("$null addon??");
            return;
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

        // only click if the list idx is different:
        var currentStablesListIdx = getSelectedStablesListIdx(stablesAddon);
        if (currentStablesListIdx < 0)
        {
            Log.Error($"cannot fetch selected stables index??");
            return;
        }

        if (isSelectedStablesListControlledByPlugin && currentStablesListIdx != stablesListIdxBeforeFeeding)
        {
            Log.Information($"currentStablesList: {currentStablesListIdx}", currentStablesListIdx);
            Log.Information($"stablesListIdxBeforeFeeding: {stablesListIdxBeforeFeeding}", stablesListIdxBeforeFeeding);
            syntheticStablesListClick(stablesAddon, stablesListIdxBeforeFeeding);
        }

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

        var allChocobos = innerChocoboListAsAtkComponentList->UldManager.NodeList;
        for (int i = 0; i < innerChocoboListAsAtkComponentList->UldManager.NodeListCount; ++i)
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
            if (!isCapped && isReady || chocoboNameTextNode->GetText() == "Varus") {
                //Log.Information($"Want to feed Chocobo {chocoboNameTextNode->GetText()}@{chocoboOwnerTextNode->GetText()} capped: {isCapped} ready in: {trainingTextNode->GetText()}", chocoboNameTextNode->GetText(), chocoboOwnerTextNode->GetText(), trainingTextNode->GetText());
                // the chocobo is not capped and ready, let's click it to start training

                // ClickChocobo(currentChocobo, innerChocoboListAsAtkComponentList, i);
                // this should open the inventory, leading us to the other callback.
                isInventoryOpenThroughStables = true;
                return;
            }
        }
    }

    private void OnCommand(string command, string args)
    {
        if (command.ToLower() != "/easystables") return;

        isEnabled = !isEnabled;
        ChatGui.Print(this.isEnabled ? "Easy Stables: Enabled" : "Easy Stables: Disabled");
    }
   
}
