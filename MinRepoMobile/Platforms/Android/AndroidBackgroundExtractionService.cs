using System.Text.Json;
using Android.App;
using Android.Content;
using Android.OS;
using MinRepoMobile.Models;
using MinRepoMobile.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.ApplicationModel;

namespace MinRepoMobile.Platforms.Android;

/// <summary>
/// 画面からAndroidのdataSyncフォアグラウンドサービスを開始・中止します。
/// </summary>
public sealed class AndroidBackgroundExtractionService(
    BackgroundExtractionCoordinator coordinator) : IBackgroundExtractionService
{
    public bool IsRunning =>
        coordinator.Current.Status == BackgroundExtractionStatus.Running;

    public async Task StartAsync(ExtractionRequest request)
    {
        if (IsRunning)
        {
            throw new InvalidOperationException("取得処理は既に実行中です。");
        }

        if (OperatingSystem.IsAndroidVersionAtLeast(33))
        {
            var permission =
                await Permissions.CheckStatusAsync<NotificationPermission>();
            if (permission != PermissionStatus.Granted)
            {
                permission = await Permissions.RequestAsync<NotificationPermission>();
            }

            if (permission != PermissionStatus.Granted)
            {
                throw new InvalidOperationException(
                    "バックグラウンド取得の進捗と完了を表示するため、" +
                    "通知を許可してください。");
            }
        }

        var context = global::Android.App.Application.Context;
        var intent = new Intent(context, typeof(ExtractionForegroundService));
        intent.SetAction(ExtractionForegroundService.StartAction);
        intent.PutExtra(
            ExtractionForegroundService.RequestExtra,
            JsonSerializer.Serialize(request));

        coordinator.Begin();
        try
        {
            context.StartForegroundService(intent);
        }
        catch
        {
            coordinator.Fail("バックグラウンド取得を開始できませんでした。");
            throw;
        }
    }

    public void Cancel()
    {
        if (!IsRunning)
        {
            return;
        }

        var context = global::Android.App.Application.Context;
        var intent = new Intent(context, typeof(ExtractionForegroundService));
        intent.SetAction(ExtractionForegroundService.CancelAction);
        context.StartService(intent);
    }
}

[Service(
    Name = "jp.minrepo.mobileextractor.ExtractionForegroundService",
    Exported = false)]
public sealed class ExtractionForegroundService : Service
{
    public const string StartAction =
        "jp.minrepo.mobileextractor.action.START_EXTRACTION";
    public const string CancelAction =
        "jp.minrepo.mobileextractor.action.CANCEL_EXTRACTION";
    public const string RequestExtra = "extraction_request_json";

    private const string ProgressChannelId = "minrepo_extraction_progress";
    private const string ResultChannelId = "minrepo_extraction_result";
    private const int ProgressNotificationId = 2101;
    private const int ResultNotificationId = 2102;
    private static readonly TimeSpan MaximumRunTime = TimeSpan.FromHours(5.5);

    private CancellationTokenSource? _cancellation;
    private NotificationManager? _notificationManager;
    private PowerManager.WakeLock? _wakeLock;
    private BackgroundExtractionCoordinator? _coordinator;
    private bool _cancelledByUser;
    private volatile bool _isRunning;
    private long _lastNotificationTicks;

    public override void OnCreate()
    {
        base.OnCreate();
        _notificationManager =
            GetSystemService(NotificationService) as NotificationManager;
        var powerManager = GetSystemService(PowerService) as PowerManager;
        _wakeLock = powerManager?.NewWakeLock(
            WakeLockFlags.Partial,
            $"{PackageName}:MinRepoExtraction");
        _wakeLock?.SetReferenceCounted(false);
        CreateNotificationChannels();
        _coordinator = AppServiceProvider.Current?
            .GetRequiredService<BackgroundExtractionCoordinator>();
    }

