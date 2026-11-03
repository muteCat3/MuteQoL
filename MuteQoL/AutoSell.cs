namespace MuteQoL;

// Optional: when a shop opens, run the same sell as '/qol sell run' once. Off by default (/qol sell auto toggles).
public sealed unsafe partial class Plugin
{
    private bool shopWasOpen;
    private int autoSellDelay = -1;

    private void AutoSellTick()
    {
        var open = !GameGui.GetAddonByName("Shop").IsNull;
        if (open && !shopWasOpen && config.AutoSell) autoSellDelay = 30; // give the shop window time to load
        if (!open) autoSellDelay = -1;
        shopWasOpen = open;

        if (autoSellDelay < 0 || --autoSellDelay > 0) return;
        autoSellDelay = -1;
        if (sellStep == SellStep.Idle && sellQueue.Count == 0) SellPreview(run: true);
    }
}
