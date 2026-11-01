using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Interface;
using Dalamud.Plugin.Services;
using ECommons;
using ECommons.DalamudServices;
using ECommons.DalamudServices.Legacy;
using ECommons.GameHelpers;
using ECommons.Throttlers;
using ECommons.UIHelpers.AddonMasterImplementations;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Graphics;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Common.Math;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using static FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.VertexShader;
using static FFXIVClientStructs.FFXIV.Component.GUI.AtkUIColorHolder.Delegates;
using SeStringBuilder = Lumina.Text.SeStringBuilder;

namespace EasyStables;

public sealed partial class Plugin
{
    private Vector4 GetDTRColor(float timeLeft, bool isEnabled)
    {
        if (isEnabled)
        {
            return ToVector(KnownColor.LightGreen);
        } else {
            if (timeLeft > 0)
            {
                return ToVector(KnownColor.Orange);
            } else
            {
                return ToVector(KnownColor.OrangeRed);
            }
        }
    }

    private static Vector4 ToVector(KnownColor knownColor)
    {
        var color = Color.FromKnownColor(knownColor);
        const float colorScale = 1f / 255f;
        return new Vector4(color.R * colorScale, color.G * colorScale, color.B * colorScale, color.A * colorScale);
    }

    private unsafe void FrameworkUpdate(IFramework framework)
    {
        if (timerNotReadyOrNeedsStart(ref timeToDoStuffInFrameworkUpdate, Configuration.userDelayMs))
        {
            // no need to refresh UI on each FPS
            return;
        }

        string dtrText = "birbs: ";

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
            unitOfMeasurement = "m";
        }

        timeToNextTimer = (float)Math.Round(timeToNextTimer, 0);

        if (timeToNextTimer <= 0)
        {
            timeToNextTimer = 0;
        } 

        string timeLeft = (timeToNextTimer == 0 ? "Ready!" : timeToNextTimer.ToString() + unitOfMeasurement);
        dtrText += timeLeft;

        _dtrEntry.Text = new SeStringBuilder()
                .PushColorRgba(GetDTRColor(timeToNextTimer, isEnabled))
                .Append(dtrText)
                .PopColor()
                .ToReadOnlySeString()
                .ToDalamudString();

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

    // same as https://github.com/PunishXIV/PandorasBox/blob/f02c572816446a0f9c34fa3e53480f07f08283fe/PandorasBox/Helpers/GameObjectHelper.cs#L11
    public static float GetTargetDistance(IGameObject target)
    {
        if (target is null || Svc.Objects.LocalPlayer is null)
            return 0;

        if (target.GameObjectId == Svc.Objects.LocalPlayer.GameObjectId)
            return 0;

        Vector3 position = new(target.Position.X, target.Position.Z, target.Position.Y);
        Vector3 selfPosition = new(Player.Position.X, Player.Position.Z, Player.Position.Y);

        return Math.Max(0, Vector3.Distance(position, selfPosition));
    }

    private bool isObjectWithinDistance(IGameObject obj, float yalms)
    {
        return GetTargetDistance(obj) < yalms;
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
            if (obj != null && isStables(obj) && isObjectWithinDistance(obj, 6.5f)) {
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

    private string cappedChocoboString(string cappedFedChocobo)
    {
        string rank = chocoboRanks.GetValueOrDefault(cappedFedChocobo, "N/A");

        bool thisChocoboNeedsOnion = false;
        if (Int32.TryParse(rank, out int numValue) && numValue >= 9 && numValue < 19)
        {
            thisChocoboNeedsOnion = true;
        }
        return $"{cappedFedChocobo} on rank {rank} {(thisChocoboNeedsOnion ? "[ONION NEEDED]" : "")}";
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

                bool anyOnionNeeded = false;
                ChatGui.Print("birbcapped:");
                foreach (var cappedFedChocobo in fedChocobosThatCapped)
                {
                    string toPrint = cappedChocoboString(cappedFedChocobo);
                    anyOnionNeeded |= toPrint.Contains("[ONION NEEDED]");
                    ChatGui.Print(toPrint);
                }

                if (anyOnionNeeded)
                {
                    ChatGui.Print("Feel free to poke any FC management in-game for a free onion for your level-up, courtesy of the FC! <3");
                    ChatGui.Print("This will save you up to 2 mil gil as a thank you for being with us!");
                    ChatGui.Print("Please check out our Chocobo Guide for info on how to use onions before you use it!");
                    ChatGui.Print("https://discord.com/channels/993258181608357949/1209321017764216862/1209321399546810408");
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
