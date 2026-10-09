using MinRepoMobile.Models;
using Microsoft.Maui.Storage;
using System.Text.Json;

namespace MinRepoMobile.Services;

/// <summary>
/// Androidサービスの進捗を画面へ安全に中継し、最後のZIPを再表示できるようにします。
/// </summary>
public sealed class BackgroundExtractionCoordinator
{
    private const string LastZipPathKey = "background_extraction.last_zip_path";
    private const string LastStateKey = "background_extraction.last_state";
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

        // 取得0件の診断ZIPを再起動後に成功結果として表示しないよう、終了状態も保存します。
        try
        {
            var saved = JsonSerializer.Deserialize<BackgroundExtractionState>(
                Preferences.Default.Get(LastStateKey, "null"));
            if (saved?.ZipPath is not null && File.Exists(saved.ZipPath))
            {
                _current = saved;
            }
        }
        catch (JsonException)
        {
            // 旧版の結果パスまたは待機状態へフォールバックします。
        }
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
        Preferences.Default.Remove(LastStateKey);
        Publish(new BackgroundExtractionState(
            BackgroundExtractionStatus.Running,
            "バックグラウンド取得を開始しています。",
            0));
    }

    public void Report(ExtractionProgress progress)
        => Publish(new BackgroundExtractionState(
            BackgroundExtractionStatus.Running,
            progress.Message,
            Math.Clamp(progress.Ratio, 0, 1)), onlyWhileRunning: true);

    public void Complete(ExtractionResult result)
    {
        Preferences.Default.Set(LastZipPathKey, result.ZipPath);
        var message = result.RowCount == 0
            ? $"取得に失敗しました: データ0件 / 警告{result.FailureCount:N0}件。失敗一覧を共有できます。"
            : result.FailureCount == 0
            ? $"完了: {result.ReportCount:N0}日分 / {result.RowCount:N0}台日"
            : $"完了: {result.ReportCount:N0}日分 / {result.RowCount:N0}台日" +
              $" / 警告{result.FailureCount:N0}件";

        var state = new BackgroundExtractionState(
            result.RowCount == 0 ? BackgroundExtractionStatus.Failed : BackgroundExtractionStatus.Completed,
            message,
            1,
            result.ZipPath,
            result.ReportCount,
            result.RowCount,
            result.FailureCount);
        Preferences.Default.Set(LastStateKey, JsonSerializer.Serialize(state));
        Publish(state);
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

    private void Publish(BackgroundExtractionState state, bool onlyWhileRunning = false)
    {
        lock (_gate)
        {
            // 完了・失敗・中止の後に届く進捗は破棄し、終了状態を維持します。
            if (onlyWhileRunning && _current.Status != BackgroundExtractionStatus.Running)
            {
                return;
            }
            _current = state;
        }

        StateChanged?.Invoke(this, state);
    }
}

