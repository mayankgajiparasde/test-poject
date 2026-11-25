namespace HotelUnderwriting.Application;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}
