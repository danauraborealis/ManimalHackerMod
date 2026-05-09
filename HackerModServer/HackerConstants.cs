namespace HackerMod;

/// <summary>
/// Shared identifiers for the Hacker Device. Mirrored client-side in
/// <c>HackerModClient/HackerConstants.cs</c> — keep both in sync when
/// the template id ever changes.
/// </summary>
public static class HackerConstants
{
    /// <summary>
    /// Template id of the custom Hacker Device item, defined in
    /// <c>ServerModFiles/db/CustomItems/HackerDevice.json</c>. Generated
    /// once at scaffold time; treat as immutable — every save with a
    /// device in inventory keys off this string.
    /// </summary>
    public const string HackerDeviceTpl = "69f95efeb93f3b8fa8d3879f";
}
