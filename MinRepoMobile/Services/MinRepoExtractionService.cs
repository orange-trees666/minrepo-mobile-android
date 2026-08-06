using System.IO.Compression;
using MinRepoMobile.Models;

namespace MinRepoMobile.Services;

/// <summary>
/// 画面からの要求を受け、URL探索、ページ取得、HTML解析、ZIP出力を順番に実行します。
/// UIに依存しないため、将来バックグラウンド処理や別画面からも再利用できます。
/// </summary>
public sealed class MinRepoExtractionService
{
    private readonly RespectfulMinRepoClient _client = new();
    private readonly MinRepoHtmlParser _parser = new();
    private readonly CsvZipExporter _exporter = new();

    public async Task<ExtractionResult> ExtractAsync(
        ExtractionRequest request,
        IProgress<ExtractionProgress>? progress,
        CancellationToken cancellationToken)
    {
        var failures = new List<ExtractionFailure>();
        IReadOnlyList<Uri> reportUris = request.IsStoreMode
            ? await DiscoverStoreReportsAsync(
                request,
                failures,
                progress,
                cancellationToken)
            : new[]
            {
                MinRepoUrl.Normalize(
                    request.SourceUrl,
                    requireStorePath: false,
                    allowAllQuery: true),
            };

        if (reportUris.Count == 0)
        {
            var failure = new ExtractionFailure(
                request.SourceUrl,
                "店舗ページから日別レポートを見つけられませんでした。",
                "店舗一覧・レポート探索");
            RegisterFailureOrThrow(
                failures,
                failure,
                request.CreatePartialOutput);
        }

        var allRows = new List<SlotRow>();

        for (var index = 0; index < reportUris.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var allMachinesUri = MinRepoUrl.ToAllMachines(reportUris[index]);
            progress?.Report(new ExtractionProgress(
                $"日別レポートを取得中 {index + 1}/{reportUris.Count}",
                0.1 + 0.78 * (index + 1d) / reportUris.Count));

            try
            {
                int? expectedUnitCount = null;
                int? expectedTotalDifference = null;
                var reportTopUri = MinRepoUrl.ToReportTop(reportUris[index]);
                string? reportTopHtml = null;
                DateOnly? reportTopDate = null;
                try
                {
                    progress?.Report(new ExtractionProgress(
                        $"全台数を確認中 {index + 1}/{reportUris.Count}",
                        0.08 + 0.78 * (index + 1d) / reportUris.Count));
                    reportTopHtml = await _client.GetTextAsync(
                        reportTopUri,
                        request.Delay,
                        cancellationToken);
                    reportTopDate = _parser.ExtractReportDate(reportTopHtml);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    var failure = new ExtractionFailure(
                        reportTopUri.AbsoluteUri,
                        $"全台数・総差枚の事前確認に失敗しました: {ex.Message}",
                        "レポートトップ取得");
                    RegisterFailureOrThrow(
                        failures,
                        failure,
                        request.CreatePartialOutput);
                }

                // 一覧は新しい順です。日付だけを先に判定し、期間外ページでは
                // 全台表や機種詳細を取得しないことで、不要な失敗記録と通信を防ぎます。
                if (request.FromDate is not null &&
                    reportTopDate is not null &&
                    reportTopDate.Value < request.FromDate.Value)
                {
                    break;
                }
                if (request.ToDate is not null &&
                    reportTopDate is not null &&
                    reportTopDate.Value > request.ToDate.Value)
                {
                    continue;
                }

                if (reportTopHtml is not null)
                {
                    expectedUnitCount =
                        _parser.ExtractExpectedUnitCount(reportTopHtml);
                    expectedTotalDifference =
                        _parser.ExtractReportedTotalDifference(reportTopHtml);
                    if (expectedUnitCount is null)
                    {
                        RegisterFailureOrThrow(
                            failures,
                            new ExtractionFailure(
                                reportTopUri.AbsoluteUri,
                                "勝率の分母を取得できず、全台数の照合を省略しました。",
                                "レポートトップ・全台数確認"),
                            request.CreatePartialOutput);
                    }
                    if (expectedTotalDifference is null)
                    {
                        RegisterFailureOrThrow(
                            failures,
                            new ExtractionFailure(
                                reportTopUri.AbsoluteUri,
                                "総差枚を取得できず、店舗全体差枚の照合を省略しました。",
                                "レポートトップ・総差枚確認"),
                            request.CreatePartialOutput);
                    }
                }

                var html = await _client.GetTextAsync(
                    allMachinesUri,
                    request.Delay,
                    cancellationToken);
                var report = _parser.ParseReport(
                    html,
                    allMachinesUri,
                    request.CreatePartialOutput,
                    issue => failures.Add(new ExtractionFailure(
                        allMachinesUri.AbsoluteUri,
                        issue,
                        "全台データ行")));

                // レポートトップの日付を取得できなかった場合の安全な代替判定です。
                // また、トップと全台ページの日付が異なる場合は別日の混入を検出します。
                if (reportTopDate is not null &&
                    report.ReportDate != reportTopDate.Value)
                {
                    RegisterFailureOrThrow(
                        failures,
                        new ExtractionFailure(
                            allMachinesUri.AbsoluteUri,
                            $"レポート日が一致しません。トップ=" +
                            $"{reportTopDate.Value:yyyy-MM-dd}、" +
                            $"全台ページ={report.ReportDate:yyyy-MM-dd}。",
                            "レポート日照合"),
                        request.CreatePartialOutput);
                }
                if (reportTopDate is null &&
                    request.FromDate is not null &&
                    report.ReportDate < request.FromDate.Value)
                {
                    break;
                }
                if (reportTopDate is null &&
                    request.ToDate is not null &&
                    report.ReportDate > request.ToDate.Value)
                {
                    continue;
                }

                var reportRows =
                    request.FetchBonusDetails || report.PendingRows.Count > 0
                    ? await EnrichWithMachineDetailsAsync(
                        report,
                        request,
                        failures,
                        progress,
                        cancellationToken)
                    : report.Rows;

                if (expectedUnitCount is not null &&
                    reportRows.Count != expectedUnitCount.Value)
                {
                    RegisterFailureOrThrow(
                        failures,
                        new ExtractionFailure(
                            allMachinesUri.AbsoluteUri,
                            $"全台数が一致しません。レポート記載=" +
                            $"{expectedUnitCount.Value}台、解析結果={reportRows.Count}台。",
                            "全台数照合"),
                        request.CreatePartialOutput);
                }
                var missingDifferenceCount =
                    reportRows.Count(row => row.Difference is null);
                var missingPayoutRateCount =
                    reportRows.Count(row => row.PayoutRate is null);
                if (missingDifferenceCount > 0 || missingPayoutRateCount > 0)
                {
                    RegisterFailureOrThrow(
                        failures,
                        new ExtractionFailure(
                            allMachinesUri.AbsoluteUri,
                            "サイト上で「-」の項目があります。" +
                            $"差枚={missingDifferenceCount}台、出率={missingPayoutRateCount}台。" +
                            "台番・G数・BB・RB等は取得し、非掲載項目だけを空欄にしました。",
                            "サイト非掲載項目"),
                        request.CreatePartialOutput);
                }

                var hasCompleteDifferences = missingDifferenceCount == 0;
                var parsedTotalDifference = hasCompleteDifferences
                    ? reportRows.Sum(row => row.Difference!.Value)
                    : (int?)null;
                if (expectedTotalDifference is not null &&
                    parsedTotalDifference is not null &&
                    parsedTotalDifference.Value != expectedTotalDifference.Value)
                {
                    RegisterFailureOrThrow(
                        failures,
                        new ExtractionFailure(
                            allMachinesUri.AbsoluteUri,
                            $"店舗全体の総差枚が一致しません。レポート記載=" +
                            $"{expectedTotalDifference.Value:+#;-#;0}枚、解析結果=" +
                            $"{parsedTotalDifference.Value:+#;-#;0}枚。",
                            "店舗全体・総差枚照合"),
                        request.CreatePartialOutput);
                }

                allRows.AddRange(reportRows);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                failures.Add(new ExtractionFailure(
                    allMachinesUri.AbsoluteUri,
                    ex.Message,
                    $"日別レポート {index + 1}/{reportUris.Count}"));
                if (!request.CreatePartialOutput)
                {
                    break;
                }
            }
        }

        if (!request.CreatePartialOutput && failures.Count > 0)
        {
            throw new InvalidOperationException(
                "完全取得モードのためファイルを作成しませんでした。" +
                $" 最初のエラー: {failures[0].Error}");
        }

        if (allRows.Count == 0 &&
            (!request.CreatePartialOutput || failures.Count == 0))
        {
            throw new InvalidOperationException(
                "指定期間の台データがありませんでした。");
        }

        progress?.Report(new ExtractionProgress("CSVとZIPを作成中", 0.93));
        var zipPath = await _exporter.ExportAsync(
            allRows,
            failures,
            request.SourceUrl,
            request.Periods,
            request.Keys,
            cancellationToken);

        progress?.Report(new ExtractionProgress("ZIPを作成しました", 1));
        return new ExtractionResult(
            ZipPath: zipPath,
            ReportCount: allRows.Select(row => row.SourceUrl).Distinct().Count(),
            RowCount: allRows.Count,
            FailureCount: failures.Count);
    }

