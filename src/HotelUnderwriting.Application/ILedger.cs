namespace HotelUnderwriting.Application;

/// <summary>Append-only audit ledger (AD-10). There is deliberately no update or delete.</summary>
public interface ILedger
{
    void Append(LedgerEntry entry);
}
