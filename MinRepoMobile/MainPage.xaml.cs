using MinRepoMobile.Models;
using MinRepoMobile.Services;
using Microsoft.Maui.ApplicationModel.DataTransfer;
using Microsoft.Maui.Devices;
using Microsoft.Maui.Networking;

namespace MinRepoMobile;

public partial class MainPage : ContentPage
{
    private readonly MinRepoExtractionService _extractor;
    private CancellationTokenSource? _cancellation;
    private string? _latestZipPath;
    private DateOnly _selectedFromDate;
    private DateOnly _selectedToDate;

    public MainPage(MinRepoExtractionService extractor)
    {
        InitializeComponent();
        _extractor = extractor;

        // 初期期間は直近30日とし、スマホで過度な件数を取得しない設定にします。
        _selectedToDate = DateOnly.FromDateTime(DateTime.Today);
        _selectedFromDate = _selectedToDate.AddDays(-30);
        ToDatePicker.Date = _selectedToDate.ToDateTime(TimeOnly.MinValue);
        FromDatePicker.Date = _selectedFromDate.ToDateTime(TimeOnly.MinValue);
        UpdateDateButtonText();
    }

    /// <summary>
    /// Androidの標準日付ダイアログを明示的に開きます。
    /// DatePicker本体だけでなくボタンからも開けるため、タップ領域が分かりやすくなります。
    /// </summary>
    private void OnOpenFromDateClicked(object? sender, EventArgs e)
        => FromDatePicker.Focus();

    private void OnOpenToDateClicked(object? sender, EventArgs e)
        => ToDatePicker.Focus();

    /// <summary>
    /// 日付ダイアログで確定された値を内部変数へ保存します。
    /// 実行時はこの値を使い、表示だけ変わって取得条件へ反映されない状態を防ぎます。
    /// </summary>
    private void OnDateSelected(object? sender, DateChangedEventArgs e)
    {
        if (ReferenceEquals(sender, FromDatePicker) &&
            FromDatePicker.Date is DateTime fromDate)
        {
            _selectedFromDate = DateOnly.FromDateTime(fromDate);
        }
        else if (ReferenceEquals(sender, ToDatePicker) &&
                 ToDatePicker.Date is DateTime toDate)
        {
            _selectedToDate = DateOnly.FromDateTime(toDate);
        }

        UpdateDateButtonText();
    }

    private void UpdateDateButtonText()
    {
        FromDateButton.Text = $"開始日: {_selectedFromDate:yyyy-MM-dd}";
        ToDateButton.Text = $"終了日: {_selectedToDate:yyyy-MM-dd}";
    }

    private void OnModeChanged(object? sender, CheckedChangedEventArgs e)
    {
        if (!e.Value)
        {
            return;
        }

        var storeMode = StoreModeRadio.IsChecked;
        PeriodPanel.IsVisible = storeMode;
        UrlLabel.Text = storeMode
            ? "店舗の /tag/ を含むURL"
            : "日別レポートURL";
        UrlEntry.Placeholder = storeMode
            ? "https://min-repo.com/tag/店舗名/"
            : "https://min-repo.com/1234567/";
    }

    private async void OnStartClicked(object? sender, EventArgs e)
    {
        if (!TryCreateRequest(out var request, out var validationMessage))
        {
            await DisplayAlertAsync("入力確認", validationMessage, "OK");
            return;
        }

        if (Connectivity.Current.NetworkAccess != NetworkAccess.Internet)
        {
            await DisplayAlertAsync(
                "通信できません",
                "Wi-Fiまたはモバイルデータ通信を確認してください。",
                "OK");
            return;
        }

        if (request!.IsStoreMode &&
            (request.MaxReports > 7 ||
             request.ToDate!.Value.DayNumber - request.FromDate!.Value.DayNumber > 7))
        {
            var continueExecution = await DisplayAlertAsync(
                "長期間の取得には時間がかかります",
                "マイナス差枚の復元やBB・RB取得では、機種別ページも順番に取得します。" +
                "1週間を超える場合は数十分かかる可能性があります。続行しますか？",
                "実行",
                "戻る");
            if (!continueExecution)
            {
                return;
            }
        }

        await RunExtractionAsync(
            cancellationToken => _extractor.ExtractAsync(
                request!,
                new Progress<ExtractionProgress>(UpdateProgress),
                cancellationToken));
    }

