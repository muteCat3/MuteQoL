using System;
using System.Collections.Generic;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace MuteQoL;

// Sells one item through the item context menu while a vendor is open. Every slot is tried once per shop visit.
public sealed unsafe partial class Plugin
{
    private enum SellStep { Idle, MenuOpening, Verifying }

    private SellStep sellStep = SellStep.Idle;
    private int sellFrames;
    private uint sellItemId;
    private string sellItemName = "";
    private InventoryType sellBag;
    private int sellSlot;
    private readonly HashSet<(InventoryType, int)> triedSlots = [];

    private void StartSellOne(SellCandidate c)
    {
        var shop = GameGui.GetAddonByName("Shop");
        if (shop.IsNull)
        {
            Chat.Print("[MuteQoL] open a vendor's shop window first.");
            return;
        }
        var im = InventoryManager.Instance();
        var agent = AgentInventoryContext.Instance();
        if (im == null || agent == null || sellStep != SellStep.Idle) return;

        foreach (var bag in Bags)
        {
            var container = im->GetInventoryContainer(bag);
            if (container == null) continue;
            for (var slot = 0; slot < container->Size; slot++)
            {
                var invItem = container->GetInventorySlot(slot);
                if (invItem == null || invItem->ItemId % 1_000_000 != c.ItemId) continue;
                if (!triedSlots.Add((bag, slot))) continue; // each slot is tried only once per shop visit

                sellBag = bag;
                sellSlot = slot;
                sellItemId = c.ItemId;
                sellItemName = c.Row.Name.ToString();
                agent->OpenForItemSlot(bag, slot, 0, ((AtkUnitBase*)shop.Address)->Id);
                sellStep = SellStep.MenuOpening;
                sellFrames = 5;
                return;
            }
        }
        Chat.Print($"[MuteQoL] {c.Row.Name}: no untried slot left in this shop visit.");
    }

    private void SellTick()
    {
        if (GameGui.GetAddonByName("Shop").IsNull) triedSlots.Clear(); // a new shop visit starts fresh
        if (sellStep == SellStep.Idle || --sellFrames > 0) return;

        if (sellStep == SellStep.MenuOpening)
        {
            var menu = GameGui.GetAddonByName("ContextMenu");
            var index = menu.IsNull ? -1 : FindSellIndex((AtkUnitBase*)menu.Address);
            if (index < 0)
            {
                Chat.Print($"[MuteQoL] {sellItemName}: no 'Sell' entry in the menu, skipped.");
                sellStep = SellStep.Idle;
                return;
            }
            var values = stackalloc AtkValue[5];
            for (var i = 0; i < 5; i++) { values[i].Type = AtkValueType.Int; values[i].Int = 0; }
            values[1].Int = index;
            ((AtkUnitBase*)menu.Address)->FireCallback(5, values, true);
            sellStep = SellStep.Verifying;
            sellFrames = 45;
            return;
        }

        // Verifying: the item must have left the slot, otherwise it counts as unsellable and is not retried.
        var im = InventoryManager.Instance();
        var container = im == null ? null : im->GetInventoryContainer(sellBag);
        var item = container == null ? null : container->GetInventorySlot(sellSlot);
        var stillThere = item != null && item->ItemId % 1_000_000 == sellItemId;
        Chat.Print(stillThere
            ? $"[MuteQoL] {sellItemName} is still in the slot: not sold (will not be retried in this shop visit)."
            : $"[MuteQoL] sold {sellItemName}.");
        sellStep = SellStep.Idle;
    }

    // Menu entries are string values; "Sell" is the first one. The callback index is its position among the entries.
    private static int FindSellIndex(AtkUnitBase* menu)
    {
        var position = 0;
        for (var i = 0; i < menu->AtkValuesCount; i++)
        {
            var v = &menu->AtkValues[i];
            if (v->Type is not (AtkValueType.String or AtkValueType.ManagedString or AtkValueType.String8)) continue;
            if (v->String.ToString().Equals("Sell", StringComparison.OrdinalIgnoreCase)) return position;
            position++;
        }
        return -1;
    }
}
