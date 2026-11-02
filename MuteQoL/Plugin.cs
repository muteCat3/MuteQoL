using System;
using System.Collections.Generic;
using Dalamud.Configuration;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Game.Command;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Dalamud.Game.Inventory.InventoryEventArgTypes;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Lumina.Excel.Sheets;

namespace MuteQoL;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;
    public bool Auto { get; set; } = true;
    public List<string> HiddenAddons { get; set; } = [];
}

public sealed unsafe class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IChatGui Chat { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IGameInventory GameInventory { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;
    [PluginService] internal static IKeyState KeyState { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;

    private const string CommandName = "/armorysort";
    private const string QolCommand = "/qol";

    // Addons we hid ourselves, so releasing Ctrl only restores those and nothing else.
    private readonly HashSet<string> hiddenNow = [];
    private bool ctrlWasDown;
    private bool findRequested;
    private bool findAll;
    private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(1);

    private static readonly InventoryType[] Bags =
    [
        InventoryType.Inventory1, InventoryType.Inventory2,
        InventoryType.Inventory3, InventoryType.Inventory4,
    ];

    private static readonly ConditionFlag[] Busy =
    [
        ConditionFlag.InCombat, ConditionFlag.Crafting, ConditionFlag.PreparingToCraft,
        ConditionFlag.Gathering, ConditionFlag.BetweenAreas, ConditionFlag.BetweenAreas51,
    ];

    private readonly Configuration config;
    private DateTime? dueAt;

    public Plugin()
    {
        config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "'/armorysort' previews, '/armorysort run' moves, '/armorysort auto' toggles automatic sorting.",
        });
        CommandManager.AddHandler(QolCommand, new CommandInfo(OnQolCommand)
        {
            HelpMessage = "/qol find (hover an element first), /qol hide <addon>, /qol show <addon>, /qol list. Hidden elements appear while Ctrl is held.",
        });
        GameInventory.InventoryChanged += OnInventoryChanged;
        Framework.Update += OnUpdate;
        PluginInterface.UiBuilder.Draw += OnDraw;
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= OnDraw;
        SetHiddenVisible(true);
        CommandManager.RemoveHandler(QolCommand);
        Framework.Update -= OnUpdate;
        GameInventory.InventoryChanged -= OnInventoryChanged;
        CommandManager.RemoveHandler(CommandName);
    }

    private void OnInventoryChanged(IReadOnlyCollection<InventoryEventArgs> events)
    {
        if (config.Auto) dueAt = DateTime.UtcNow + Debounce;
    }

    // Hidden elements stay hidden unless Ctrl is held; the same for every job.
    private void UpdateHiddenAddons()
    {
        var ctrl = KeyState[VirtualKey.CONTROL];
        if (ctrl)
        {
            if (!ctrlWasDown) SetHiddenVisible(true);
        }
        else
        {
            foreach (var spec in config.HiddenAddons)
                if (SetVisible(spec, false)) hiddenNow.Add(spec);
        }
        ctrlWasDown = ctrl;
    }

    private void SetHiddenVisible(bool visible)
    {
        foreach (var spec in hiddenNow) SetVisible(spec, visible);
        hiddenNow.Clear();
    }

    // spec is "Addon" for a whole element or "Addon:12" / "Addon:12>3" for one node inside it.
    private bool SetVisible(string spec, bool visible)
    {
        var parts = spec.Split(':', 2);
        var addon = GameGui.GetAddonByName(parts[0]);
        if (addon.IsNull) return false;
        var unit = (AtkUnitBase*)addon.Address;
        if (parts.Length == 1)
        {
            unit->IsVisible = visible;
            return true;
        }
        var node = FindNode(unit, parts[1]);
        if (node == null) return false;
        node->ToggleVisibility(visible);
        return true;
    }

    private static AtkResNode* FindNode(AtkUnitBase* unit, string path)
    {
        var manager = &unit->UldManager;
        AtkResNode* node = null;
        foreach (var part in path.Split('>'))
        {
            if (!uint.TryParse(part, out var id) || manager == null) return null;
            node = null;
            for (var i = 0; i < manager->NodeListCount; i++)
                if (manager->NodeList[i] != null && manager->NodeList[i]->NodeId == id) { node = manager->NodeList[i]; break; }
            if (node == null) return null;
            manager = (ushort)node->Type >= 1000 ? &((AtkComponentNode*)node)->Component->UldManager : null;
        }
        return node;
    }

    // /qol nodes <addon>: lists the parts of one UI element (and one level of sub-parts) with ids and sizes.
    private void DumpNodes(string addonName)
    {
        var addon = GameGui.GetAddonByName(addonName);
        if (addon.IsNull)
        {
            Chat.Print($"[MuteQoL] {addonName} is not loaded.");
            return;
        }
        DumpManager(&((AtkUnitBase*)addon.Address)->UldManager, "", 0);
    }

