
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Chat;
using Dalamud.Game.ClientState.GamePad;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Command;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Memory;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using ECommons;
using ECommons.DalamudServices;
using ECommons.DalamudServices.Legacy;
using ECommons.EzEventManager;
using ECommons.GameHelpers;
using ECommons.SimpleGui;
using ECommons.Throttlers;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Graphics;
using FFXIVClientStructs.FFXIV.Client.System.Input;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Common.Math;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using static ECommons.UIHelpers.AddonMasterImplementations.AddonMaster;
using static FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.VertexShader;
using static FFXIVClientStructs.FFXIV.Component.GUI.AtkUIColorHolder.Delegates;
using SeStringBuilder = Lumina.Text.SeStringBuilder;

namespace EasyStables;

public sealed partial class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    public string Name => "EasyStables";

    private const string MainCommandName = "/easystables";

    public Configuration Configuration { get; init; }

    public readonly WindowSystem WindowSystem = new("EasyStables");
    private SamplePlugin.Windows.MainWindow MainWindow { get; init; }

    private bool isEnabled = false;

    /* only manages whether the inventory is open through clicking on a chocobo to not break manual inventory open while the stable is open */
    private bool stablesOpen = false;
    private int currentPageIdx = 0;

    /* whether a synthetic click was automatically fired by the plugin */
    private bool closeStablesAtNextTick = false;

    private long timeToDoStuffInStables = 0;
    private long timeToDoStuffInInventory = 0;
    private long timeToDoStuffInStableCleanliness = 0;
    private long timeToDoStuffInFrameworkUpdate = 0;

    private HashSet<string> fedChocobos = new HashSet<string>();
    private HashSet<string> cappedChocobos = new HashSet<string>();

    private Dictionary<string, string> chocoboRanks = new Dictionary<string, string>();

    private Dictionary<string, long> chocoboTimeToFeed = new Dictionary<string, long>();

    private static IDtrBarEntry _dtrEntry;

    private void resetStateToInitial()
    {
        stablesOpen = false;
        currentPageIdx = 0;
        closeStablesAtNextTick = false;

        resetTimers();
    }
    private void resetTimers()
    {
        timeToDoStuffInStables = 0;
        timeToDoStuffInInventory = 0;
    }

    internal IAddonLifecycle lifeCycle { get; init; }
    [PluginService] internal IChatGui ChatGui { get; private set; } 

    public Plugin(IDalamudPluginInterface dalamud, ICommandManager commmandManager, IPluginLog log, INotificationManager notificationManager, IAddonLifecycle addonLifecycle)
    {
        ECommonsMain.Init(dalamud, this);

        lifeCycle = addonLifecycle;
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        CommandManager.AddHandler(MainCommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open config window"
        });

        _dtrEntry = Svc.DtrBar.Get("EasyStables");
        _dtrEntry.OnClick = OnDTRClick;

        // Use /xllog to open the log window in-game

        resetTimers();

        Svc.Framework.Update += FrameworkUpdate;
        Svc.Chat.ChatMessage += ChatMessage;

        WindowSystem.AddWindow(MainWindow = new SamplePlugin.Windows.MainWindow(this, Configuration));
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;

        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
    }

    public void ToggleMainUi() => MainWindow.Toggle();
    public void ToggleConfigUi() => MainWindow.Toggle();


    public void Dispose()
    {
        isEnabled = false;

        unregisterListeners();

        Svc.Framework.Update -= FrameworkUpdate;

        CommandManager.RemoveHandler(MainCommandName);

        _dtrEntry.Remove();

        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        WindowSystem.RemoveAllWindows();
        MainWindow.Dispose();
        Configuration.Save();
    }

    //private unsafe void LogGameEvent(AtkEvent* atkEvent)
    //{
    //    Log.Information($"""
    //            Logging atkEvent: {((int)atkEvent).ToString("X")}
    //        """);

    //    Log.Information($"""
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

    //        LogGameEvent(atkEvent);
    //        Log.Information($"""
    //            atkEventData->ListItemRenderer: {((int)(((AtkEventData.AtkListItemData*)evt.AtkEventData))->ListItemRenderer).ToString("X")}  
    //            atkEventData->ListItem: {((int)(((AtkEventData.AtkListItemData*)evt.AtkEventData))->ListItem).ToString("X")}
    //            atkEventData->SelectedIndex: {((int)(((AtkEventData.AtkListItemData*)evt.AtkEventData))->HoveredItemIndex3)}
    //            atkEventData->MouseButtonId: {((int)(((AtkEventData.AtkListItemData*)evt.AtkEventData))->MouseButtonId)}
    //            atkEventData->MouseModifier: {((int)(((AtkEventData.AtkListItemData*)evt.AtkEventData))->MouseModifier)}
    //            """);
    //    }
    //}

    private unsafe void syntheticListItemRendererClick(AtkComponentList* list, AtkEventListener* addon, int stablesListItemToSelect)
    {
        var target = (AtkEventTarget*)list;
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
                ListItemRenderer = (AtkComponentListItemRenderer*)list->UldManager.NodeList[stablesListItemToSelect + 1]->GetAsAtkComponentListItemRenderer(), // replace with stablesListToSelect 
                ListItem = null,
                SelectedIndex = stablesListItemToSelect,
                //UnkListField15C = 0, // this is private for some reason, but it's 0 anyways
                HoveredItemIndex3 = (short)stablesListItemToSelect,
                MouseButtonId = 0,
                MouseModifier = FFXIVClientStructs.FFXIV.Component.GUI.ModifierFlag.None
            }
        };
        var eventData = stackalloc AtkEventData[1] { atkEventData };
        addon->ReceiveEvent(eventType, 2, syntheticEvent, eventData);

        //LogGameEvent(syntheticEvent);
    }

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

        syntheticListItemRendererClick(stablesListComponent, (AtkEventListener*)addon, stablesListToSelect);

        setSelectedStablesListIdx(addon, stablesListToSelect); // need to set this manually; UI still does not update properly. but this works

        this.resetTimers();
    }

    private unsafe bool IsChocoboCapped(AtkTextNode* rank)
    {
        var color = rank->TextColor.RGBA;

        return color != 0xFFC5E1EE; // uncapped is 0xFF378EF0
    }

    private unsafe bool IsChocoboReady(AtkTextNode* training)
    {
        return training->GetText().ToString().Equals("Ready");
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

    private bool timerNotReady(ref long timer, long delayMs)
    {
        if (timer == 0)
        {
            timer = Environment.TickCount64 + delayMs;
            return false;
        }
        else if (Environment.TickCount64 < timer)
        {
            return true;
        }
        return false;
    }

    private bool timerNotReadyOrNeedsStart(ref long timer, long delayMs)
    {
        if (timer == 0)
        {
            timer = Environment.TickCount64 + delayMs;
            return true;
        }
        else if (Environment.TickCount64 < timer)
        {
            return true;
        }
        return false;
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
                MouseModifier = FFXIVClientStructs.FFXIV.Component.GUI.ModifierFlag.None
            }
        };
        var eventData = stackalloc AtkEventData[1] { atkEventData };

        stablesAddon->ReceiveEvent(AtkEventType.ListItemClick, 3, syntheticListItemClick, eventData);

        this.resetTimers();
    }

}
