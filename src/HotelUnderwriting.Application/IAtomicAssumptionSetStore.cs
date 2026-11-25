namespace HotelUnderwriting.Application;

/// <summary>An assumption-set store that can write the state and its ledger row as one unit: both are stored or neither (the call throws).</summary>
public interface IAtomicAssumptionSetStore : IAssumptionSetStore
{
    /// <summary>Like <see cref="IAssumptionSetStore.SaveIfAbsent"/> plus exactly one ledger row.</summary>
    AssumptionSetRecord SaveIfAbsent(AssumptionSetRecord record, LedgerEntry ledgerEntry);
}