    /// <summary>
    /// 部分出力モードでは失敗を記録して継続し、完全取得モードでは即時に例外化します。
    /// </summary>
    private static void RegisterFailureOrThrow(
        ICollection<ExtractionFailure> failures,
        ExtractionFailure failure,
        bool createPartialOutput)
    {
        if (!createPartialOutput)
        {
            throw new InvalidDataException(failure.Error);
        }

        failures.Add(failure);
    }

    public async Task<ExtractionResult> RunSelfTestAsync(
        IProgress<ExtractionProgress>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report(new ExtractionProgress("テストHTMLを解析中", 0.3));
        var source = new Uri("https://min-repo.com/1/?kishu=all");
        var report = _parser.ParseReport(SelfTestHtml, source);
        var partialIssues = new List<string>();
        var partialReport = _parser.ParseReport(
            SelfTestPartialHtml,
            source,
            continueOnRowError: true,
            reportIssue: partialIssues.Add);
        var strictModeRejectedPartialData = false;
        try
        {
            _parser.ParseReport(SelfTestPartialHtml, source);
        }
        catch (InvalidDataException)
        {
            strictModeRejectedPartialData = true;
        }

        var expectedUnitCount = _parser.ExtractExpectedUnitCount(SelfTestHtml);
        var expectedTotalDifference =
            _parser.ExtractReportedTotalDifference(SelfTestHtml);
        var extractedReportDate = _parser.ExtractReportDate(SelfTestHtml);
        var testDetailSource = new Uri("https://min-repo.com/1/?kishu=test");
        var testDetails = _parser.ParseMachineDetails(SelfTestDetailHtml, testDetailSource);
        var enrichedRows = ApplyMachineDetails(report, testDetails);

        if (expectedUnitCount != 3 ||
            expectedTotalDifference != 900 ||
            extractedReportDate != new DateOnly(2026, 7, 27) ||
            report.Rows.Count != 3 ||
            report.PendingRows.Count != 0 ||
            partialReport.Rows.Count != 2 ||
            partialIssues.Count != 1 ||
            !strictModeRejectedPartialData ||
            enrichedRows.Count != expectedUnitCount ||
            enrichedRows.Count(row => row.Difference is null) != 1 ||
            testDetails[102].Difference is not null ||
            enrichedRows.Count(row => row.Machine == "機種A") != 2 ||
            enrichedRows.Where(row => row.Difference is not null)
                .Sum(row => row.Difference!.Value) != 1200 ||
            enrichedRows.Sum(row => row.Bb ?? 0) != 15 ||
            enrichedRows.Sum(row => row.Rb ?? 0) != 18)
        {
            throw new InvalidOperationException("内部解析テストの結果が一致しません。");
        }

        progress?.Report(new ExtractionProgress("失敗時の部分出力をテスト中", 0.6));
        var failureOnlyZip = await _exporter.ExportAsync(
            Array.Empty<SlotRow>(),
            new[]
            {
                new ExtractionFailure(
                    "https://min-repo.com/1/?kishu=all",
                    "自己テスト用の取得失敗です。",
                    "全台データ・台番102"),
            },
            "アプリ内部分出力テスト",
            AggregationPeriods.Day,
            AggregationKeys.Machine,
            cancellationToken);
        try
        {
            ValidateFailureOnlyZip(failureOnlyZip);
        }
        finally
        {
            // 自己テスト途中の確認用ZIPだけを削除し、利用者へ返す最終ZIPは残します。
            if (File.Exists(failureOnlyZip))
            {
                File.Delete(failureOnlyZip);
            }
        }

        progress?.Report(new ExtractionProgress("テスト用ZIPを作成中", 0.75));
        var zipPath = await _exporter.ExportAsync(
            enrichedRows,
            [],
            "アプリ内テストHTML（実店舗データではありません）",
            AggregationPeriods.Day | AggregationPeriods.Week | AggregationPeriods.Month,
            AggregationKeys.Machine |
            AggregationKeys.PayoutBand |
            AggregationKeys.Ending |
            AggregationKeys.InstalledUnits,
            cancellationToken);

        // HTML解析だけでなく、10種類のCSV・取得情報JSON・ZIP内の台数まで検査します。
        // 画面の「通信なしで動作テスト」により、端末内の一連の処理を確認できます。
        ValidateSelfTestZip(zipPath);

        progress?.Report(new ExtractionProgress("動作テストに成功しました", 1));
        return new ExtractionResult(zipPath, 1, enrichedRows.Count, 0);
    }

