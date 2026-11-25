namespace HotelUnderwriting.Application;

/// <summary>A run store that can write the state and its ledger row as one unit: both are stored or neither (the call throws).</summary>
public interface IAtomicRunStore : IRunStore
{
    /// <summary>Like <see cref="IRunStore.SaveIfAbsent"/> plus exactly one ledger row. <paramref name="ledgerEntry"/> receives true when this call stored the record.</summary>
    RunRecord SaveIfAbsent(RunRecord record, Func<bool, LedgerEntry> ledgerEntry);
}
