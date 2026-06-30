
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Chat;
using Dalamud.Game.ClientState.GamePad;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Command;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Memory;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
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

    private HttpClient httpClient = new HttpClient();

    public async Task SendToBark(string title, string content)
    {
        Log.Info(Configuration.BarkServer);
        if (Configuration.BarkServer.IsNullOrEmpty())
        {
            // don't error out for no server URL, just don't send the notification
            return;
        }

        string serverUrl = Configuration.BarkServer.TrimEnd('/');
        var url = $"{serverUrl}/{title}/{content}?" +
                 $"sound=ff14&icon=https://cdn2.steamgriddb.com/icon/5f268dfb0fbef44de0f668a022707b86/32/256x256.png" + // Customize notify ring and icon
                 "&level=timeSensitive"; // iOS Time Sensitive
        var response = await httpClient.GetAsync(url);
        response.EnsureSuccessStatusCode();
    }


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


    static HashSet<Dalamud.Game.Text.XivChatType> DalamudChatsToMonitor = new HashSet<Dalamud.Game.Text.XivChatType> {
        Dalamud.Game.Text.XivChatType.GmSay,
        Dalamud.Game.Text.XivChatType.GmShout,
        Dalamud.Game.Text.XivChatType.GmYell,
        Dalamud.Game.Text.XivChatType.GmTell,
        Dalamud.Game.Text.XivChatType.GmParty,
        Dalamud.Game.Text.XivChatType.TellIncoming,
    };

    private void ChatMessage(IChatMessage message)
    {
        if (!DalamudChatsToMonitor.Contains(message.Type))
        {
            return;
        }
        SendToBark("Chat Message", $"{message.Sender}: {message.Message.ToString()}");
    }

    public void invalidateTimers()
    {
        timeToDoStuffInStableCleanliness = 0;
    }

    private void ResetBirdTimer(AddonEvent type, AddonArgs args)
    {
        timeToDoStuffInStableCleanliness = 0;
    }

    private unsafe void UseBroom(AddonEvent type, AddonArgs args)
    {
        var addonAddress = args.Addon.Address;
        var stablesAddon = (AddonSelectYesno*)addonAddress;

        var select = new AddonMaster.SelectYesno(stablesAddon);
        Log.Info(select.Text);
        if (select.Text.Contains("Use a magicked stable broom"))
        {
            timeToDoStuffInStableCleanliness = 0; // after broom, feed the birds immediately without waiting for the next sync
            select.Yes();
        }
    }

    public void registerListeners()
    {
        { // HousingChocoboList
            lifeCycle.RegisterListener(AddonEvent.PreOpen, "HousingChocoboList", HookStablesOpen);
            lifeCycle.RegisterListener(AddonEvent.PostClose, "HousingChocoboList", HookStablesClose);
            lifeCycle.RegisterListener(AddonEvent.PostDraw, "HousingChocoboList", SyncWithGameState);
        }

        { // InventoryGrid
            lifeCycle.RegisterListener(AddonEvent.PostDraw, "InventoryGrid", SearchInventoryForFood);
            lifeCycle.RegisterListener(AddonEvent.PostDraw, "InventoryGrid3E", SearchInventoryForFood); // support full inventory
        }

        { // SelectString
            lifeCycle.RegisterListener(AddonEvent.PostDraw, "SelectString", CheckIfStableIsClean);
            lifeCycle.RegisterListener(AddonEvent.PostClose, "SelectString", ResetBirdTimer);
        }

        { // SelectYesno
            lifeCycle.RegisterListener(AddonEvent.PostDraw, "SelectYesno", UseBroom);
        }

        // { // debug code:
        //     addonLifecycle.RegisterListener(AddonEvent.PreReceiveEvent, "SelectString", PreEventHook);
        //     addonLifecycle.RegisterListener(AddonEvent.PostReceiveEvent, "HousingChocoboList", PostEventHook);
        // }
    }

    public void unregisterListeners()
    {
        { // HousingChocoboList
            lifeCycle.UnregisterListener(AddonEvent.PreOpen, "HousingChocoboList", HookStablesOpen);
            lifeCycle.UnregisterListener(AddonEvent.PostClose, "HousingChocoboList", HookStablesClose);
            lifeCycle.UnregisterListener(AddonEvent.PostDraw, "HousingChocoboList", SyncWithGameState);
        }

        { // InventoryGrid
            lifeCycle.UnregisterListener(AddonEvent.PostDraw, "InventoryGrid", SearchInventoryForFood);
            lifeCycle.UnregisterListener(AddonEvent.PostDraw, "InventoryGrid3E", SearchInventoryForFood);
        }

        { // SelectString
            lifeCycle.UnregisterListener(AddonEvent.PostDraw, "SelectString", CheckIfStableIsClean);
            lifeCycle.UnregisterListener(AddonEvent.PostClose, "SelectString", ResetBirdTimer);
        }

        { // SelectYesno
            lifeCycle.UnregisterListener(AddonEvent.PostDraw, "SelectYesno", UseBroom);
        }
    }

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

    private unsafe void FrameworkUpdate(IFramework framework)
    {
        if (timerNotReadyOrNeedsStart(ref timeToDoStuffInFrameworkUpdate, Configuration.userDelayMs))
        {
            // no need to refresh UI on each FPS
            return;
        }

        string dtrText = "Stables: ";

        if (isEnabled)
        {
            dtrText += "Enabled";
        } else
        {
            dtrText += "Disabled";
        }


        // convert remaining bird timer to minutes:
        float timeToNextTimer = timeToDoStuffInStableCleanliness - Environment.TickCount64;
        timeToNextTimer /= 1000; // convert to seconds
        string unitOfMeasurement;
        if (timeToNextTimer < 60)
        {
            unitOfMeasurement = "s";
        }
        else
        {
            timeToNextTimer /= 60; // convert to minutes
            unitOfMeasurement = "min";
        }

        timeToNextTimer = (float)Math.Round(timeToNextTimer, 0);

        if (timeToNextTimer <= 0)
        {
            timeToNextTimer = 0;
        }
        string timeLeft = " - " + (timeToNextTimer == 0 ? "Ready!" : timeToNextTimer.ToString() + unitOfMeasurement);
        dtrText += timeLeft;

        _dtrEntry.Text = new Dalamud.Game.Text.SeStringHandling.SeString(new Dalamud.Game.Text.SeStringHandling.Payloads.TextPayload(dtrText));


        if (!EzThrottler.Throttle("OpenStables", Configuration.userDelayMs))
        {
            // Don't spam multiple openStables() calls until the window opens
            return;
        }

        if (isEnabled && !(isStablesInitialMenuOpen() || stablesOpen))
        {
            Log.Info("Opening stables!");
            openStables();
        }
    }


    private unsafe bool isStablesInitialMenuOpen()
    {
        var stablesSelectString = (AddonSelectString*)Svc.GameGui.GetAddonByName("SelectString").Address;
        if (stablesSelectString == null) {
            return false;
        }
        
        if (stablesSelectString->IsVisible == false)
        {
            return false;
        }

        var textNode = stablesSelectString->GetNodeById(2);
        if (textNode == null)
        {
            return false;
        }

        var castedTextNode = textNode->GetAsAtkTextNode();
        return castedTextNode != null && castedTextNode->GetText().ToString().Contains("Stable Cleanliness");
    }

    private unsafe void CheckIfStableIsClean(AddonEvent type, AddonArgs args)
    {
        if (stablesOpen)
        {
            // for some reason, this also gets called during chocobo feeding.
            // we only care about this handle for the entry menu
            // otherwise it messes up the state machine through the resetStateToInitial call below
            return;
        }


        if (timerNotReady(ref timeToDoStuffInStableCleanliness, timeToDoStuffInStableCleanliness))
        {
            // parse the chocobo list every BirdTimer
            return;
        }

        Log.Info("CheckIfStableIsClean");

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
        if (!cString.HasValue)
        {
            Log.Error($"null cString??");
            return;
        }

        resetStateToInitial();

        var cSharpString = cString.ToString();
        var select = new AddonMaster.SelectString(stablesAddon);
        var entryToSelect = "Tend to a Specified Chocobo";
        if (cSharpString.Contains("Stable Cleanliness") && cSharpString.Contains("Stable Cleanliness: Good") == false)
        {
            ByteColor red = new ByteColor();
            red.A = 255;
            red.R = 255;
            red.G = 0;
            red.B = 0;
            castedTextNode->TextColor = red;
            entryToSelect = "Clean Stable";
        } else
        {
            entryToSelect = "Tend to a Specified Chocobo";
        }

        foreach (var entry in select.Entries)
        {
            if (entry.Text == entryToSelect)
            {
                entry.Select();
            }
        }
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

    private void HookStablesClose(AddonEvent type, AddonArgs args)
    {
        Log.Info("HookStablesClose");
        // this is called in between feeding chocobos!
        stablesOpen = false;
        resetTimers();
        timerNotReady(ref timeToDoStuffInStableCleanliness, timeToDoStuffInStableCleanliness);
    }

    private unsafe void HookStablesOpen(AddonEvent type, AddonArgs args)
    {
        Log.Info("HookStablesOpen");
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

    private unsafe void SearchInventoryForFood(AddonEvent type, AddonArgs args)
    {
        if (!stablesOpen)
        {
            // don't break non-automated inventory open
            return;
        }

        if (timerNotReadyOrNeedsStart(ref timeToDoStuffInInventory, Configuration.userDelayMs))
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
                var contextMenu = RaptureAtkUnitManager.Instance()->GetAddonByName("ContextMenu");
                var contextMenuAddon = (AddonContextMenu*)contextMenu; 
                var contextAgent = AgentInventoryContext.Instance();

                int indexOfReward = -1;
                var looper = 0;
                // apparently we need to loop because "Reward" is not always at index 0...
                foreach (var contextObj in contextAgent->EventParams)
                {
                    if (contextObj.Type == FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.String)
                    {
                        var label = MemoryHelper.ReadSeStringNullTerminated(new IntPtr(contextObj.String));

                        if (label.TextValue == "Reward")
                        {
                            indexOfReward = looper;
                        }

                        looper++;
                    }
                }

                if (indexOfReward >= 0) {
                    Log.Information($"Clicking Reward!");

                    ECommons.Automation.Callback.Fire((AtkUnitBase*)contextMenuAddon, false, 0, indexOfReward, 0, 0, 0);
                }

                this.resetTimers();
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
                MouseModifier = FFXIVClientStructs.FFXIV.Component.GUI.ModifierFlag.None
            }
        };
        var eventData = stackalloc AtkEventData[1] { atkEventData };

        stablesAddon->ReceiveEvent(AtkEventType.ListItemClick, 3, syntheticListItemClick, eventData);

        this.resetTimers();
    }

    private unsafe int remainingTrainingTimeToMs(AtkTextNode* trainingTextNode)
    {
        Regex numberRegex = new Regex(@"\d+");
        var match = numberRegex.Match(trainingTextNode->GetText().ToString());
        if (match.Success)
        {
            var nextFeedTime = trainingTextNode->GetText().ToString(); // 42m for example
                                                                       // strip the "m" and convert to int
            var nextFeedTimeInt = int.Parse(match.Value);
            return nextFeedTimeInt * 60 * 1000; // convert to milliseconds
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
        int msUntilNextFeed = -1;

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

            bool isCapped = IsChocoboCapped(chocoboRankTextNode);
            bool isReady = IsChocoboReady(trainingTextNode);
            if (!isReady)
            {
                var remainingTime = remainingTrainingTimeToMs(trainingTextNode);
                msUntilNextFeed = Math.Max(msUntilNextFeed, remainingTime);
            }

            string chocoboIdentifier = $"{chocoboNameTextNode->GetText()}@{chocoboOwnerTextNode->GetText()}";

            if (isCapped)
            {
                cappedChocobos.Add(chocoboIdentifier);
            }
            chocoboRanks[chocoboIdentifier] = chocoboRankTextNode->GetText().ToString();

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

                // the bird timer needs to be updated to minutesUntilNextFeed minutes in case someone else fed the birds.
                if (msUntilNextFeed >= 0)
                {
                    timeToDoStuffInStableCleanliness = Environment.TickCount64 + msUntilNextFeed;
                }
            }
            this.resetTimers();
        } else
        {
            // continue feeding on this page through next sync
        }
    }

    // same as https://github.com/PunishXIV/PandorasBox/blob/f02c572816446a0f9c34fa3e53480f07f08283fe/PandorasBox/Helpers/GameObjectHelper.cs#L11
    public static float GetTargetDistance(IGameObject target)
    {
        if (target is null || Svc.Objects.LocalPlayer is null)
            return 0;

        if (target.GameObjectId == Svc.Objects.LocalPlayer.GameObjectId)
            return 0;

        Vector3 position = new(target.Position.X, target.Position.Z, target.Position.Y);
        Vector3 selfPosition = new(Player.Position.X, Player.Position.Z, Player.Position.Y);

        return Math.Max(0, Vector3.Distance(position, selfPosition) - target.HitboxRadius - Svc.Objects.LocalPlayer.HitboxRadius);
    }

    private bool isObjectWithinDistance(IGameObject obj, float yalms)
    {
        return GetTargetDistance(obj) <= yalms;
    }

    private unsafe bool isStables(IGameObject obj)
    {
        if (obj == null) return false;
        // GameObjectId was 1073743500, then changed to 1073743521; string is more reliable, although will only work with an EN game
        return obj.Name.ToString() == "Chocobo Stable";
    }

    private IGameObject? FindNearestStables()
    {
        foreach (var obj in Svc.Objects)
        {
            if (obj != null && isStables(obj) && isObjectWithinDistance(obj, 4.0f)) {
                return obj;
            }
        }
        return null;
    }

    private unsafe void openStables()
    {
        var stables = FindNearestStables();
        if (stables != null)
        {
            Svc.Targets.Target = stables; // not needed functionally, but feeding birds without focusing the stables can be sus to other players
            TargetSystem.Instance()->InteractWithObject((FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)stables.Address, false);
        }
        else
        {
            Log.Error("No Stables found!");
        }
    }

    private unsafe void OnDTRClick(DtrInteractionEvent ev)
    {
        isEnabled = !isEnabled;

        if (isEnabled)
        {
            if (FindNearestStables() == null)
            {
                ChatGui.PrintError("No nearby Stables found!");
                isEnabled = false;
                return;
            }
        }

        if (isEnabled)
        {
            registerListeners();
        }
        else
        {
            unregisterListeners();

            var fedChocobosThatCapped = fedChocobos.Intersect(cappedChocobos);
            if (fedChocobosThatCapped.Any())
            {
                ChatGui.Print($"[Easy Stables] {fedChocobosThatCapped.Count()} Birds capped during session:");
                foreach (var cappedFedChocobo in fedChocobosThatCapped)
                {
                    string rank = chocoboRanks.GetValueOrDefault(cappedFedChocobo, "N/A");
                    ChatGui.Print($"birbcapped: {cappedFedChocobo} on rank {rank}");
                }

            }
            else
            {
                ChatGui.Print($"[Easy Stables] No birds capped during session.");
            }
            fedChocobos.Clear();
            cappedChocobos.Clear();
            chocoboRanks.Clear();
        }
    }

    private void OnCommand(string command, string args)
    {
        if (command.ToLower() != "/easystables")
        {
            return;
        }

        MainWindow.Toggle();
    }
}