    /// <summary>
    /// 自己テストで作成したZIPを開き直し、必要ファイルと台別明細を検証します。
    /// </summary>
    private static void ValidateSelfTestZip(string zipPath)
    {
        var expectedEntries = new[]
        {
            "00_取得失敗一覧.csv",
            "01_台別詳細.csv",
            "02_日別_機種別.csv",
            "03_日別_末尾別.csv",
            "04_日別_設置台数別.csv",
            "05_期間_機種別.csv",
            "06_期間_末尾別.csv",
            "07_期間_設置台数別.csv",
            "08_日週月_可変集計.csv",
            "09_機種評価.csv",
            "取得情報.json",
        };

        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var expectedEntry in expectedEntries)
        {
            if (archive.GetEntry(expectedEntry) is null)
            {
                throw new InvalidOperationException(
                    $"内部テストZIPに{expectedEntry}がありません。");
            }
        }

        var failureCsv = ReadZipText(archive, "00_取得失敗一覧.csv");
        if (!failureCsv.Contains(
                "取得結果,処理箇所,URL,内容",
                StringComparison.Ordinal) ||
            failureCsv.Contains("取得できませんでした", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "内部テストの取得失敗一覧が正しくありません。");
        }

        var rawCsv = ReadZipText(archive, "01_台別詳細.csv");
        var rawLines = rawCsv
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        if (rawLines.Length != 4 ||
            !rawCsv.Contains(",101,", StringComparison.Ordinal) ||
            !rawCsv.Contains(",102,", StringComparison.Ordinal) ||
            !rawCsv.Contains(",113,", StringComparison.Ordinal) ||
            !rawCsv.Contains("一部非掲載,差枚・出率", StringComparison.Ordinal) ||
            rawCsv.Contains(",999,", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "内部テストの台別CSVに欠落または範囲外データがあります。");
        }

