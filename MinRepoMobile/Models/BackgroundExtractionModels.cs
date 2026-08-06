namespace MinRepoMobile.Models;

/// <summary>
/// Androidのバックグラウンド取得状態。画面とフォアグラウンドサービスで共有します。
/// </summary>
public enum BackgroundExtractionStatus
{
    Idle,
    Running,
    Completed,
    Failed,
    Cancelled,
}

public sealed record BackgroundExtractionState(
    BackgroundExtractionStatus Status,
    string Message,
    double Progress,
    string? ZipPath = null,
    int ReportCount = 0,
    int RowCount = 0,
    int FailureCount = 0)
{
    public static BackgroundExtractionState Idle { get; } = new(
        BackgroundExtractionStatus.Idle,
        "待機中",
        0);
}

