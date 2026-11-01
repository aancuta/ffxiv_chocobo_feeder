using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.UI;
using System;

namespace EasyStables;

public sealed partial class Plugin
{
    public void resetBirdTimer()
    {
        timeToDoStuffInStableCleanliness = Environment.TickCount64 + 5 * Configuration.userDelayMs;
    }

    private void ResetBirdTimer(AddonEvent type, AddonArgs args)
    {
        resetBirdTimer();
    }

    private unsafe void UseBroom(AddonEvent type, AddonArgs args)
    {
        var addonAddress = args.Addon.Address;
        var stablesAddon = (AddonSelectYesno*)addonAddress;

        var select = new AddonMaster.SelectYesno(stablesAddon);
        Log.Info(select.Text);
        if (select.Text.Contains("Use a magicked stable broom"))
        {
            resetBirdTimer(); // after broom, feed the birds immediately without waiting for the next sync
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
}