    private void DumpManager(AtkUldManager* manager, string prefix, int depth)
    {
        for (var i = 0; i < manager->NodeListCount; i++)
        {
            var node = manager->NodeList[i];
            if (node == null) continue;
            var component = (ushort)node->Type >= 1000;
            if (node->Width > 150 || node->Height > 150) continue; // the lock is a small element
            Chat.Print($"[MuteQoL] {prefix}{node->NodeId} type {(ushort)node->Type} {(int)node->Width}x{(int)node->Height} at {(int)node->X},{(int)node->Y} visible={node->IsVisible()}");
            if (component && depth < 1)
                DumpManager(&((AtkComponentNode*)node)->Component->UldManager, $"{node->NodeId}>", depth + 1);
        }
    }

    private void OnQolCommand(string command, string args)
    {
        var parts = args.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var verb = parts.Length > 0 ? parts[0].ToLowerInvariant() : "list";
        var name = parts.Length > 1 ? parts[1] : "";
        switch (verb)
        {
            case "nodes" when name.Length > 0:
                DumpNodes(name);
                break;
            case "find":
                findAll = name.Equals("all", StringComparison.OrdinalIgnoreCase);
                findRequested = true;
                break;
            case "hide" when name.Length > 0:
                if (!config.HiddenAddons.Contains(name)) config.HiddenAddons.Add(name);
                PluginInterface.SavePluginConfig(config);
                Chat.Print($"[MuteQoL] {name} is hidden; hold Ctrl to show it.");
                break;
            case "show" when name.Length > 0:
                config.HiddenAddons.Remove(name);
                PluginInterface.SavePluginConfig(config);
                SetHiddenVisible(true);
                Chat.Print($"[MuteQoL] {name} is no longer hidden.");
                break;
            default:
                Chat.Print($"[MuteQoL] hidden: {(config.HiddenAddons.Count == 0 ? "(none)" : string.Join(", ", config.HiddenAddons))}");
                break;
        }
    }

    // /qol find: lists the visible UI elements under the mouse cursor so the right addon name can be hidden.
    private void OnDraw()
    {
        if (!findRequested) return;
        findRequested = false;
        var mouse = ImGui.GetIO().MousePos;
        var manager = AtkStage.Instance()->RaptureAtkUnitManager;
        if (manager == null) return;
        var list = &manager->AtkUnitManager.AllLoadedUnitsList;
        var found = 0;
        Chat.Print($"[MuteQoL] cursor at {(int)mouse.X},{(int)mouse.Y}");
        for (var i = 0; i < list->Count; i++)
        {
            var unit = list->Entries[i].Value;
            if (unit == null || !unit->IsVisible) continue;
            float x = unit->X, y = unit->Y, w = unit->GetScaledWidth(true), h = unit->GetScaledHeight(true);
            if (w > 400 || h > 400) continue; // skip full-screen overlays and wide bars
            const float margin = 40; // small icons are hard to hit exactly
            if (!findAll && (mouse.X < x - margin || mouse.X > x + w + margin || mouse.Y < y - margin || mouse.Y > y + h + margin)) continue;
            Chat.Print($"[MuteQoL] near cursor: {unit->NameString} ({(int)w}x{(int)h} at {(int)x},{(int)y})");
            found++;
        }
        if (found == 0) Chat.Print("[MuteQoL] nothing small near the cursor.");
    }

    private void OnUpdate(IFramework framework)
    {
        UpdateHiddenAddons();
        if (dueAt is not { } due || DateTime.UtcNow < due) return;
        foreach (var flag in Busy)
            if (Condition[flag])
            {
                dueAt = DateTime.UtcNow + Debounce; // try again once the player is free
                return;
            }
        dueAt = null;
        Sort(run: true, quiet: true);
    }

    private void OnCommand(string command, string args)
    {
        var arg = args.Trim();
        if (arg.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            config.Auto = !config.Auto;
            PluginInterface.SavePluginConfig(config);
            Chat.Print($"[MuteQoL] automatic sorting {(config.Auto ? "on" : "off")}.");
            return;
        }
        Sort(arg.Equals("run", StringComparison.OrdinalIgnoreCase), quiet: false);
    }

    private void Sort(bool run, bool quiet)
    {
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
                    if (!quiet) Chat.Print($"[MuteQoL] {target}: full, skipped {row.Name}");
                    continue;
                }

                candidates++;
                if (!run)
                {
                    Chat.Print($"[MuteQoL] would move {row.Name} (ilvl {row.LevelItem.RowId}) -> {target}");
                    continue;
                }

                var result = im->MoveItemSlot(bag, (ushort)slot, target.Value, (ushort)free, true);
                if (result == 0)
                {
                    moved++;
                    if (quiet) Chat.Print($"[MuteQoL] moved {row.Name} -> {target}");
                }
                else Log.Warning($"MoveItemSlot returned {result} for {row.Name}");
            }
        }

        if (quiet) return;
        Chat.Print(run
            ? $"[MuteQoL] moved {moved} item(s)."
            : $"[MuteQoL] preview: {candidates} item(s). Use /armorysort run to move.");
    }

    // Crafter and gatherer gear (confirmed in game: the Cosmic Round Knife matches).
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
