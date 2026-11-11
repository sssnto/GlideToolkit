namespace Jellyfin.Plugin.GlideToolkit.Core;

public sealed record RotationCapability(int ProtocolVersion, string PluginVersion, bool Available, string? Reason,
    string? ItemId = null, string? MediaSourceId = null, string? FileName = null, string? Version = null,
    long? FileBytes = null, string[]? SupportedExtensions = null);
public sealed record RotationRequest(Guid RequestId, string ItemId, string MediaSourceId, string ExpectedVersion, int QuarterTurns, Guid? RestoreJobId = null);
public sealed record JobStatus(Guid JobId, Guid RequestId, string ItemId, string MediaSourceId, string State, string Stage,
    int Progress, string? ErrorCode, bool CanRestore, bool RefreshPending, DateTime UpdatedAt, string PublicationMode, string? FailureCode);

// Server-only journal. Never returned to clients or logged: contains filesystem paths.
public sealed class RotationJob
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public RotationRequest Request { get; set; } = null!;
    public string Path { get; set; } = "";
    public string Root { get; set; } = "";
    public string StageFile { get; set; } = "";
    public string BackupFile { get; set; } = "";
    public string State { get; set; } = "queued";
    public string Stage { get; set; } = "queued";
    public int Progress { get; set; }
    public string? ErrorCode { get; set; }
    public string? FailureCode { get; set; }
    public string? OriginalDigest { get; set; }
    public string? SavedDigest { get; set; }
    public bool BackupMetadataPreserved { get; set; }
    public PublicationMode PublicationMode { get; set; }
    public bool RefreshPending { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public JobStatus Status() => new(Id, Request.RequestId, Request.ItemId, Request.MediaSourceId, State, Stage,
        Progress, ErrorCode, State == "completed" && File.Exists(BackupFile), RefreshPending, UpdatedAt, PublicationMode.ToString(), FailureCode);
}
public sealed record RecoveryStatus(Guid JobId, string State, string? ErrorCode, string? FailureCode,
    bool OriginalExists, bool BackupExists, bool CanRecoverMissing, bool OriginalRestored);
public sealed class RotationException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
