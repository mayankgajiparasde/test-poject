namespace HotelUnderwriting.Application;

/// <summary>The resources that belong to deals (AD-16).</summary>
public enum DealResource { Set, Run, Job }

/// <summary>Which deals may see a resource. Kept apart from the resources so content-addressed ids and run keys are unchanged.</summary>
public interface IDealAccess
{
    /// <summary>Adds the deals to the resource as one unit; granting again is harmless. Grants are never removed.</summary>
    void Grant(DealResource kind, string resourceId, IReadOnlyCollection<string> dealIds);

    /// <summary>The deals the resource is granted to; empty when none (or the resource is unknown).</summary>
    IReadOnlyCollection<string> DealsOf(DealResource kind, string resourceId);
}
