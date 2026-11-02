using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dalamud.IoC;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;

namespace MuteQoL;

// Vendor selling rules. This file only decides and previews; nothing is sold yet.
public sealed unsafe partial class Plugin
{
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;
    [PluginService] internal static IUnlockState UnlockState { get; private set; } = null!;

    private const int MaxPrice = 1000;      // market items worth less than this (gil) go to the vendor
    private const int MinGearItemLevel = 760; // gear below this item level goes to the vendor


    private sealed record SellCandidate(Item Row, uint ItemId, int Quantity, string Reason, int? MarketPrice);

    private void SellPreview()
    {
        var im = InventoryManager.Instance();
        if (im == null) return;

        var gearsetItems = GetGearsetItemIds();
        var items = DataManager.GetExcelSheet<Item>();
        var bagItems = new Dictionary<uint, int>();
        foreach (var bag in Bags)
        {
            var container = im->GetInventoryContainer(bag);
            if (container == null) continue;
            for (var slot = 0; slot < container->Size; slot++)
            {
                var invItem = container->GetInventorySlot(slot);
                if (invItem == null || invItem->ItemId == 0) continue;
                var id = invItem->ItemId % 1_000_000;
                bagItems[id] = bagItems.GetValueOrDefault(id) + (int)invItem->Quantity;
            }
        }

        var gear = new List<SellCandidate>();
        var priced = new List<Item>();
        var currentMateria = CurrentMateriaIds();
        foreach (var (id, qty) in bagItems)
        {
            if (!items.TryGetRow(id, out var row)) continue;
            if (gearsetItems.Contains(id) || config.KeepItems.Contains(id) || currentMateria.Contains(id)) continue;
            if (row.PriceLow == 0) continue; // the vendor does not buy it
            if (UnlockState.IsItemUnlockable(row) && !UnlockState.IsItemUnlocked(row)) continue; // not collected yet

            if (row.EquipSlotCategory.RowId != 0)
            {
                if (IsCrafterOrGatherer(row)) continue;
                if (row.LevelItem.RowId < MinGearItemLevel)
                    gear.Add(new SellCandidate(row, id, qty, $"gear ilvl {row.LevelItem.RowId}", null));
            }
            else if (row.ItemSearchCategory.RowId > 0)
            {
                priced.Add(row);
            }
        }

        var worldId = PlayerState.HomeWorld.RowId;
        _ = Universalis.GetPrices(priced.Select(r => r.RowId).ToList(), worldId).ContinueWith(t =>
        {
            var prices = t.Result;
            var sell = new List<SellCandidate>(gear);
            var noData = 0;
            foreach (var row in priced)
            {
                if (!prices.TryGetValue(row.RowId, out var price)) { noData++; continue; }
                if (price < MaxPrice)
                    sell.Add(new SellCandidate(row, row.RowId, bagItems[row.RowId], $"market ~{price} gil", price));
            }
            _ = Framework.RunOnFrameworkThread(() => PrintSellPreview(sell, noData));
        });
    }

    // The two highest materia grades that exist in the game data are "current" and never sold.
    private HashSet<uint> CurrentMateriaIds()
    {
        var ids = new HashSet<uint>();
        var sheet = DataManager.GetExcelSheet<Materia>();
        var top = -1;
        foreach (var row in sheet)
            for (var i = 0; i < row.Item.Count; i++)
                if (row.Item[i].RowId != 0) top = Math.Max(top, i);
        if (top < 0) return ids;
        foreach (var row in sheet)
            for (var i = Math.Max(0, top - 1); i <= top && i < row.Item.Count; i++)
                if (row.Item[i].RowId != 0) ids.Add(row.Item[i].RowId);
        return ids;
    }

    private void PrintSellPreview(List<SellCandidate> sell, int noData)
    {
        long total = 0;
        foreach (var c in sell.OrderBy(c => c.Row.Name.ToString()))
        {
            var gil = (long)c.Row.PriceLow * c.Quantity;
            total += gil;
            Chat.Print($"[MuteQoL] would sell {c.Quantity}x {c.Row.Name} ({c.Reason}) -> {gil} gil");
        }
        Chat.Print($"[MuteQoL] sell preview: {sell.Count} item type(s), about {total} gil from the vendor. {noData} item(s) skipped (no market data). Nothing was sold.");
    }

}
