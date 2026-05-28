using Launcher.Core.Launching;

namespace Launcher.Core.Instances;

/// <summary>
/// A user-managed Minecraft profile. Has a stable <see cref="Id"/> used as the
/// folder name under data/instances/{id}/game, and a display <see cref="Name"/>
/// the user can freely rename without disturbing the on-disk layout.
/// </summary>
public sealed record Instance
{
    /// <summary>Folder-safe stable identifier. Never changes after creation.</summary>
    public required string Id { get; init; }

    /// <summary>User-facing display name. Editable.</summary>
    public required string Name { get; init; }

    /// <summary>Minecraft version id, e.g. "26.1.2".</summary>
    public required string VersionId { get; init; }

    public required Modloader Modloader { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? LastPlayedAt { get; init; }

    // Per-instance JVM overrides. Null = inherit from Settings → 고급. These
    // live on the instance so a heavy modpack can request 8 GB while a vanilla
    // throwaway instance stays on the global 2 GB default.
    public int? MinMemoryMb { get; init; }
    public int? MaxMemoryMb { get; init; }
    public string? CustomJvmArgs { get; init; }
}
