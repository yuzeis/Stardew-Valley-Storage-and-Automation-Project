namespace SVSAP.Models;

internal static class RemoteSnapshotSessionRules
{
    public static bool Matches(Guid expectedSessionId, Guid candidateSessionId)
    {
        return expectedSessionId != Guid.Empty && candidateSessionId == expectedSessionId;
    }

    public static bool IsNewer(long lastAppliedSequence, long candidateSequence)
    {
        return candidateSequence > lastAppliedSequence;
    }

    public static bool ShouldApplyPush(long lastAppliedSequence, long candidateSequence)
    {
        return candidateSequence <= 0 || candidateSequence > lastAppliedSequence;
    }

    public static bool ShouldOpenMenu(bool consumedPendingSession, bool hasActiveMenu)
    {
        return consumedPendingSession && !hasActiveMenu;
    }

    public static bool HasTimedOut(int startedAtTick, int currentTick, int timeoutTicks)
    {
        if (timeoutTicks <= 0 || currentTick < startedAtTick)
            return true;

        return currentTick - startedAtTick >= timeoutTicks;
    }
}