    private async void OnSelfTestClicked(object? sender, EventArgs e)
    {
        await RunExtractionAsync(
            cancellationToken => _extractor.RunSelfTestAsync(
                new Progress<ExtractionProgress>(UpdateProgress),
                cancellationToken));
    }

    private async Task RunExtractionAsync(
        Func<CancellationToken, Task<ExtractionResult>> action)
    {
        _cancellation?.Dispose();
        _cancellation = new CancellationTokenSource();
        _latestZipPath = null;
        ShareButton.IsEnabled = false;
        LogEditor.Text = string.Empty;
        ProgressBar.Progress = 0;
        SetRunningState(true);

        // Androidが画面消灯で処理を止めないよう、実行中だけ画面点灯を維持します。
        DeviceDisplay.Current.KeepScreenOn = true;

        try
        {
            var result = await action(_cancellation.Token);
            _latestZipPath = result.ZipPath;
            ShareButton.IsEnabled = true;
            ProgressBar.Progress = 1;
            StatusLabel.Text = result.FailureCount == 0
                ? $"完了: {result.ReportCount:N0}日分 / {result.RowCount:N0}台日"
                : $"完了: {result.ReportCount:N0}日分 / {result.RowCount:N0}台日" +
                  $" / 警告{result.FailureCount:N0}件";
            AppendLog($"結果: {Path.GetFileName(result.ZipPath)}");
            if (result.FailureCount > 0)
            {
                AppendLog(
                    $"00_取得失敗一覧.csvと取得情報.jsonに" +
                    $"警告{result.FailureCount:N0}件を記録しました。");
            }
        }
        catch (OperationCanceledException)
        {
            StatusLabel.Text = "中止しました";
            AppendLog("ユーザー操作により処理を中止しました。");
        }
        catch (Exception ex)
        {
            StatusLabel.Text = "エラー";
            AppendLog(ex.Message);
            await DisplayAlertAsync("処理できませんでした", ex.Message, "OK");
        }
        finally
        {
            DeviceDisplay.Current.KeepScreenOn = false;
            SetRunningState(false);
        }
    }

    private void OnCancelClicked(object? sender, EventArgs e)
        => _cancellation?.Cancel();

