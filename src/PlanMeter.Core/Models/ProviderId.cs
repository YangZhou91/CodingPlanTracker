namespace PlanMeter.Core.Models;

/// <summary>
/// The PRIMARY identity of a provider — the key of the keyed <c>UsageStore</c>, the
/// <c>ProviderRegistry</c> lookup, and each <c>ProviderPoller</c> slot (assumption-delta
/// "promote"). A <c>readonly record struct</c>: type-safe, value-equatable,
/// dictionary-keyable, and implicitly convertible from a string literal
/// (<c>"zai"</c>, <c>"demo"</c>) so call sites stay terse.
/// </summary>
/// <remarks>
/// The <c>Provider</c> string on <c>UsageReading</c> is NOT the identity — it stays as
/// display metadata (the human-readable name, e.g. "Z.ai"). The id/display-name split
/// lives on <see cref="PlanMeter.Core.Adapters.IProviderAdapter"/> (Id vs DisplayName);
/// this value type is the machine key.
/// </remarks>
public readonly record struct ProviderId(string Value)
{
    /// <summary>
    /// Terse call sites: <c>store.Update("zai", reading)</c> instead of
    /// <c>store.Update(new ProviderId("zai"), reading)</c>.
    /// </summary>
    public static implicit operator ProviderId(string value) => new(value);

    public override string ToString() => Value;
}