    public override StartCommandResult OnStartCommand(
        Intent? intent,
        StartCommandFlags flags,
        int startId)
    {
        if (intent?.Action == CancelAction)
        {
            _cancelledByUser = true;
            if (_cancellation is not null)
            {
                _cancellation.Cancel();
            }
            else
            {
                _coordinator?.Cancel();
                ShowResultNotification(
                    "みんレポ取得を中止しました",
                    "取得処理はユーザー操作により中止されました。");
                StopSelf();
            }
            return StartCommandResult.NotSticky;
        }

        // Androidはサービス開始後すぐのStartForegroundを要求します。
        StartForeground(
            ProgressNotificationId,
            BuildProgressNotification("取得条件を準備しています。", 0));

        if (_cancellation is not null || _isRunning)
        {
            return StartCommandResult.NotSticky;
        }

        try
        {
            var requestJson = intent?.GetStringExtra(RequestExtra);
            var request = string.IsNullOrWhiteSpace(requestJson)
                ? null
                : JsonSerializer.Deserialize<ExtractionRequest>(requestJson);
            if (request is null)
            {
                throw new InvalidOperationException("取得条件を読み込めませんでした。");
            }

            _isRunning = true;
            _wakeLock?.Acquire((long)MaximumRunTime.TotalMilliseconds);
            _cancelledByUser = false;
            _ = RunExtractionAsync(request);
        }
        catch (Exception ex)
        {
            FinishWithFailure(ex.Message);
        }

        return StartCommandResult.NotSticky;
    }

    public override global::Android.OS.IBinder? OnBind(Intent? intent) => null;

    public override void OnDestroy()
    {
        _cancellation?.Cancel();
        if (_isRunning)
        {
            _isRunning = false;
            const string message = "Androidにより取得サービスが停止されました。再度取得を実行してください。";
            _coordinator?.Fail(message);
            ShowResultNotification("みんレポ取得に失敗しました", message);
        }
        // CTSの破棄は非同期処理のfinallyで行い、終了前のObjectDisposedExceptionを防ぎます。
        ReleaseWakeLock();
        base.OnDestroy();
    }

    /// <summary>
    /// Android 15以降の累積実行上限に達したら、OSの猶予時間内にサービスを停止します。
    /// 1回5時間30分の自主制限だけでは、OS側の累積上限には対応できません。
    /// </summary>
    public override void OnTimeout(int startId, global::Android.Content.PM.ForegroundService fgsType)
    {
        _cancellation?.Cancel();
        FinishWithFailure("Androidのバックグラウンド実行時間上限に達したため停止しました。");
    }

    private async Task RunExtractionAsync(ExtractionRequest request)
    {
        using var cancellation = new CancellationTokenSource(MaximumRunTime);
        _cancellation = cancellation;

        try
        {
            var services = AppServiceProvider.Current ??
                throw new InvalidOperationException(
                    "アプリの取得サービスを初期化できませんでした。");
            var extractor = services.GetRequiredService<MinRepoExtractionService>();
            _coordinator ??=
                services.GetRequiredService<BackgroundExtractionCoordinator>();

            // HTML解析・CSV圧縮もUIスレッドから離し、取得中の画面操作を妨げません。
            // Progress<T>の非同期キューを使わず、その場で通知して終了後の再通知を防ぎます。
            var progress = new ImmediateProgress(OnProgress);
            var result = await Task.Run(() => extractor.ExtractAsync(
                request, progress, cancellation.Token), cancellation.Token);

            if (!_isRunning) return;

            _coordinator.Complete(result);
            var title = result.RowCount == 0
                ? "みんレポ取得に失敗しました（データ0件）"
                : result.FailureCount == 0
                ? "みんレポ取得が完了しました"
                : "みんレポ取得が完了しました（警告あり）";
            var message =
                $"{result.ReportCount:N0}日分 / {result.RowCount:N0}台日" +
                (result.FailureCount == 0
                    ? string.Empty
                    : $" / 警告{result.FailureCount:N0}件");
            ShowResultNotification(title, message);
        }
        catch (global::System.OperationCanceledException)
        {
            if (!_isRunning) return;
            if (_cancelledByUser)
            {
                _coordinator?.Cancel();
                ShowResultNotification(
                    "みんレポ取得を中止しました",
                    "取得処理はユーザー操作により中止されました。");
            }
            else
            {
                const string message =
                    "Androidの継続実行上限を超えないよう、5時間30分で停止しました。";
                _coordinator?.Fail(message);
                ShowResultNotification("みんレポ取得に失敗しました", message);
            }
        }
        catch (Exception ex)
        {
            if (_isRunning) FinishWithFailure(ex.Message);
            return;
        }
        finally
        {
            _isRunning = false;
            _cancellation = null;
            ReleaseWakeLock();
            StopForeground(StopForegroundFlags.Remove);
            StopSelf();
        }
    }

