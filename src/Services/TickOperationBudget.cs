namespace SVSAP.Services;

internal sealed class TickOperationBudget
{
    private uint currentTick;
    private int used;

    public int GetUsed(uint tick)
    {
        this.ResetIfNewTick(tick);
        return this.used;
    }

    public void SetUsed(uint tick, int value, int maxOperations)
    {
        this.ResetIfNewTick(tick);
        this.used = Math.Clamp(value, 0, Math.Max(1, maxOperations));
    }

    private void ResetIfNewTick(uint tick)
    {
        if (this.currentTick == tick)
            return;

        this.currentTick = tick;
        this.used = 0;
    }
}
