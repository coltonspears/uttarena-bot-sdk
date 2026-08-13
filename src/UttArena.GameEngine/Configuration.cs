namespace UttArena.GameEngine;

public interface IVersionedConfiguration
{
    string Name { get; }
    int Version { get; }
}

public sealed record Ruleset : IVersionedConfiguration
{
    public Ruleset(string name, int version)
    {
        Name = ValidateName(name);
        Version = ValidateVersion(version);
    }

    public string Name { get; }
    public int Version { get; }

    public static Ruleset Standard { get; } = new("ultimate-tic-tac-toe", 1);

    private static string ValidateName(string name) =>
        string.IsNullOrWhiteSpace(name)
            ? throw new ArgumentException("A configuration name is required.", nameof(name))
            : name;

    private static int ValidateVersion(int version) =>
        version > 0
            ? version
            : throw new ArgumentOutOfRangeException(nameof(version), "Version must be positive.");
}

public sealed record TimeControl : IVersionedConfiguration
{
    public TimeControl(
        string name,
        int version,
        TimeSpan initialTime,
        TimeSpan increment,
        TimeSpan? moveLimit = null)
    {
        Name = ValidateName(name);
        Version = ValidateVersion(version);
        InitialTime = NonNegative(initialTime, nameof(initialTime));
        Increment = NonNegative(increment, nameof(increment));
        MoveLimit = moveLimit is null ? null : NonNegative(moveLimit.Value, nameof(moveLimit));
    }

    public string Name { get; }
    public int Version { get; }
    public TimeSpan InitialTime { get; }
    public TimeSpan Increment { get; }
    public TimeSpan? MoveLimit { get; }

    public static TimeControl Untimed { get; } =
        new("untimed", 1, TimeSpan.Zero, TimeSpan.Zero);

    private static TimeSpan NonNegative(TimeSpan value, string name) =>
        value >= TimeSpan.Zero
            ? value
            : throw new ArgumentOutOfRangeException(name, "Duration cannot be negative.");

    private static string ValidateName(string name) =>
        string.IsNullOrWhiteSpace(name)
            ? throw new ArgumentException("A configuration name is required.", nameof(name))
            : name;

    private static int ValidateVersion(int version) =>
        version > 0 ? version : throw new ArgumentOutOfRangeException(nameof(version));
}

public sealed record FailurePolicy : IVersionedConfiguration
{
    public FailurePolicy(
        string name,
        int version,
        bool forfeitOnIllegalMove,
        bool forfeitOnTimeout,
        int maximumFailures)
    {
        Name = ValidateName(name);
        Version = ValidateVersion(version);
        ForfeitOnIllegalMove = forfeitOnIllegalMove;
        ForfeitOnTimeout = forfeitOnTimeout;
        MaximumFailures = maximumFailures >= 0
            ? maximumFailures
            : throw new ArgumentOutOfRangeException(nameof(maximumFailures));
    }

    public string Name { get; }
    public int Version { get; }
    public bool ForfeitOnIllegalMove { get; }
    public bool ForfeitOnTimeout { get; }
    public int MaximumFailures { get; }

    public static FailurePolicy Strict { get; } = new("strict", 1, true, true, 0);

    private static string ValidateName(string name) =>
        string.IsNullOrWhiteSpace(name)
            ? throw new ArgumentException("A configuration name is required.", nameof(name))
            : name;

    private static int ValidateVersion(int version) =>
        version > 0 ? version : throw new ArgumentOutOfRangeException(nameof(version));
}

public sealed record ResourceProfile : IVersionedConfiguration
{
    public ResourceProfile(
        string name,
        int version,
        int maximumMemoryMegabytes,
        int maximumProcessorCount)
    {
        Name = ValidateName(name);
        Version = ValidateVersion(version);
        MaximumMemoryMegabytes = maximumMemoryMegabytes > 0
            ? maximumMemoryMegabytes
            : throw new ArgumentOutOfRangeException(nameof(maximumMemoryMegabytes));
        MaximumProcessorCount = maximumProcessorCount > 0
            ? maximumProcessorCount
            : throw new ArgumentOutOfRangeException(nameof(maximumProcessorCount));
    }

    public string Name { get; }
    public int Version { get; }
    public int MaximumMemoryMegabytes { get; }
    public int MaximumProcessorCount { get; }

    public static ResourceProfile Default { get; } = new("default", 1, 256, 1);

    private static string ValidateName(string name) =>
        string.IsNullOrWhiteSpace(name)
            ? throw new ArgumentException("A configuration name is required.", nameof(name))
            : name;

    private static int ValidateVersion(int version) =>
        version > 0 ? version : throw new ArgumentOutOfRangeException(nameof(version));
}
