namespace HotelUnderwriting.Application;

public sealed class SystemClock : IClock
{
    // PostgreSQL timestamptz keeps microseconds, so drop the last tick digit to make stored and returned values equal.
    public DateTimeOffset UtcNow { get { var t = DateTimeOffset.UtcNow; return t.AddTicks(-(t.Ticks % 10)); } }
}
