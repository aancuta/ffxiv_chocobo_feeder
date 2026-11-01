using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Memory;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System;
using System.Collections.Generic;

namespace EasyStables;

public sealed partial class Plugin
{
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
}
