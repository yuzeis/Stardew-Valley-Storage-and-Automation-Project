using StardewValley;
using StardewValley.Menus;
using StardewValley.Objects;

namespace SVSAP.Services;

/// <summary>
/// Chest locking helper for SVSAP network writes.
///
/// IMPORTANT: Stardew's <see cref="StardewValley.Network.NetMutex"/> grants a lock
/// asynchronously - the "acquired" callback runs on a later NetMutex.Update() tick, NOT
/// synchronously inside RequestLock(). The previous implementation called RequestLock and
/// then immediately checked an "acquired" flag, which was therefore almost always still
/// false, so it returned false and aborted EVERY chest write (deposit/withdraw/import/export
/// silently failed in single player and multiplayer alike).
///
/// We instead use an optimistic write model: a chest may be mutated this tick when it is not
/// currently held by another farmer and not open in the local vanilla chest UI. This is
/// correct in single player (single writer) and safe in multiplayer because all SVSAP chest
/// writes run host-side and complete synchronously within one tick, and contention from
/// another actor or the local UI is detected up front and skipped.
/// </summary>
internal static class ChestMutexHelper
{
    public static bool IsLockedByAnotherActor(Chest chest)
    {
        if (IsOpenInLocalMenu(chest))
            return true;

        var mutex = chest.GetMutex();
        return mutex.IsLocked() && !mutex.IsLockHeld();
    }

    /// <summary>
    /// Returns true and a (no-op) lease when it is safe to mutate the chest this tick.
    /// Does NOT block on an asynchronous NetMutex grant.
    /// </summary>
    public static bool TryAcquireImmediate(Chest chest, out ChestMutexLease lease)
    {
        lease = ChestMutexLease.NoOp;

        // The local player is editing this exact chest through the vanilla UI: never write under it.
        if (IsOpenInLocalMenu(chest))
            return false;

        var mutex = chest.GetMutex();

        // Another farmer currently holds the lock: skip this tick to avoid clobbering their edit.
        if (mutex.IsLocked() && !mutex.IsLockHeld())
            return false;

        // Free, or already held by us: safe to mutate now.
        return true;
    }

    private static bool IsOpenInLocalMenu(Chest chest)
    {
        if (Game1.activeClickableMenu is not ItemGrabMenu menu)
            return false;

        return ReferenceEquals(menu.context, chest)
            || ReferenceEquals(menu.sourceItem, chest)
            || ReferenceEquals(menu.ItemsToGrabMenu, chest.Items);
    }
}

internal sealed class ChestMutexLease : IDisposable
{
    public static readonly ChestMutexLease NoOp = new(null);

    private readonly Action? release;
    private bool released;

    public ChestMutexLease(Action? release)
    {
        this.release = release;
    }

    public void Dispose()
    {
        this.Release();
    }

    public void Release()
    {
        if (this.released)
            return;

        this.released = true;
        this.release?.Invoke();
    }
}
