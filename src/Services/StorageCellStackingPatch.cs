using HarmonyLib;
using SVSAP.Content;
using StardewModdingAPI;
using StardewValley;

namespace SVSAP.Services;

internal static class StorageCellStackingPatch
{
    public static void Apply(string uniqueId, IMonitor monitor)
    {
        try
        {
            var target = AccessTools.Method(typeof(Item), nameof(Item.canStackWith), new[] { typeof(ISalable) });
            if (target is null)
            {
                monitor.Log("SVSAP could not patch Item.canStackWith(ISalable); storage cells may still need inventory normalization fallback.", LogLevel.Warn);
                return;
            }

            var harmony = new Harmony(uniqueId);
            harmony.Patch(
                target,
                prefix: new HarmonyMethod(typeof(StorageCellStackingPatch), nameof(CanStackWithPrefix)));
        }
        catch (Exception ex)
        {
            monitor.Log($"SVSAP could not apply the storage-cell stacking Harmony patch: {ex.GetType().Name}: {ex.Message}", LogLevel.Warn);
        }
    }

    internal static bool CanStackPreservingCellState(Item? left, ISalable? right)
    {
        if (right is not Item rightItem)
            return true;

        return !IsStorageCell(left?.QualifiedItemId) && !IsStorageCell(rightItem.QualifiedItemId);
    }

    private static bool CanStackWithPrefix(Item __instance, ISalable other, ref bool __result)
    {
        try
        {
            if (CanStackPreservingCellState(__instance, other))
                return true;

            __result = false;
            return false;
        }
        catch
        {
            return true;
        }
    }

    private static bool IsStorageCell(string? qualifiedItemId)
    {
        return !string.IsNullOrWhiteSpace(qualifiedItemId)
            && ModItemCatalog.TryGetStorageCellTier(qualifiedItemId, out _);
    }
}
