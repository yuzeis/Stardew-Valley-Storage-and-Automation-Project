using SVSAP.Models;

namespace SVSAP.Services;

internal static class ExecutedRemoteActionLedger
{
    public const int MaxEntriesPerKind = 4096;

    public static bool TryGetTerminal(
        IReadOnlyList<ExecutedTerminalDeposit> entries,
        long playerId,
        Guid transactionId,
        out ExecutedTerminalDeposit entry)
    {
        entry = entries.FirstOrDefault(candidate =>
            candidate.PlayerId == playerId && candidate.TransactionId == transactionId)!;
        return entry is not null;
    }

    public static bool TryGetStructural(
        IReadOnlyList<ExecutedStructuralConsumption> entries,
        long playerId,
        Guid transactionId,
        out ExecutedStructuralConsumption entry)
    {
        entry = entries.FirstOrDefault(candidate =>
            candidate.PlayerId == playerId && candidate.TransactionId == transactionId)!;
        return entry is not null;
    }

    public static bool RememberTerminal(List<ExecutedTerminalDeposit> entries, ExecutedTerminalDeposit entry)
    {
        if (!IsValid(entry) || TryGetTerminal(entries, entry.PlayerId, entry.TransactionId, out _))
            return false;

        entries.Add(entry);
        Trim(entries);
        return true;
    }

    public static bool RememberStructural(List<ExecutedStructuralConsumption> entries, ExecutedStructuralConsumption entry)
    {
        if (!IsValid(entry) || TryGetStructural(entries, entry.PlayerId, entry.TransactionId, out _))
            return false;

        entries.Add(entry);
        Trim(entries);
        return true;
    }

    public static bool NormalizeTerminal(List<ExecutedTerminalDeposit> entries)
    {
        var changed = false;
        var seen = new HashSet<(long PlayerId, Guid TransactionId)>();
        for (var index = 0; index < entries.Count;)
        {
            var entry = entries[index];
            if (!IsValid(entry) || !seen.Add((entry.PlayerId, entry.TransactionId)))
            {
                entries.RemoveAt(index);
                changed = true;
                continue;
            }

            entry.ActionKind ??= string.Empty;
            entry.Message ??= string.Empty;
            entry.ReturnedDepositItems ??= new();
            index++;
        }

        return Trim(entries) || changed;
    }

    public static bool NormalizeStructural(List<ExecutedStructuralConsumption> entries)
    {
        var changed = false;
        var seen = new HashSet<(long PlayerId, Guid TransactionId)>();
        for (var index = 0; index < entries.Count;)
        {
            var entry = entries[index];
            if (!IsValid(entry) || !seen.Add((entry.PlayerId, entry.TransactionId)))
            {
                entries.RemoveAt(index);
                changed = true;
                continue;
            }

            entry.LocationName ??= string.Empty;
            entry.ActionKind ??= string.Empty;
            entry.Message ??= string.Empty;
            index++;
        }

        return Trim(entries) || changed;
    }

    private static bool IsValid(ExecutedTerminalDeposit? entry)
    {
        return entry is not null
            && entry.PlayerId > 0
            && entry.TransactionId != Guid.Empty
            && entry.NetworkId != Guid.Empty
            && entry.EndpointId != Guid.Empty
            && !string.IsNullOrWhiteSpace(entry.ActionKind);
    }

    private static bool IsValid(ExecutedStructuralConsumption? entry)
    {
        return entry is not null
            && entry.PlayerId > 0
            && entry.TransactionId != Guid.Empty
            && !string.IsNullOrWhiteSpace(entry.LocationName)
            && !string.IsNullOrWhiteSpace(entry.ActionKind);
    }

    private static bool Trim<T>(List<T> entries)
    {
        var changed = false;
        while (entries.Count > MaxEntriesPerKind)
        {
            entries.RemoveAt(0);
            changed = true;
        }

        return changed;
    }
}
