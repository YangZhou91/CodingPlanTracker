namespace PlanMeter.Core.Models;

/// <summary>
/// AllWindows is the tooltip stack (DATA-02): one row per <see cref="WindowKind"/>,
/// never one row per upstream model/meter that happens to share a kind.
/// </summary>
public static class WindowReadings
{
    /// <summary>
    /// Keep the most-binding window of each <see cref="WindowKind"/> (lowest
    /// <see cref="WindowReading.RemainingPct"/>; original order breaks ties).
    /// Result is ordered Rolling → FiveHour → Weekly → Monthly → Credits.
    /// </summary>
    public static IReadOnlyList<WindowReading> DistinctMostBindingByKind(
        IReadOnlyList<WindowReading>? windows)
    {
        if (windows is not { Count: > 0 })
        {
            return windows ?? Array.Empty<WindowReading>();
        }

        return windows
            .GroupBy(w => w.Kind)
            .Select(g => g.OrderBy(w => w.RemainingPct).First())
            .OrderBy(w => DisplayOrder(w.Kind))
            .ToList();
    }

    private static int DisplayOrder(WindowKind kind) => kind switch
    {
        WindowKind.Rolling => 0,
        WindowKind.FiveHour => 1,
        WindowKind.Weekly => 2,
        WindowKind.Monthly => 3,
        WindowKind.Credits => 4,
        _ => 5,
    };
}
