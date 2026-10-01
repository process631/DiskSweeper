namespace DiskSweeper.Core.Deletion;

public enum DeleteMethod
{
    /// <summary>Move to the Recycle Bin so it can be restored. Space is only freed once the bin is emptied.</summary>
    RecycleBin,

    /// <summary>Delete for good, using every trick for stubborn files (long paths, read-only flags, odd names).</summary>
    Permanent,
}

public enum DeleteOutcome
{
    Deleted,
    Recycled,
    AlreadyGone,
    WouldDelete,
    ScheduledForRestart,
    Refused,
    Failed,
}

/// <summary>Why something couldn't be deleted. Each reason has a matching fix in <see cref="DeleteService"/>.</summary>
public enum BlockReason
{
    None,

    /// <summary>On the protected list. There is deliberately no fix for this.</summary>
    Protected,

    /// <summary>A running program has it open. Fix: close that program, or delete on next restart.</summary>
    InUse,

    /// <summary>Windows says we don't have permission. Fix: take ownership (needs admin).</summary>
    AccessDenied,

    /// <summary>The Recycle Bin can't take this path (too long or an odd name). Fix: delete permanently.</summary>
    RecycleBinCantTakeIt,

    /// <summary>The Recycle Bin operation failed or was cancelled at Windows' "too big for the bin" prompt.</summary>
    RecycleBinFailed,

    Other,
}

public sealed record FailedItem(string Path, BlockReason Reason, string Message);

public sealed class DeleteResult
{
    public required string Path { get; init; }

    public required DeleteOutcome Outcome { get; init; }

    public BlockReason Reason { get; init; }

    /// <summary>Bytes removed from disk (or moved to the bin, or that would be removed in a dry run).</summary>
    public long Bytes { get; init; }

    /// <summary>Plain-language explanation for anything other than a clean success.</summary>
    public string? Message { get; init; }

    /// <summary>Files or folders inside that couldn't be removed (capped, see <see cref="FailedCount"/>).</summary>
    public IReadOnlyList<FailedItem> FailedItems { get; init; } = [];

    public int FailedCount { get; init; }

    public IReadOnlyList<LockingProcess> LockedBy { get; init; } = [];

    public bool Succeeded => Outcome is DeleteOutcome.Deleted or DeleteOutcome.Recycled or DeleteOutcome.AlreadyGone
        or DeleteOutcome.WouldDelete or DeleteOutcome.ScheduledForRestart;

    /// <summary>The fix for this failure needs administrator rights that this process doesn't have.</summary>
    public bool NeedsAdmin => !Elevation.IsElevated && Reason is BlockReason.AccessDenied;

    public override string ToString() => $"{Outcome} {Path}" + (Message is null ? "" : $": {Message}");
}
