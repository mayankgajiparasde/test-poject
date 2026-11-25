namespace HotelUnderwriting.Application;

public sealed class NullLedger : ILedger
{
    public void Append(LedgerEntry entry) { }
}
