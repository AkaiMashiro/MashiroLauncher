using MashiroLauncher.Core.Launching;

namespace MashiroLauncher.Core.Instances;

/// <summary>
/// Per-instance account selection policy. Lets each instance launch with a
/// different identity without the user changing the global active account.
/// </summary>
public enum InstanceAccountMode
{
    /// <summary>Inherit whichever account is currently active in the launcher.</summary>
    Default,

    /// <summary>Force offline mode for this instance, regardless of the active account.</summary>
    Offline,

    /// <summary>Use a specific Microsoft account by UUID (see <see cref="Instance.SpecificAccountId"/>).</summary>
    Specific,
}

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

    // Per-instance account selection. The launch path's ResolveAccount logic
    // turns these three fields into an IAccount according to:
    //   - Specific + SpecificAccountId → that MS account
    //   - Offline                       → OfflineAccount(OfflineUsername ?? global)
    //   - Default                       → global active account, or global offline-mode
    public InstanceAccountMode AccountMode { get; init; } = InstanceAccountMode.Default;

    /// <summary>UUID hex of the Microsoft account to use when <see cref="AccountMode"/> is Specific.</summary>
    public string? SpecificAccountId { get; init; }

    /// <summary>Username to use when <see cref="AccountMode"/> is Offline. Null falls back to the launcher's global username.</summary>
    public string? OfflineUsername { get; init; }
}
