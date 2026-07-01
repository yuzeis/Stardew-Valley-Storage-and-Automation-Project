using SObject = StardewValley.Object;

namespace SVSAP.Services;

internal static class MachineStateHelper
{
    public static void ResetAfterAutomatedCollect(SObject machine)
    {
        machine.heldObject.Value = null;
        machine.readyForHarvest.Value = false;
        machine.MinutesUntilReady = 0;
        machine.showNextIndex.Value = false;
    }
}
