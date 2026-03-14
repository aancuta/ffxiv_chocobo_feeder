
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
    private bool isInventoryOpenThroughStables = false;

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
        addonLifecycle.RegisterListener(AddonEvent.PostClose, "HousingChocoboList", HookStablesClose);

        addonLifecycle.RegisterListener(AddonEvent.PostDraw, "HousingChocoboList", SyncWithGameState);
        addonLifecycle.RegisterListener(AddonEvent.PostDraw, "InventoryGrid", SearchInventoryForFood);

        addonLifecycle.RegisterListener(AddonEvent.PreReceiveEvent, "HousingChocoboList", PreEventHook);
    }

    private unsafe void PreEventHook(AddonEvent type, AddonArgs args)
    {
        if (args is AddonReceiveEventArgs evt)
        {
            var addonAddress = args.Addon.Address;

            var addon = (AddonChocoboBreedTraining*)addonAddress;

            var atkEvent = (AtkEvent*)evt.AtkEvent;
            var data = MemoryHelper.ReadRaw(evt.AtkEventData, 40);

            PluginLog.Information($"""
                atkEvent->Node->NodeId: {(atkEvent->Node == null ? "-" : atkEvent->Node->NodeId)}
                atkEvent->Target: {((int)(atkEvent->Target)).ToString("X")},
                atkEvent->Listener: {((int)(atkEvent->Listener)).ToString("X")},
                atkEvent->Param: {atkEvent->Param}
                atkEvent->NextEvent: {((int)(atkEvent->NextEvent)).ToString("X")},
                AtkEventType: {evt.AtkEventType}
                atkEvent->StateEventType: {atkEvent->State.EventType}
                atkEvent->StateFlags: {atkEvent->State.StateFlags}
                data: {data.ToHexString()}
                CursorTarget: {(addon->CursorTarget == null ? "-" : addon->CursorTarget->NodeId)}
                """);

            PluginLog.Information($"""
                atkEventData->ListItemRenderer: {((int)(((AtkEventData.AtkListItemData*)evt.AtkEventData))->ListItemRenderer).ToString("X")}
                atkEventData->ListItem: {((int)(((AtkEventData.AtkListItemData*)evt.AtkEventData))->ListItem).ToString("X")}
                atkEventData->SelectedIndex: {((int)(((AtkEventData.AtkListItemData*)evt.AtkEventData))->HoveredItemIndex3)}
                atkEventData->MouseButtonId: {((int)(((AtkEventData.AtkListItemData*)evt.AtkEventData))->MouseButtonId)}
                atkEventData->MouseModifier: {((int)(((AtkEventData.AtkListItemData*)evt.AtkEventData))->MouseModifier)}
                """);
        }
    }

    public unsafe void syntheticStablesListClick(AddonChocoboBreedTraining* addon, int stablesListToSelect)
    {
        var stablesList = addon->GetNodeById(3);
        if (stablesList == null)
        {
            Log.Information("stables list is null??");
            return;
        }

        var stablesListComponent = stablesList->GetAsAtkComponentList();
        if (stablesListComponent == null)
        {
            Log.Information("stables list component is null??");
            return;
        }

        var target = (AtkEventTarget*)stablesList;
        var listener = (AtkEventListener*)addon;
        if (listener == null)
        {
            Log.Information("listener is null??");
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
                ListItemRenderer = (AtkComponentListItemRenderer*)stablesListComponent->UldManager.NodeList[2]->GetAsAtkComponentListItemRenderer(), // replace with stablesListToSelect 
                ListItem = null,
                SelectedIndex = 1,
                //UnkListField15C = 0, // this is private for some reason, set below   
                HoveredItemIndex3 = 1, // replace with stablesListToSelect
                MouseButtonId = 0,
                MouseModifier = AtkEventData.AtkMouseData.ModifierFlag.None
            }
        };
        var eventData = stackalloc AtkEventData[1]
        {
                atkEventData
            };
        //eventData->GetType() 
        //    .GetFields(BindingFlags.NonPublic) 
        //    .Where(info => info.Name == "UnkListField15C")
        //    .FirstOrDefault()
        //    .SetValue(atkEventData, 0);
        addon->ReceiveEvent(eventType, 2, syntheticEvent, eventData);

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
        isInventoryOpenThroughStables = false;
    }

    private void HookStablesOpen(AddonEvent type, AddonArgs args)
    {
        isInventoryOpenThroughStables = true;
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

    private unsafe void SearchInventoryForFood(AddonEvent type, AddonArgs args)
    {
        if (!isEnabled || !isInventoryOpenThroughStables)
        {
            // don't break non-automated inventory open
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

        //// Use the AtkEventData.AtkInputData type (expected by HandleButtonPress)
        //var inputDataDown = new AtkEventData.AtkMouseData()
        //{
        //    ButtonId = 0, // Left click
        //    Modifier = AtkEventData.AtkMouseData.ModifierFlag.None,
        //};
        //outerListListener->HandleMouseDown(&atkEvent, &inputDataDown);
        //var inputDataUp = new AtkEventData.AtkMouseData()
        //{
        //    ButtonId = 0, // Left click
        //    Modifier = AtkEventData.AtkMouseData.ModifierFlag.None,
        //};
        //outerListListener->HandleMouseDown(&atkEvent, &inputDataUp);
        //chocobo->HandleMouseUpEvent(&inputDataUp);

        var castedOwner = owner->GetAsAtkComponentButton();
        if (castedOwner == null)
        {
            return;
        }
        //castedOwner->ClickAddonButton((AtkUnitBase*)Svc.GameGui.GetAddonByName("Button", 1).Address);

        //var eventData = new AtkEvent();
        //var inputData = stackalloc int[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
        // castedOwner->ReceiveEvent(AtkEventType.ButtonClick, 0, &eventData); // this crashes and deletes the kraka roots

        // Log.Information($"Target: {((int)atkEvent.Target).ToString("X")} Listener: {((int)atkEvent.Listener).ToString("X")}");
    }

    bool fired = false;

    private unsafe void SyncWithGameState(AddonEvent type, AddonArgs args)
    {
        if (!args.Addon.IsVisible || !args.Addon.IsReady) return;

        if (!isEnabled)
        {
            return;
        }

        var addonAddress = args.Addon.Address;

        var window = (AddonChocoboBreedTraining*)addonAddress; // has ID 1 
        // the root has multiple children:
        // #22 Window Component Node [+11]
        // #5 Res Node [+9] <- this is the parent of the parent of the chocobo list
        // #3 Listcomponent node [+25] <- this contains the stables list
        var stablesList = window->GetNodeById(3);
        if (stablesList == null)
        {
            Log.Error($"null stables list??");
            return;
        }
        if (!fired)
        {
            Log.Information("fired stables click");
            syntheticStablesListClick(window, 1);
            fired = true;
        }

        var innerChocoboListRaw = window->GetNodeById(18);
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

        // Log.Information($"===resNode: {(int)resNode}===");
    }

    private void OnCommand(string command, string args)
    {
        if (command.ToLower() != "/easystables") return;

        isEnabled = !isEnabled;
        ChatGui.Print(this.isEnabled ? "Easy Stables: Enabled" : "Easy Stables: Disabled");
    }
   
}
