using MinRepoMobile.Models;
using Microsoft.Maui.Storage;

namespace MinRepoMobile.Services;

/// <summary>
/// Androidサービスの進捗を画面へ安全に中継し、最後のZIPを再表示できるようにします。
/// </summary>
public sealed class BackgroundExtractionCoordinator
{
    private const string LastZipPathKey = "background_extraction.last_zip_path";
    private readonly object _gate = new();
    private BackgroundExtractionState _current;

    public BackgroundExtractionCoordinator()
    {
        var lastZipPath = Preferences.Default.Get(LastZipPathKey, string.Empty);
        _current = !string.IsNullOrWhiteSpace(lastZipPath) && File.Exists(lastZipPath)
            ? new BackgroundExtractionState(
                BackgroundExtractionStatus.Completed,
                "前回の取得結果を共有できます。",
                1,
                lastZipPath)
            : BackgroundExtractionState.Idle;
    }

    public event EventHandler<BackgroundExtractionState>? StateChanged;

    public BackgroundExtractionState Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public void Begin()
    {
        Preferences.Default.Remove(LastZipPathKey);
        Publish(new BackgroundExtractionState(
            BackgroundExtractionStatus.Running,
            "バックグラウンド取得を開始しています。",
            0));
    }

    public void Report(ExtractionProgress progress)
        => Publish(new BackgroundExtractionState(
            BackgroundExtractionStatus.Running,
            progress.Message,
            Math.Clamp(progress.Ratio, 0, 1)));

    public void Complete(ExtractionResult result)
    {
        Preferences.Default.Set(LastZipPathKey, result.ZipPath);
        var message = result.FailureCount == 0
            ? $"完了: {result.ReportCount:N0}日分 / {result.RowCount:N0}台日"
            : $"完了: {result.ReportCount:N0}日分 / {result.RowCount:N0}台日" +
              $" / 警告{result.FailureCount:N0}件";

        Publish(new BackgroundExtractionState(
            BackgroundExtractionStatus.Completed,
            message,
            1,
            result.ZipPath,
            result.ReportCount,
            result.RowCount,
            result.FailureCount));
    }

    public void Fail(string message)
        => Publish(new BackgroundExtractionState(
            BackgroundExtractionStatus.Failed,
            message,
            Current.Progress));

    public void Cancel()
        => Publish(new BackgroundExtractionState(
            BackgroundExtractionStatus.Cancelled,
            "取得を中止しました。",
            Current.Progress));

    private void Publish(BackgroundExtractionState state)
    {
        lock (_gate)
        {
            _current = state;
        }

        StateChanged?.Invoke(this, state);
    }
}

