using System;
using System.Collections.Generic;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Lumina.Excel.Sheets;

namespace ArmorySorter;

public sealed unsafe class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IChatGui Chat { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    private const string CommandName = "/armorysort";

    private static readonly InventoryType[] Bags =
    [
        InventoryType.Inventory1, InventoryType.Inventory2,
        InventoryType.Inventory3, InventoryType.Inventory4,
    ];

    public Plugin()
    {
        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Preview what would move to the Armoury Chest. '/armorysort run' moves it.",
        });
    }

    public void Dispose() => CommandManager.RemoveHandler(CommandName);

    private void OnCommand(string command, string args)
    {
        var run = args.Trim().Equals("run", StringComparison.OrdinalIgnoreCase);
        var im = InventoryManager.Instance();
        if (im == null) return;

        var gearsetItems = GetGearsetItemIds();
        var items = DataManager.GetExcelSheet<Item>();
        var moved = 0;
        var candidates = 0;

        foreach (var bag in Bags)
        {
            var container = im->GetInventoryContainer(bag);
            if (container == null) continue;
            for (var slot = 0; slot < container->Size; slot++)
            {
                var invItem = container->GetInventorySlot(slot);
                if (invItem == null || invItem->ItemId == 0) continue;
                var baseId = invItem->ItemId % 1_000_000; // strip HQ and collectable offsets
                if (!items.TryGetRow(baseId, out var row)) continue;
                var target = ArmoryFor(row.EquipSlotCategory.RowId);
                if (target == null) continue;
                if (!gearsetItems.Contains(baseId) && !IsCrafterOrGatherer(row)) continue;

                var dest = im->GetInventoryContainer(target.Value);
                if (dest == null) continue;
                var free = FindFreeSlot(dest);
                if (free < 0)
                {
                    Chat.Print($"[ArmorySorter] {target}: full, skipped {row.Name}");
                    continue;
                }

                candidates++;
                if (!run)
                {
                    Chat.Print($"[ArmorySorter] would move {row.Name} (ilvl {row.LevelItem.RowId}) -> {target}");
                    continue;
                }

                var result = im->MoveItemSlot(bag, (ushort)slot, target.Value, (ushort)free, true);
                if (result == 0) moved++;
                else Log.Warning($"MoveItemSlot returned {result} for {row.Name}");
            }
        }

        Chat.Print(run
            ? $"[ArmorySorter] moved {moved} item(s)."
            : $"[ArmorySorter] preview: {candidates} item(s). Use /armorysort run to move.");
    }

    // Crafter and gatherer gear. The class job category names are confirmed on the first
    // preview run (see /xllog) before anything is moved for real.
    private static bool IsCrafterOrGatherer(Item row)
    {
        var name = row.ClassJobCategory.ValueNullable?.Name.ToString() ?? "";
        return name is "Disciples of the Hand" or "Disciples of the Land"
            or "CRP" or "BSM" or "ARM" or "GSM" or "LTW" or "WVR" or "ALC" or "CUL"
            or "MIN" or "BTN" or "FSH";
    }

    private static HashSet<uint> GetGearsetItemIds()
    {
        var ids = new HashSet<uint>();
        var module = RaptureGearsetModule.Instance();
        if (module == null) return ids;
        for (var i = 0; i < 100; i++)
        {
            var gs = module->GetGearset(i);
            if (gs == null || !gs->Flags.HasFlag(RaptureGearsetModule.GearsetFlag.Exists)) continue;
            foreach (var entry in gs->Items)
                if (entry.ItemId != 0) ids.Add(entry.ItemId % 1_000_000);
        }
        return ids;
    }

    private static InventoryType? ArmoryFor(uint equipSlotCategory) => equipSlotCategory switch
    {
        1 or 13 => InventoryType.ArmoryMainHand,
        2 => InventoryType.ArmoryOffHand,
        3 => InventoryType.ArmoryHead,
        4 => InventoryType.ArmoryBody,
        5 => InventoryType.ArmoryHands,
        7 => InventoryType.ArmoryLegs,
        8 => InventoryType.ArmoryFeets,
        9 => InventoryType.ArmoryEar,
        10 => InventoryType.ArmoryNeck,
        11 => InventoryType.ArmoryWrist,
        12 => InventoryType.ArmoryRings,
        17 => InventoryType.ArmorySoulCrystal,
        _ => null,
    };

    private static int FindFreeSlot(InventoryContainer* container)
    {
        for (var i = 0; i < container->Size; i++)
        {
            var item = container->GetInventorySlot(i);
            if (item != null && item->ItemId == 0) return i;
        }
        return -1;
    }
}
