namespace ShyFtp.Sync;

public enum SyncState
{
    UploadNew,
    UploadChanged,
    DownloadNew,
    DownloadChanged,
    Different,
    Unchanged
}

public sealed class SyncItem
{
    public string RelativePath { get; set; } = "";
    public SyncState State { get; set; }

    public bool LocalExists { get; set; }
    public bool RemoteExists { get; set; }

    public long LocalSize { get; set; }
    public DateTime LocalTime { get; set; }

    public long RemoteSize { get; set; }
    public DateTime RemoteTime { get; set; }

    public bool Selected { get; set; }

    public string? LocalFullPath { get; set; }
    public string? RemoteFullPath { get; set; }

    public bool IsUploadCandidate => LocalExists;
    public bool IsDownloadCandidate => RemoteExists;

    public string SuggestedAction => State switch
    {
        SyncState.UploadNew => "upload (new)",
        SyncState.UploadChanged => "upload (changed)",
        SyncState.DownloadNew => "download (new)",
        SyncState.DownloadChanged => "download (changed)",
        SyncState.Different => "review",
        _ => "unchanged"
    };
}