        var flexibleCsv = ReadZipText(archive, "08_日週月_可変集計.csv");
        var storeSummaryCount = flexibleCsv
            .Split("店舗全体,全体", StringSplitOptions.None)
            .Length - 1;
        if (storeSummaryCount != 3 ||
            !flexibleCsv.Contains("差枚取得台日数", StringComparison.Ordinal) ||
            !flexibleCsv.Contains("差枚非掲載台日数", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "内部テストの日・週・月または店舗全体の集計結果が一致しません。");
        }
    }

    /// <summary>
    /// ZIP内の指定テキストファイルをUTF-8として読み取ります。
    /// </summary>
    private static string ReadZipText(ZipArchive archive, string entryName)
    {
        var entry = archive.GetEntry(entryName) ??
            throw new InvalidOperationException(
                $"内部テストZIPに{entryName}がありません。");
        using var stream = entry.Open();
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// 取得データが0件でも、部分出力モード用のZIPと失敗一覧が作られることを確認します。
    /// </summary>
    private static void ValidateFailureOnlyZip(string zipPath)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        var failureCsv = ReadZipText(archive, "00_取得失敗一覧.csv");
        var rawCsv = ReadZipText(archive, "01_台別詳細.csv");
        var rawLines = rawCsv
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);

        if (!failureCsv.Contains("取得できませんでした", StringComparison.Ordinal) ||
            !failureCsv.Contains("全台データ・台番102", StringComparison.Ordinal) ||
            !failureCsv.Contains("自己テスト用の取得失敗です。", StringComparison.Ordinal) ||
            rawLines.Length != 1)
        {
            throw new InvalidOperationException(
                "取得0件時の部分出力テストに失敗しました。");
        }
    }

    private async Task<IReadOnlyList<SlotRow>> EnrichWithMachineDetailsAsync(
        ReportResult report,
        ExtractionRequest request,
        List<ExtractionFailure> failures,
        IProgress<ExtractionProgress>? progress,
        CancellationToken cancellationToken)
    {
        var enriched = report.Rows.ToList();
        var detailUrls = report.Rows
            .Where(row => !string.IsNullOrWhiteSpace(row.DetailUrl))
            .Select(row => row.DetailUrl!)
            .Concat(report.PendingRows.Select(row => row.DetailUrl))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        for (var index = 0; index < detailUrls.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Uri.TryCreate(detailUrls[index], UriKind.Absolute, out var detailUri) ||
                !MinRepoUrl.TryNormalizeDetailLink(detailUri, out var normalized))
            {
                var failure = new ExtractionFailure(
                    detailUrls[index],
                    "機種詳細URLを正規化できませんでした。",
                    $"機種詳細URL {index + 1}/{detailUrls.Count}");
                RegisterFailureOrThrow(
                    failures,
                    failure,
                    request.CreatePartialOutput);
                continue;
            }

            progress?.Report(new ExtractionProgress(
                $"BB/RB詳細を取得中 {index + 1}/{detailUrls.Count}",
                0.2));

            try
            {
                var html = await _client.GetTextAsync(
                    normalized,
                    request.Delay,
                    cancellationToken);
                var details = _parser.ParseMachineDetails(
                    html,
                    normalized,
                    request.CreatePartialOutput,
                    issue => failures.Add(new ExtractionFailure(
                        normalized.AbsoluteUri,
                        issue,
                        "機種詳細行")));

                var expectedUnits = enriched
                    .Where(row => string.Equals(
                        row.DetailUrl,
                        normalized.AbsoluteUri,
                        StringComparison.OrdinalIgnoreCase))
                    .Select(row => row.UnitNumber)
                    .Concat(report.PendingRows
                        .Where(row => string.Equals(
                            row.DetailUrl,
                            normalized.AbsoluteUri,
                            StringComparison.OrdinalIgnoreCase))
                        .Select(row => row.UnitNumber))
                    .Distinct()
                    .ToList();

                // 同じ詳細URLを持つ機種だけへ適用し、同じ台番が別機種にあっても混同しません。
                for (var rowIndex = 0; rowIndex < enriched.Count; rowIndex++)
                {
                    var row = enriched[rowIndex];
                    if (!string.Equals(
                            row.DetailUrl,
                            normalized.AbsoluteUri,
                            StringComparison.OrdinalIgnoreCase) ||
                        !details.TryGetValue(row.UnitNumber, out var detail))
                    {
                        continue;
                    }

                    enriched[rowIndex] = ApplyMachineDetail(row, detail);
                }

                // 全台ページで必須列を解析できなかった行だけを、機種詳細ページの
                // 取得可能な値から復元します。サイト非掲載値はnullのまま保持します。
                foreach (var pending in report.PendingRows.Where(row =>
                    string.Equals(
                        row.DetailUrl,
                        normalized.AbsoluteUri,
                        StringComparison.OrdinalIgnoreCase)))
                {
                    if (!details.TryGetValue(pending.UnitNumber, out var detail) ||
                        enriched.Any(row => row.UnitNumber == pending.UnitNumber))
                    {
                        continue;
                    }

                    enriched.Add(CreateRecoveredSlotRow(pending, detail));
                }

                foreach (var expectedUnit in expectedUnits)
                {
                    if (details.ContainsKey(expectedUnit))
                    {
                        continue;
                    }

                    RegisterFailureOrThrow(
                        failures,
                        new ExtractionFailure(
                            normalized.AbsoluteUri,
                            $"台番{expectedUnit}の詳細データを取得できませんでした。",
                            $"機種詳細・台番{expectedUnit}"),
                        request.CreatePartialOutput);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                failures.Add(new ExtractionFailure(
                    normalized.AbsoluteUri,
                    ex.Message,
                    $"機種詳細 {index + 1}/{detailUrls.Count}"));
                if (!request.CreatePartialOutput)
                {
                    break;
                }
            }
        }

        return enriched;
    }

    private static IReadOnlyList<SlotRow> ApplyMachineDetails(
        ReportResult report,
        IReadOnlyDictionary<int, MachineDetail> details)
        => report.Rows
            .Select(row => details.TryGetValue(row.UnitNumber, out var detail)
                ? ApplyMachineDetail(row, detail)
                : row)
            .Concat(report.PendingRows
                .Where(row => details.ContainsKey(row.UnitNumber))
                .Select(row => CreateRecoveredSlotRow(
                    row,
                    details[row.UnitNumber])))
            .ToList();

    private static SlotRow ApplyMachineDetail(SlotRow row, MachineDetail detail)
        => row with
        {
            Difference = detail.Difference ?? row.Difference,
            Games = detail.Games,
            TotalSpins = detail.TotalSpins,
            PayoutRate = detail.PayoutRate ?? row.PayoutRate,
            Bb = detail.Bb,
            Rb = detail.Rb,
            CombinedRate = detail.CombinedRate,
            BbRate = detail.BbRate,
            RbRate = detail.RbRate,
            DetailUrl = detail.SourceUrl,
        };

    private static SlotRow CreateRecoveredSlotRow(
        PendingSlotRow pending,
        MachineDetail detail)
        => new(
            Store: pending.Store,
            ReportDate: pending.ReportDate,
            Machine: pending.Machine,
            UnitNumber: pending.UnitNumber,
            Ending: pending.Ending,
            Difference: detail.Difference,
            Games: detail.Games,
            TotalSpins: detail.TotalSpins,
            PayoutRate: detail.PayoutRate,
            Bb: detail.Bb,
            Rb: detail.Rb,
            CombinedRate: detail.CombinedRate,
            BbRate: detail.BbRate,
            RbRate: detail.RbRate,
            SourceUrl: pending.SourceUrl,
            DetailUrl: detail.SourceUrl);

    private async Task<IReadOnlyList<Uri>> DiscoverStoreReportsAsync(
        ExtractionRequest request,
        List<ExtractionFailure> failures,
        IProgress<ExtractionProgress>? progress,
        CancellationToken cancellationToken)
    {
        var storeUri = MinRepoUrl.Normalize(
            request.SourceUrl,
            requireStorePath: true,
            allowAllQuery: false);
        var results = new List<Uri>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var pageNumber = 1;
             pageNumber <= request.MaxListPages && results.Count < request.MaxReports;
             pageNumber++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pageUri = pageNumber == 1
                ? storeUri
                : new Uri(storeUri, $"page/{pageNumber}/");

            progress?.Report(new ExtractionProgress(
                $"店舗一覧を確認中 {pageNumber}/{request.MaxListPages}",
                0.02 + 0.08 * pageNumber / request.MaxListPages));

            string html;
            try
            {
                html = await _client.GetTextAsync(
                    pageUri,
                    request.Delay,
                    cancellationToken);
            }
            catch (HttpRequestException ex) when (
                ex.StatusCode == System.Net.HttpStatusCode.NotFound &&
                pageNumber > 1)
            {
                break;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                var failure = new ExtractionFailure(
                    pageUri.AbsoluteUri,
                    $"店舗一覧ページを取得できませんでした: {ex.Message}",
                    $"店舗一覧 {pageNumber}/{request.MaxListPages}");
                RegisterFailureOrThrow(
                    failures,
                    failure,
                    request.CreatePartialOutput);
                continue;
            }

            IReadOnlyList<Uri> pageLinks;
            try
            {
                pageLinks = _parser.ExtractReportLinks(html, pageUri);
            }
            catch (Exception ex)
            {
                var failure = new ExtractionFailure(
                    pageUri.AbsoluteUri,
                    $"店舗一覧ページを解析できませんでした: {ex.Message}",
                    $"店舗一覧解析 {pageNumber}/{request.MaxListPages}");
                RegisterFailureOrThrow(
                    failures,
                    failure,
                    request.CreatePartialOutput);
                continue;
            }
            var newCount = 0;
            foreach (var link in pageLinks)
            {
                if (!seen.Add(link.AbsoluteUri))
                {
                    continue;
                }

                results.Add(link);
                newCount++;
                if (results.Count >= request.MaxReports)
                {
                    break;
                }
            }

            // ページ番号が上限を越えて先頭へ戻るサイト挙動にも対応します。
            if (newCount == 0)
            {
                break;
            }
        }

        return results;
    }

    private const string SelfTestHtml = """
        <html><body>
        <h1>7/27(月) テスト店舗</h1>
        <div>2026年7月28日</div>
        <div>総差枚 +900</div>
        <div>勝率 2/3</div>
        <h2>全台　データ一覧</h2>
        <table>
          <tr><th>機種</th><th>台番</th><th>差枚</th><th>G数</th><th>出率</th></tr>
          <tr><td><a href="/1/?kishu=machine-a">機種A</a></td><td>101</td><td>+1,200</td><td>3,000</td><td>113.3%</td></tr>
          <tr><td><a href="/1/?kishu=machine-a">機種A</a></td><td>102</td><td>-</td><td>1,000</td><td>-</td></tr>
        </table>
        <div>長い一覧の途中にある広告領域</div>
        <table>
          <tr><th>順位</th><th>台番</th><th>ポイント</th></tr>
          <tr><td>広告</td><td>777</td><td>123</td></tr>
        </table>
        <table>
          <tr><th>台番</th><th>機種</th><th>G数</th><th>出率</th><th>差枚</th></tr>
          <tr><td>113</td><td><a href="/1/?kishu=machine-b">機種B</a></td><td>0</td><td>-</td><td>0</td></tr>
        </table>
        <a style="margin-top:15px;" class="btn1 back" href="https://min-repo.com/1/">レポートトップに戻る</a>
        <h3>県内ランキング</h3>
        <table>
          <tr><td>別店舗</td><td>999</td><td>9999</td><td>9999</td><td>133%</td></tr>
        </table>
        </body></html>
        """;

    private const string SelfTestPartialHtml = """
        <html><body>
        <h1>7/27(月) テスト店舗</h1>
        <div>2026年7月28日</div>
        <div>総差枚 +900</div>
        <div>勝率 2/3</div>
        <h2>全台　データ一覧</h2>
        <table>
          <tr><th>機種</th><th>台番</th><th>差枚</th><th>G数</th><th>出率</th></tr>
          <tr><td>機種A</td><td>101</td><td>+1,200</td><td>3,000</td><td>113.3%</td></tr>
          <tr><td>機種A</td><td>102</td><td>-300</td><td>解析不能</td><td>90%</td></tr>
          <tr><td>機種B</td><td>113</td><td>0</td><td>0</td><td>-</td></tr>
        </table>
        <a class="btn1 back" href="https://min-repo.com/1/">レポートトップに戻る</a>
        </body></html>
        """;

    private const string SelfTestDetailHtml = """
        <html><body>
        <h1>7/27(月) テスト店舗</h1>
        <div>2026年7月28日</div>
        <h2>テスト機種　データ一覧</h2>
        <table>
          <tr>
            <th>台番</th><th>差枚</th><th>G数</th><th>出率</th>
            <th>BB</th><th>RB</th><th>合成</th><th>BB率</th><th>RB率</th>
          </tr>
          <tr><td>101</td><td>1,200</td><td>3,000</td><td>113.3%</td><td>10</td><td>8</td><td>1/167</td><td>1/300</td><td>1/375</td></tr>
          <tr><td>102</td><td>-</td><td>1,000</td><td>-</td><td>3</td><td>4</td><td>1/143</td><td>1/333</td><td>1/250</td></tr>
        </table>
        <div>長い詳細一覧の途中にある広告領域</div>
        <table>
          <tr><th>差枚</th><th>G数</th><th>合成</th></tr>
          <tr><td>9,999</td><td>9,999</td><td>1/1</td></tr>
        </table>
        <table>
          <tr>
            <th>BB</th><th>台番</th><th>出率</th><th>差枚</th>
            <th>RB</th><th>G数</th><th>合成</th><th>BB率</th><th>RB率</th>
          </tr>
          <tr><td>2</td><td>113</td><td>-</td><td>0</td><td>6</td><td>0</td><td>-</td><td>-</td><td>-</td></tr>
          <tr><td>5</td><td>平均</td><td>101%</td><td>300</td><td>6</td><td>1,333</td><td>1/121</td><td>1/267</td><td>1/222</td></tr>
        </table>
        <a style="margin-top:15px;" class="btn1 back" href="https://min-repo.com/1/">レポートトップに戻る</a>
        <h3>県内ランキング</h3>
        <table>
          <tr><td>999</td><td>0</td><td>9999</td><td>133%</td><td>9</td><td>9</td><td>1/1</td><td>1/1</td><td>1/1</td></tr>
        </table>
        </body></html>
        """;
}
