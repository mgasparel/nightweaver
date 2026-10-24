namespace Nightweaver.Configuration;

internal readonly record struct Optional<T>(bool IsPresent, T Value)
{
    internal static Optional<T> Absent => default;

    internal static Optional<T> Of(T value) => new(true, value);

    internal T Or(T fallback) => IsPresent ? Value : fallback;
}
