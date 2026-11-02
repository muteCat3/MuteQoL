using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace MuteQoL;

// Average sale price per unit on the home world, from Universalis; cached for an hour.
internal static class Universalis
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly ConcurrentDictionary<uint, (DateTime At, int? Price)> Cache = new();

    public static async Task<Dictionary<uint, int>> GetPrices(List<uint> ids, uint worldId)
    {
        var result = new Dictionary<uint, int>();
        var missing = new List<uint>();
        foreach (var id in ids)
        {
            if (Cache.TryGetValue(id, out var cached) && DateTime.UtcNow - cached.At < TimeSpan.FromHours(1))
            {
                if (cached.Price is { } p) result[id] = p;
            }
            else missing.Add(id);
        }

        foreach (var chunk in missing.Chunk(100))
        {
            try
            {
                var url = $"https://universalis.app/api/v2/aggregated/{worldId}/{string.Join(',', chunk)}";
                using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
                var found = new HashSet<uint>();
                foreach (var entry in doc.RootElement.GetProperty("results").EnumerateArray())
                {
                    var id = entry.GetProperty("itemId").GetUInt32();
                    var price = ReadAverage(entry, "nq") ?? ReadAverage(entry, "hq");
                    Cache[id] = (DateTime.UtcNow, price);
                    if (price is { } p) result[id] = p;
                    found.Add(id);
                }
                foreach (var id in chunk.Where(i => !found.Contains(i))) Cache[id] = (DateTime.UtcNow, null);
            }
            catch (Exception ex)
            {
                Plugin.Log.Warning(ex, "Universalis request failed");
            }
        }
        return result;
    }

    private static int? ReadAverage(JsonElement entry, string quality)
    {
        if (!entry.TryGetProperty(quality, out var q)) return null;
        if (!q.TryGetProperty("averageSalePrice", out var avg)) return null;
        if (!avg.TryGetProperty("world", out var world)) return null;
        if (!world.TryGetProperty("price", out var price)) return null;
        return (int)Math.Round(price.GetDouble());
    }
}
