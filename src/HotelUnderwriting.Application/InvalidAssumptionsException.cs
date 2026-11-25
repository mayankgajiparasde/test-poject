namespace HotelUnderwriting.Application;

/// <summary>The assumptions are not acceptable (engine validation or a missing or invalid value). The only exception the API maps to 422.</summary>
public sealed class InvalidAssumptionsException(string message, Exception? inner = null) : ArgumentException(message, inner)
{
    public const string NotFinite = "The assumptions produce a number that is too large or not finite.";

    // "Line CODE: unknown or not-yet-computed driver 'DRIVER'." quotes client text: keep the line code, drop the driver
    private static string NoDriver(string m)
    {
        const string phrase = "unknown or not-yet-computed driver";
        var i = m.IndexOf(phrase, StringComparison.Ordinal);
        return i < 0 ? m : m[..(i + phrase.Length)] + ".";
    }

    /// <summary>What an exception thrown by the engine means for the caller; null when it is not about the assumptions.</summary>
    internal static InvalidAssumptionsException? FromEngine(Exception e) => e switch
    {
        // fixed texts: these messages would quote an input or say nothing useful
        KeyNotFoundException => new("A percent-of-revenue item refers to an unknown base.", e),
        ArithmeticException => new(NotFinite, e),
        ArgumentException or InvalidOperationException => new(NoDriver(e.Message), e), // validation, year coverage, vector lengths, unknown drivers
        _ => null,
    };
}
