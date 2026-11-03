using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace MuteQoL;

// /qol probe (with a vendor open): opens the item context menu for the first sellable bag item and lists its
// entries, without clicking any. This tells us the real menu layout before anything is sold.
public sealed unsafe partial class Plugin
{
    private int probeFrames = -1;

    private void SellProbe()
    {
        var shop = GameGui.GetAddonByName("Shop");
        if (shop.IsNull)
        {
            Chat.Print("[MuteQoL] open a vendor's shop window first.");
            return;
        }
        var im = InventoryManager.Instance();
        var agent = AgentInventoryContext.Instance();
        if (im == null || agent == null) return;
        var items = DataManager.GetExcelSheet<Item>();
        foreach (var bag in Bags)
        {
            var container = im->GetInventoryContainer(bag);
            if (container == null) continue;
            for (var slot = 0; slot < container->Size; slot++)
            {
                var invItem = container->GetInventorySlot(slot);
                if (invItem == null || invItem->ItemId == 0) continue;
                if (!items.TryGetRow(invItem->ItemId % 1_000_000, out var row) || row.PriceLow == 0) continue;
                Chat.Print($"[MuteQoL] probing the menu of {row.Name} ({bag} slot {slot}); nothing will be clicked.");
                agent->OpenForItemSlot(bag, slot, 0, ((AtkUnitBase*)shop.Address)->Id);
                probeFrames = 5;
                return;
            }
        }
        Chat.Print("[MuteQoL] no sellable item in the bags.");
    }

    private void ProbeTick()
    {
        if (probeFrames < 0 || --probeFrames > 0) return;
        probeFrames = -1;
        var menu = GameGui.GetAddonByName("ContextMenu");
        if (menu.IsNull)
        {
            Chat.Print("[MuteQoL] the context menu did not open.");
            return;
        }
        var unit = (AtkUnitBase*)menu.Address;
        for (var i = 0; i < unit->AtkValuesCount; i++)
        {
            var v = &unit->AtkValues[i];
            var text = v->Type is AtkValueType.String or AtkValueType.ManagedString or AtkValueType.String8
                ? v->String.ToString()
                : v->Type == AtkValueType.Int ? v->Int.ToString() : "";
            Log.Information($"[MuteQoL probe] value {i}: {v->Type} {text}");
            if (text.Length > 0 || (v->Type == AtkValueType.Int && v->Int != 0)) Chat.Print($"[MuteQoL] menu value {i}: {v->Type} {text}");
        }
    }
}