    private void OnProgress(ExtractionProgress progress)
    {
        if (!_isRunning || _cancellation?.IsCancellationRequested == true) return;
        _coordinator?.Report(progress);

        // 通知の過度な再描画を避けつつ、最後の100%は必ず反映します。
        var now = global::System.Environment.TickCount64;
        if (progress.Ratio < 1 && now - _lastNotificationTicks < 1000)
        {
            return;
        }

        _lastNotificationTicks = now;
        _notificationManager?.Notify(
            ProgressNotificationId,
            BuildProgressNotification(progress.Message, progress.Ratio));
    }

    private void FinishWithFailure(string message)
    {
        _coordinator?.Fail(message);
        ShowResultNotification("みんレポ取得に失敗しました", message);
        _isRunning = false;
        ReleaseWakeLock();
        StopForeground(StopForegroundFlags.Remove);
        StopSelf();
    }

    private sealed class ImmediateProgress(Action<ExtractionProgress> report) : IProgress<ExtractionProgress>
    {
        public void Report(ExtractionProgress value) => report(value);
    }

    private Notification BuildProgressNotification(string message, double ratio)
    {
        var progress = (int)Math.Round(Math.Clamp(ratio, 0, 1) * 100);
        return new Notification.Builder(this, ProgressChannelId)
            .SetContentTitle("みんレポを取得中")
            .SetContentText(message)
            .SetSmallIcon(global::Android.Resource.Drawable.StatSysDownload)
            .SetContentIntent(CreateOpenAppIntent())
            .SetOngoing(true)
            .SetOnlyAlertOnce(true)
            .SetProgress(100, progress, progress == 0)
            .Build();
    }

    private void ShowResultNotification(string title, string message)
    {
        var notification = new Notification.Builder(this, ResultChannelId)
            .SetContentTitle(title)
            .SetContentText(message)
            .SetStyle(new Notification.BigTextStyle().BigText(message))
            .SetSmallIcon(global::Android.Resource.Drawable.StatSysDownloadDone)
            .SetContentIntent(CreateOpenAppIntent())
            .SetAutoCancel(true)
            .Build();

        _notificationManager?.Notify(ResultNotificationId, notification);
    }

    private PendingIntent CreateOpenAppIntent()
    {
        var intent = new Intent(this, typeof(MainActivity));
        intent.AddFlags(ActivityFlags.ClearTop | ActivityFlags.SingleTop);
        return PendingIntent.GetActivity(
            this,
            0,
            intent,
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable)!;
    }

    private void CreateNotificationChannels()
    {
        if (_notificationManager is null)
        {
            return;
        }

        var progressChannel = new NotificationChannel(
            ProgressChannelId,
            "取得中の進捗",
            NotificationImportance.Low)
        {
            Description = "バックグラウンド取得中の進捗を表示します。",
        };
        progressChannel.SetSound(null, null);

        var resultChannel = new NotificationChannel(
            ResultChannelId,
            "取得結果",
            NotificationImportance.Default)
        {
            Description = "取得の完了、失敗、中止を通知します。",
        };

        _notificationManager.CreateNotificationChannel(progressChannel);
        _notificationManager.CreateNotificationChannel(resultChannel);
    }

    private void ReleaseWakeLock()
    {
        if (_wakeLock?.IsHeld == true)
        {
            _wakeLock.Release();
        }
    }
}