    private async void OnShareClicked(object? sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_latestZipPath) || !File.Exists(_latestZipPath))
        {
            await DisplayAlertAsync("結果なし", "先に取得または動作テストを実行してください。", "OK");
            return;
        }

        // Androidの共有画面からFiles、Google Drive等を選択して保存できます。
        await Share.Default.RequestAsync(new ShareFileRequest
        {
            Title = "みんレポ抽出結果を保存",
            File = new ShareFile(_latestZipPath, "application/zip"),
        });
    }

    private bool TryCreateRequest(
        out ExtractionRequest? request,
        out string validationMessage)
    {
        request = null;
        validationMessage = string.Empty;

        if (string.IsNullOrWhiteSpace(UrlEntry.Text))
        {
            validationMessage = "みんレポのURLを入力してください。";
            return false;
        }

        if (!int.TryParse(MaxReportsEntry.Text, out var maxReports))
        {
            maxReports = 31;
        }

        if (maxReports is < 1 or > 100)
        {
            validationMessage = "最大レポート数は1～100で指定してください。";
            return false;
        }

        // DateSelectedで確定済みの値を使い、Androidの選択結果を確実に反映します。
        var fromDate = _selectedFromDate;
        var toDate = _selectedToDate;
        if (StoreModeRadio.IsChecked && fromDate > toDate)
        {
            validationMessage = "開始日は終了日以前にしてください。";
            return false;
        }

        var periods = AggregationPeriods.None;
        periods |= DayCheckBox.IsChecked ? AggregationPeriods.Day : AggregationPeriods.None;
        periods |= WeekCheckBox.IsChecked ? AggregationPeriods.Week : AggregationPeriods.None;
        periods |= MonthCheckBox.IsChecked ? AggregationPeriods.Month : AggregationPeriods.None;
        if (periods == AggregationPeriods.None)
        {
            validationMessage = "日毎・週毎・月毎から、1つ以上選択してください。";
            return false;
        }

        var keys = AggregationKeys.None;
        keys |= MachineKeyCheckBox.IsChecked ? AggregationKeys.Machine : AggregationKeys.None;
        keys |= PayoutKeyCheckBox.IsChecked ? AggregationKeys.PayoutBand : AggregationKeys.None;
        keys |= EndingKeyCheckBox.IsChecked ? AggregationKeys.Ending : AggregationKeys.None;
        keys |= InstalledKeyCheckBox.IsChecked
            ? AggregationKeys.InstalledUnits
            : AggregationKeys.None;
        if (keys == AggregationKeys.None)
        {
            validationMessage = "機種別・出率帯別・末尾別・設置台数別から、1つ以上選択してください。";
            return false;
        }

        request = new ExtractionRequest(
            SourceUrl: UrlEntry.Text.Trim(),
            IsStoreMode: StoreModeRadio.IsChecked,
            FromDate: StoreModeRadio.IsChecked ? fromDate : null,
            ToDate: StoreModeRadio.IsChecked ? toDate : null,
            MaxListPages: 5,
            MaxReports: maxReports,
            Delay: TimeSpan.FromSeconds(1.5),
            Periods: periods,
            Keys: keys,
            FetchBonusDetails: BonusDetailsCheckBox.IsChecked,
            CreatePartialOutput: PartialOutputRadio.IsChecked);
        return true;
    }

    private void UpdateProgress(ExtractionProgress progress)
    {
        StatusLabel.Text = progress.Message;
        ProgressBar.Progress = Math.Clamp(progress.Ratio, 0, 1);
        AppendLog(progress.Message);
    }

    private void AppendLog(string message)
    {
        var timestamped = $"{DateTime.Now:HH:mm:ss}  {message}";
        LogEditor.Text = string.IsNullOrEmpty(LogEditor.Text)
            ? timestamped
            : $"{LogEditor.Text}{Environment.NewLine}{timestamped}";
        LogEditor.CursorPosition = LogEditor.Text.Length;
    }

    private void SetRunningState(bool isRunning)
    {
        StartButton.IsEnabled = !isRunning;
        CancelButton.IsEnabled = isRunning;
        UrlEntry.IsEnabled = !isRunning;
        StoreModeRadio.IsEnabled = !isRunning;
        ReportModeRadio.IsEnabled = !isRunning;
        MaxReportsEntry.IsEnabled = !isRunning;
        FromDatePicker.IsEnabled = !isRunning;
        ToDatePicker.IsEnabled = !isRunning;
        FromDateButton.IsEnabled = !isRunning;
        ToDateButton.IsEnabled = !isRunning;
        DayCheckBox.IsEnabled = !isRunning;
        WeekCheckBox.IsEnabled = !isRunning;
        MonthCheckBox.IsEnabled = !isRunning;
        MachineKeyCheckBox.IsEnabled = !isRunning;
        PayoutKeyCheckBox.IsEnabled = !isRunning;
        EndingKeyCheckBox.IsEnabled = !isRunning;
        InstalledKeyCheckBox.IsEnabled = !isRunning;
        BonusDetailsCheckBox.IsEnabled = !isRunning;
        PartialOutputRadio.IsEnabled = !isRunning;
        StrictOutputRadio.IsEnabled = !isRunning;
    }

}
