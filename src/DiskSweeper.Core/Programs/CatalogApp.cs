namespace DiskSweeper.Core.Programs;

public enum AppSource
{
    Registry,
    Steam,
    Epic,
    Gog,
    Roblox,
    MicrosoftStore,
}

/// <summary>One installed program or game, merged from the registry and from any launcher that also lists it.</summary>
public sealed record CatalogApp
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public string? Publisher { get; init; }

    public string? Version { get; init; }

    public required AppSource Source { get; init; }

    public string? InstallLocation { get; init; }

    /// <summary>Folders that belong to this app alone, such as a Steam shader cache or an old Roblox version.</summary>
    public IReadOnlyList<string> OwnedPaths { get; init; } = [];

    /// <summary>Size reported by the registry (EstimatedSize) or a launcher manifest. Not measured.</summary>
    public long? ListedSizeBytes { get; init; }

    public string? UninstallString { get; init; }

    public string? QuietUninstallString { get; init; }

    /// <summary>True when the uninstall entry is under HKLM, so removing files often needs administrator rights.</summary>
    public bool MachineWide { get; init; }

    /// <summary>Uninstall registry key, for example HKLM\SOFTWARE\...\Uninstall\Fortnite.</summary>
    public string? UninstallKey { get; init; }

    public string? SteamAppId { get; init; }

    public string? SteamExe { get; init; }

    public string? StorePackageFullName { get; init; }

    public string? StoreFamilyName { get; init; }

    public string SourceLabel => Source switch
    {
        AppSource.Steam => "Steam",
        AppSource.Epic => "Epic Games",
        AppSource.Gog => "GOG",
        AppSource.Roblox => "Roblox",
        AppSource.MicrosoftStore => "Microsoft Store",
        _ => "Installed program",
    };
}

public enum RemovalKind
{
    InstallFolder,
    LeftoverFolder,
    Shortcut,
    RegistryKey,
}

/// <summary>One file, folder, shortcut or registry key a removal would touch.</summary>
public sealed record RemovalItem
{
    public required RemovalKind Kind { get; init; }

    public required string Title { get; init; }

    public required string Location { get; init; }

    /// <summary>Why this was included, in plain words.</summary>
    public required string Reason { get; init; }

    public bool SelectedByDefault { get; init; }

    /// <summary>Set when the item matched but must not be removed. Shown unticked and disabled.</summary>
    public string? Refusal { get; init; }

    public long SizeBytes { get; init; }
}

/// <summary>A shortcut found on the desktop or Start menu, with the target path when it could be read.</summary>
public sealed record ShortcutHint(string Path, string? Target);

public sealed class RemovalPlan
{
    public required IReadOnlyList<RemovalItem> Items { get; init; }

    /// <summary>Shown when the name is too generic for a leftover search, or when something else is worth knowing.</summary>
    public string? Note { get; init; }
}
