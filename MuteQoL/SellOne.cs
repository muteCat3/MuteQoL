using System;
using System.Collections.Generic;
using System.Linq;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace MuteQoL;

// Sells the listed items one by one through the item context menu while a vendor is open.
// Every slot is tried once per shop visit, and a run stops at a hard attempt limit.
public sealed unsafe partial class Plugin
{
    private enum SellStep { Idle, MenuOpening, Verifying }

    private const int MaxSellAttempts = 60;

    private SellStep sellStep = SellStep.Idle;
    private int sellFrames;
    private uint sellItemId;
    private string sellItemName = "";
    private InventoryType sellBag;
    private int sellSlot;
    private int sellAttempts, sellSold, sellFailed;
    private readonly HashSet<(InventoryType, int)> triedSlots = [];
    private readonly Queue<SellCandidate> sellQueue = new();

    private void EnqueueSells(List<SellCandidate> sell)
    {
        if (GameGui.GetAddonByName("Shop").IsNull)
        {
            Chat.Print("[MuteQoL] open a vendor's shop window first.");
            return;
        }
        if (sellStep != SellStep.Idle || sellQueue.Count > 0) return;
        foreach (var c in sell.OrderBy(c => c.Row.Name.ToString())) sellQueue.Enqueue(c);
        sellAttempts = sellSold = sellFailed = 0;
        NextSell();
    }

    // Takes the next untried slot of the front candidate; a candidate is done when no untried slot is left.
    private void NextSell()
    {
        while (sellQueue.Count > 0)
        {
            if (sellAttempts >= MaxSellAttempts)
            {
                Chat.Print("[MuteQoL] reached the attempt limit for this run, stopping.");
                sellQueue.Clear();
                break;
            }
            if (TryStartSell(sellQueue.Peek())) { sellAttempts++; return; }
            sellQueue.Dequeue();
        }
        Chat.Print($"[MuteQoL] sell run finished: {sellSold} sold, {sellFailed} not sold.");
    }

    private bool TryStartSell(SellCandidate c)
    {
        var shop = GameGui.GetAddonByName("Shop");
        var im = InventoryManager.Instance();
        var agent = AgentInventoryContext.Instance();
        if (shop.IsNull || im == null || agent == null) return false;

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
                return true;
            }
        }
        return false;
    }

    private void SellTick()
    {
        if (GameGui.GetAddonByName("Shop").IsNull)
        {
            triedSlots.Clear(); // a new shop visit starts fresh
            if (sellQueue.Count > 0 || sellStep != SellStep.Idle)
            {
                sellQueue.Clear(); // the shop was closed: stop the run
                sellStep = SellStep.Idle;
                Chat.Print($"[MuteQoL] shop closed, sell run stopped: {sellSold} sold, {sellFailed} not sold.");
            }
            return;
        }
        if (sellStep == SellStep.Idle || --sellFrames > 0) return;

        if (sellStep == SellStep.MenuOpening)
        {
            var menu = GameGui.GetAddonByName("ContextMenu");
            var index = menu.IsNull ? -1 : FindSellIndex((AtkUnitBase*)menu.Address);
            if (index < 0)
            {
                Chat.Print($"[MuteQoL] {sellItemName}: no 'Sell' entry in the menu, skipped.");
                sellFailed++;
                sellStep = SellStep.Idle;
                NextSell();
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
        if (stillThere)
        {
            sellFailed++;
            Chat.Print($"[MuteQoL] {sellItemName} is still in the slot: not sold (not retried in this shop visit).");
        }
        else sellSold++;
        sellStep = SellStep.Idle;
        NextSell();
    }

    // Menu entries are string values; the callback index is the position of "Sell" among them.
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
