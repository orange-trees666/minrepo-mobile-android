using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using MinRepoMobile.Models;
using Microsoft.Maui.Storage;

namespace MinRepoMobile.Services;

/// <summary>
/// 台明細から10種類のCSVと取得情報JSONを作成し、Android共有用ZIPへまとめます。
/// CSVはExcelで文字化けしにくいUTF-8 BOM付きです。
/// </summary>
public sealed class CsvZipExporter
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public async Task<string> ExportAsync(
        IReadOnlyList<SlotRow> rows,
        IReadOnlyList<ExtractionFailure> failures,
        string source,
        AggregationPeriods periods,
        AggregationKeys keys,
        CancellationToken cancellationToken)
    {
        var workDirectory = Path.Combine(
            FileSystem.CacheDirectory,
            "minrepo-work",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDirectory);

        try
        {
            WriteFailureCsv(
                Path.Combine(workDirectory, "00_取得失敗一覧.csv"),
                failures);
            WriteRawCsv(Path.Combine(workDirectory, "01_台別詳細.csv"), rows);
            WriteDailyMachineCsv(Path.Combine(workDirectory, "02_日別_機種別.csv"), rows);
            WriteDailyEndingCsv(Path.Combine(workDirectory, "03_日別_末尾別.csv"), rows);
            WriteDailyCountCsv(Path.Combine(workDirectory, "04_日別_設置台数別.csv"), rows);
            WritePeriodMachineCsv(Path.Combine(workDirectory, "05_期間_機種別.csv"), rows);
            WritePeriodEndingCsv(Path.Combine(workDirectory, "06_期間_末尾別.csv"), rows);
            WritePeriodCountCsv(Path.Combine(workDirectory, "07_期間_設置台数別.csv"), rows);
            WriteFlexibleAggregationCsv(
                Path.Combine(workDirectory, "08_日週月_可変集計.csv"),
                rows,
                periods,
                keys);
            WriteMachineEvaluationCsv(
                Path.Combine(workDirectory, "09_機種評価.csv"),
                rows);

            var metadata = new
            {
                createdAt = DateTimeOffset.Now,
                source,
                rowCount = rows.Count,
                reportCount = rows.Select(row => row.SourceUrl).Distinct().Count(),
                aggregationPeriods = periods.ToString(),
                aggregationKeys = keys.ToString(),
                bonusDetailRows = rows.Count(row => row.Bb is not null || row.Rb is not null),
                differencePublishedRows = rows.Count(row => row.Difference is not null),
                differenceNotPublishedRows = rows.Count(row => row.Difference is null),
                failures,
                notice =
                    "みんレポ掲載値は同サイトの独自調査値で、実際の数値と異なる可能性があります。" +
                    "サイト上で「-」の項目は推測せず空欄にし、欠損を含む集計の差枚指標も空欄にしています。" +
                    "引用時は各CSVの出典URLを併記してください。",
            };
            await File.WriteAllTextAsync(
                Path.Combine(workDirectory, "取得情報.json"),
                JsonSerializer.Serialize(
                    metadata,
                    new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false),
                cancellationToken);

            var shareDirectory = Path.Combine(
                FileSystem.CacheDirectory,
                "sharing-root");
            Directory.CreateDirectory(shareDirectory);

            var shortId = Guid.NewGuid().ToString("N").Substring(0, 8);
            var zipName = $"minrepo_{DateTime.Now:yyyyMMdd_HHmmss}_{shortId}.zip";
            var zipPath = Path.Combine(shareDirectory, zipName);
            ZipFile.CreateFromDirectory(
                workDirectory,
                zipPath,
                CompressionLevel.Optimal,
                includeBaseDirectory: false);
            return zipPath;
        }
        finally
        {
            // GUIDで作成した今回専用フォルダーだけを削除します。
            if (Directory.Exists(workDirectory))
            {
                try
                {
                    Directory.Delete(workDirectory, recursive: true);
                }
                catch (IOException)
                {
                    // ZIP作成済みなら、一時フォルダーの後片付け失敗で結果を失敗扱いにしません。
                }
            }
        }
    }

    /// <summary>
    /// 取得できなかったページ・台番・検証項目を、利用者がCSVで確認できる形にします。
    /// 失敗がない場合もヘッダーだけを出力し、完全取得だったことを判別できます。
    /// </summary>
    private static void WriteFailureCsv(
        string path,
        IReadOnlyList<ExtractionFailure> failures)
    {
        WriteCsv(
            path,
            new[] { "取得結果", "処理箇所", "URL", "内容" },
            failures.Select(failure => new object?[]
            {
                "取得できませんでした",
                failure.Location,
                failure.Url,
                failure.Error,
            }));
    }

    private static void WriteRawCsv(string path, IReadOnlyList<SlotRow> rows)
    {
        WriteCsv(
            path,
            [
                "店舗", "日付", "曜日", "機種", "台番", "末尾", "差枚",
                "G数", "総回転数", "出率(%)", "BB", "RB", "合成",
                "BB率", "RB率", "取得状態", "未取得項目",
                "日別出典URL", "機種詳細URL",
            ],
            rows
                .OrderBy(row => row.Store)
                .ThenBy(row => row.ReportDate)
                .ThenBy(row => row.Machine)
                .ThenBy(row => row.UnitNumber)
                .Select(row => new object?[]
                {
                    row.Store,
                    row.ReportDate,
                    JapaneseDayOfWeek(row.ReportDate.DayOfWeek),
                    row.Machine,
                    row.UnitNumber,
                    row.Ending,
                    row.Difference,
                    row.Games,
                    row.TotalSpins,
                    row.PayoutRate,
                    row.Bb,
                    row.Rb,
                    row.CombinedRate,
                    row.BbRate,
                    row.RbRate,
                    GetRowStatus(row),
                    GetMissingFields(row),
                    row.SourceUrl,
                    row.DetailUrl,
                }));
    }

    private static void WriteDailyMachineCsv(string path, IReadOnlyList<SlotRow> rows)
    {
        var records = rows
            .GroupBy(row => (row.Store, row.ReportDate, row.Machine))
            .OrderBy(group => group.Key.Store)
            .ThenBy(group => group.Key.ReportDate)
            .ThenBy(group => group.Key.Machine)
            .Select(group => SummaryRecord(
                [group.Key.Store, group.Key.ReportDate, group.Key.Machine],
                group));

        WriteCsv(path, DailyHeaders("機種"), records);
    }

    private static void WriteDailyEndingCsv(string path, IReadOnlyList<SlotRow> rows)
    {
        var records = rows
            .GroupBy(row => (row.Store, row.ReportDate, row.Ending))
            .OrderBy(group => group.Key.Store)
            .ThenBy(group => group.Key.ReportDate)
            .ThenBy(group => group.Key.Ending)
            .Select(group => SummaryRecord(
                [group.Key.Store, group.Key.ReportDate, group.Key.Ending],
                group));

        WriteCsv(path, DailyHeaders("末尾"), records);
    }

    private static void WriteDailyCountCsv(string path, IReadOnlyList<SlotRow> rows)
    {
        var machineDays = rows
            .GroupBy(row => (row.Store, row.ReportDate, row.Machine))
            .Select(group => new MachineDay(group.Key, group.ToList()))
            .ToList();

        var records = machineDays
            .GroupBy(item => (
                item.Key.Store,
                item.Key.ReportDate,
                InstalledUnits: item.Rows.Count))
            .OrderBy(group => group.Key.Store)
            .ThenBy(group => group.Key.ReportDate)
            .ThenBy(group => group.Key.InstalledUnits)
            .Select(group =>
            {
                var flattened = group.SelectMany(item => item.Rows).ToList();
                return SummaryRecord(
                    [
                        group.Key.Store,
                        group.Key.ReportDate,
                        group.Key.InstalledUnits,
                        group.Count(),
                    ],
                    flattened);
            });

        WriteCsv(
            path,
            [
                "店舗", "日付", "1機種あたり設置台数", "機種数",
                "対象台数", "稼働台数", "差枚取得台数", "差枚非掲載台数",
                "総差枚", "平均差枚", "平均G数",
                "勝ち台数", "勝率(%)", "計算出率(%)", "出典URL",
            ],
            records);
    }

    private static void WritePeriodMachineCsv(string path, IReadOnlyList<SlotRow> rows)
    {
        var records = rows
            .GroupBy(row => (row.Store, row.Machine))
            .OrderBy(group => group.Key.Store)
            .ThenBy(group => group.Key.Machine)
            .Select(group => PeriodSummaryRecord(
                [group.Key.Store, group.Key.Machine],
                group));

        WriteCsv(path, PeriodHeaders("機種"), records);
    }

    private static void WritePeriodEndingCsv(string path, IReadOnlyList<SlotRow> rows)
    {
        var records = rows
            .GroupBy(row => (row.Store, row.Ending))
            .OrderBy(group => group.Key.Store)
            .ThenBy(group => group.Key.Ending)
            .Select(group => PeriodSummaryRecord(
                [group.Key.Store, group.Key.Ending],
                group));

        WriteCsv(path, PeriodHeaders("末尾"), records);
    }

    private static void WritePeriodCountCsv(string path, IReadOnlyList<SlotRow> rows)
    {
        var machineDays = rows
            .GroupBy(row => (row.Store, row.ReportDate, row.Machine))
            .Select(group => new MachineDay(group.Key, group.ToList()))
            .ToList();

        var records = machineDays
            .GroupBy(item => (item.Key.Store, InstalledUnits: item.Rows.Count))
            .OrderBy(group => group.Key.Store)
            .ThenBy(group => group.Key.InstalledUnits)
            .Select(group =>
            {
                var flattened = group.SelectMany(item => item.Rows).ToList();
                var summary = CalculateSummary(flattened);
                return new object?[]
                {
                    group.Key.Store,
                    group.Key.InstalledUnits,
                    flattened.Select(row => row.ReportDate).Distinct().Count(),
                    group.Count(),
                    summary.UnitCount,
                    summary.ActiveCount,
                    summary.KnownDifferenceCount,
                    summary.MissingDifferenceCount,
                    summary.TotalDifference,
                    summary.AverageDifference,
                    summary.AverageGames,
                    summary.Wins,
                    summary.WinRate,
                    summary.CalculatedRate,
                    summary.SourceUrls,
                };
            });

        WriteCsv(
            path,
            [
                "店舗", "1機種あたり設置台数", "対象日数", "機種日数",
                "台日数", "稼働台日数", "差枚取得台日数", "差枚非掲載台日数",
                "総差枚", "1台1日平均差枚",
                "1台1日平均G数", "勝ち台日数", "勝率(%)",
                "計算出率(%)", "出典URL",
            ],
            records);
    }

    private static void WriteFlexibleAggregationCsv(
        string path,
        IReadOnlyList<SlotRow> rows,
        AggregationPeriods periods,
        AggregationKeys keys)
    {
        var installedUnitsByMachineDay = rows
            .GroupBy(row => (row.Store, row.ReportDate, row.Machine))
            .ToDictionary(group => group.Key, group => group.Count());

        var records = new List<object?[]>();
        foreach (var period in EnumeratePeriods(periods))
        {
            var periodGroups = rows
                .GroupBy(row =>
                {
                    var bucket = GetPeriodBucket(row.ReportDate, period);
                    return (row.Store, bucket.Start, bucket.End, bucket.Label);
                })
                .OrderBy(group => group.Key.Store)
                .ThenBy(group => group.Key.Start);

            foreach (var periodGroup in periodGroups)
            {
                var values = periodGroup.ToList();

                // 店舗全体はキー選択に関係なく必ず出力し、総差枚の確認に使います。
                records.Add(FlexibleSummaryRecord(
                    periodGroup.Key.Store,
                    periodGroup.Key.Label,
                    periodGroup.Key.Start,
                    periodGroup.Key.End,
                    "店舗全体",
                    "全体",
                    values));

                if (keys.HasFlag(AggregationKeys.Machine))
                {
                    records.AddRange(values
                        .GroupBy(row => row.Machine)
                        .OrderBy(group => group.Key)
                        .Select(group => FlexibleSummaryRecord(
                            periodGroup.Key.Store,
                            periodGroup.Key.Label,
                            periodGroup.Key.Start,
                            periodGroup.Key.End,
                            "機種",
                            group.Key,
                            group)));
                }

                if (keys.HasFlag(AggregationKeys.PayoutBand))
                {
                    records.AddRange(values
                        .GroupBy(row => PayoutBand(row.PayoutRate))
                        .OrderBy(group => PayoutBandOrder(group.Key))
                        .Select(group => FlexibleSummaryRecord(
                            periodGroup.Key.Store,
                            periodGroup.Key.Label,
                            periodGroup.Key.Start,
                            periodGroup.Key.End,
                            "出率帯",
                            group.Key,
                            group)));
                }

                if (keys.HasFlag(AggregationKeys.Ending))
                {
                    records.AddRange(values
                        .GroupBy(row => row.Ending)
                        .OrderBy(group => group.Key)
                        .Select(group => FlexibleSummaryRecord(
                            periodGroup.Key.Store,
                            periodGroup.Key.Label,
                            periodGroup.Key.Start,
                            periodGroup.Key.End,
                            "末尾",
                            group.Key.ToString(Invariant),
                            group)));
                }

                if (keys.HasFlag(AggregationKeys.InstalledUnits))
                {
                    records.AddRange(values
                        .GroupBy(row => installedUnitsByMachineDay[
                            (row.Store, row.ReportDate, row.Machine)])
                        .OrderBy(group => group.Key)
                        .Select(group => FlexibleSummaryRecord(
                            periodGroup.Key.Store,
                            periodGroup.Key.Label,
                            periodGroup.Key.Start,
                            periodGroup.Key.End,
                            "1機種あたり設置台数",
                            $"{group.Key}台",
                            group)));
                }
            }
        }

        WriteCsv(
            path,
            [
                "店舗", "集計期間", "期間開始", "期間終了", "集計キー", "キー値",
                "対象日数", "台日数", "稼働台日数",
                "差枚取得台日数", "差枚非掲載台日数", "総差枚", "平均差枚",
                "中央値差枚", "差枚標準偏差", "合計G数", "1台日平均G数",
                "総回転数", "BB合計", "RB合計", "詳細取得台日数", "合成実績",
                "平均出率(%)", "勝ち台日数", "勝率(%)", "105%以上率(%)",
                "110%以上率(%)", "出典URL",
            ],
            records);
    }

    private static void WriteMachineEvaluationCsv(
        string path,
        IReadOnlyList<SlotRow> rows)
    {
        var records = rows
            .GroupBy(row => (row.Store, row.Machine))
            .OrderBy(group => group.Key.Store)
            .ThenBy(group => group.Key.Machine)
            .Select(group =>
            {
                var values = group.ToList();
                var summary = CalculateSummary(values);
                var dayGroups = values.GroupBy(row => row.ReportDate).ToList();
                int? positiveDays = summary.MissingDifferenceCount == 0
                    ? dayGroups.Count(day => day.Sum(row => row.Difference!.Value) > 0)
                    : null;
                var positiveDifferences = values
                    .Where(row => row.Difference is > 0)
                    .Select(row => row.Difference!.Value)
                    .ToList();
                var positiveTotal = positiveDifferences.Sum();
                var topPositiveShare = summary.MissingDifferenceCount > 0 || positiveTotal == 0
                    ? null
                    : (double?)positiveDifferences.Max() / positiveTotal * 100;
                var combined = CombinedActual(summary);
                var sufficient = summary.MissingDifferenceCount > 0
                    ? "差枚不足"
                    : dayGroups.Count >= 5 && summary.ActiveCount >= 20
                    ? "十分"
                    : "少ない";

                return new object?[]
                {
                    group.Key.Store,
                    group.Key.Machine,
                    dayGroups.Count,
                    summary.UnitCount,
                    summary.ActiveCount,
                    summary.KnownDifferenceCount,
                    summary.MissingDifferenceCount,
                    (double)summary.UnitCount / dayGroups.Count,
                    summary.TotalDifference,
                    summary.AverageDifference,
                    summary.MedianDifference,
                    summary.StandardDeviation,
                    positiveDays,
                    positiveDays is null
                        ? null
                        : (double?)positiveDays.Value / dayGroups.Count * 100,
                    summary.Wins,
                    summary.WinRate,
                    summary.TotalGames,
                    summary.AverageGames,
                    summary.AveragePayoutRate,
                    summary.Rate105OrMore,
                    summary.Rate110OrMore,
                    summary.BbTotal,
                    summary.RbTotal,
                    combined,
                    (double)summary.DetailRows / summary.UnitCount * 100,
                    (double)summary.ZeroGameRows / summary.UnitCount * 100,
                    topPositiveShare,
                    sufficient,
                    summary.SourceUrls,
                };
            });

        WriteCsv(
            path,
            [
                "店舗", "機種", "対象日数", "台日数", "稼働台日数",
                "差枚取得台日数", "差枚非掲載台日数",
                "平均設置台数", "総差枚", "平均差枚", "中央値差枚",
                "差枚標準偏差", "プラス日数", "機種プラス日率(%)",
                "勝ち台日数", "勝率(%)", "合計G数", "平均G数",
                "平均出率(%)", "105%以上率(%)", "110%以上率(%)",
                "BB合計", "RB合計", "合成実績", "詳細取得率(%)",
                "0G率(%)", "最大勝ち台依存率(%)", "データ充足度", "出典URL",
            ],
            records);
    }

    private static object?[] FlexibleSummaryRecord(
        string store,
        string periodLabel,
        DateOnly start,
        DateOnly end,
        string keyName,
        string keyValue,
        IEnumerable<SlotRow> source)
    {
        var values = source.ToList();
        var summary = CalculateSummary(values);
        return
        [
            store,
            periodLabel,
            start,
            end,
            keyName,
            keyValue,
            values.Select(row => row.ReportDate).Distinct().Count(),
            summary.UnitCount,
            summary.ActiveCount,
            summary.KnownDifferenceCount,
            summary.MissingDifferenceCount,
            summary.TotalDifference,
            summary.AverageDifference,
            summary.MedianDifference,
            summary.StandardDeviation,
            summary.TotalGames,
            summary.AverageGames,
            summary.TotalSpins,
            summary.BbTotal,
            summary.RbTotal,
            summary.DetailRows,
            CombinedActual(summary),
            summary.AveragePayoutRate,
            summary.Wins,
            summary.WinRate,
            summary.Rate105OrMore,
            summary.Rate110OrMore,
            summary.SourceUrls,
        ];
    }

    private static string? CombinedActual(AggregateSummary summary)
    {
        var bonusCount = summary.BbTotal + summary.RbTotal;
        return bonusCount <= 0 || summary.DetailRows == 0
            ? null
            : $"1/{Math.Round((double)summary.DetailSpins / bonusCount, MidpointRounding.AwayFromZero):0}";
    }

    private static IEnumerable<AggregationPeriods> EnumeratePeriods(
        AggregationPeriods periods)
    {
        if (periods.HasFlag(AggregationPeriods.Day))
        {
            yield return AggregationPeriods.Day;
        }
        if (periods.HasFlag(AggregationPeriods.Week))
        {
            yield return AggregationPeriods.Week;
        }
        if (periods.HasFlag(AggregationPeriods.Month))
        {
            yield return AggregationPeriods.Month;
        }
    }

    private static PeriodBucket GetPeriodBucket(
        DateOnly value,
        AggregationPeriods period)
        => period switch
        {
            AggregationPeriods.Day => new PeriodBucket("日", value, value),
            AggregationPeriods.Week => WeekBucket(value),
            AggregationPeriods.Month => new PeriodBucket(
                "月",
                new DateOnly(value.Year, value.Month, 1),
                new DateOnly(
                    value.Year,
                    value.Month,
                    DateTime.DaysInMonth(value.Year, value.Month))),
            _ => throw new ArgumentOutOfRangeException(nameof(period)),
        };

    private static PeriodBucket WeekBucket(DateOnly value)
    {
        // ISO週と同じ月曜日始まりで、期間終了は日曜日です。
        var daysFromMonday = ((int)value.DayOfWeek + 6) % 7;
        var start = value.AddDays(-daysFromMonday);
        return new PeriodBucket("週", start, start.AddDays(6));
    }

    private static string PayoutBand(double? payoutRate)
    {
        if (payoutRate is null)
        {
            return "不明";
        }

        var rate = payoutRate.Value;
        return rate switch
        {
            < 90 => "90%未満",
            < 100 => "90%以上100%未満",
            < 105 => "100%以上105%未満",
            < 110 => "105%以上110%未満",
            _ => "110%以上",
        };
    }

    private static string GetRowStatus(SlotRow row)
        => row.Difference is not null && row.PayoutRate is not null
            ? "取得済み"
            : "一部非掲載";

    private static string GetMissingFields(SlotRow row)
    {
        var fields = new List<string>();
        if (row.Difference is null)
        {
            fields.Add("差枚");
        }
        if (row.PayoutRate is null)
        {
            fields.Add("出率");
        }

        return string.Join("・", fields);
    }

    private static int PayoutBandOrder(string band)
        => band switch
        {
            "90%未満" => 1,
            "90%以上100%未満" => 2,
            "100%以上105%未満" => 3,
            "105%以上110%未満" => 4,
            "110%以上" => 5,
            _ => 6,
        };

    private static string JapaneseDayOfWeek(DayOfWeek dayOfWeek)
        => dayOfWeek switch
        {
            DayOfWeek.Monday => "月",
            DayOfWeek.Tuesday => "火",
            DayOfWeek.Wednesday => "水",
            DayOfWeek.Thursday => "木",
            DayOfWeek.Friday => "金",
            DayOfWeek.Saturday => "土",
            DayOfWeek.Sunday => "日",
            _ => string.Empty,
        };

    private static object?[] SummaryRecord(
        object?[] keyValues,
        IEnumerable<SlotRow> values)
    {
        var summary = CalculateSummary(values);
        return
        [
            .. keyValues,
            summary.UnitCount,
            summary.ActiveCount,
            summary.KnownDifferenceCount,
            summary.MissingDifferenceCount,
            summary.TotalDifference,
            summary.AverageDifference,
            summary.AverageGames,
            summary.Wins,
            summary.WinRate,
            summary.CalculatedRate,
            summary.SourceUrls,
        ];
    }

    private static object?[] PeriodSummaryRecord(
        object?[] keyValues,
        IEnumerable<SlotRow> values)
    {
        var materialized = values.ToList();
        var summary = CalculateSummary(materialized);
        return
        [
            .. keyValues,
            materialized.Select(row => row.ReportDate).Distinct().Count(),
            summary.UnitCount,
            summary.ActiveCount,
            summary.KnownDifferenceCount,
            summary.MissingDifferenceCount,
            summary.TotalDifference,
            summary.AverageDifference,
            summary.AverageGames,
            summary.Wins,
            summary.WinRate,
            summary.CalculatedRate,
            summary.SourceUrls,
        ];
    }

    private static AggregateSummary CalculateSummary(IEnumerable<SlotRow> source)
    {
        var values = source.ToList();
        var unitCount = values.Count;
        var knownDifferences = values
            .Where(row => row.Difference is not null)
            .Select(row => row.Difference!.Value)
            .ToArray();
        var knownDifferenceCount = knownDifferences.Length;
        var missingDifferenceCount = unitCount - knownDifferenceCount;
        var hasCompleteDifferences = missingDifferenceCount == 0;
        int? totalDifference = hasCompleteDifferences
            ? knownDifferences.Sum()
            : null;
        var totalGames = values.Sum(row => row.Games);
        var totalSpins = values.Sum(row => row.TotalSpins);
        int? wins = hasCompleteDifferences
            ? knownDifferences.Count(difference => difference > 0)
            : null;
        var sortedDifferences = knownDifferences.Order().ToArray();
        double? medianDifference = !hasCompleteDifferences || sortedDifferences.Length == 0
            ? null
            : sortedDifferences.Length % 2 == 1
                ? sortedDifferences[sortedDifferences.Length / 2]
                : (
                    sortedDifferences[sortedDifferences.Length / 2 - 1] +
                    sortedDifferences[sortedDifferences.Length / 2]) / 2d;
        double? averageDifference = !hasCompleteDifferences || unitCount == 0
            ? null
            : (double)totalDifference!.Value / unitCount;
        double? variance = averageDifference is null
            ? null
            : knownDifferences.Sum(difference =>
                Math.Pow(difference - averageDifference.Value, 2)) / unitCount;
        var knownRates = values
            .Where(row => row.PayoutRate is not null)
            .Select(row => row.PayoutRate!.Value)
            .ToList();
        var knownRateCount = knownRates.Count;
        var detailValues = values
            .Where(row => row.Bb is not null || row.Rb is not null)
            .ToList();
        return new AggregateSummary(
            UnitCount: unitCount,
            ActiveCount: values.Count(row => row.Games > 0),
            KnownDifferenceCount: knownDifferenceCount,
            MissingDifferenceCount: missingDifferenceCount,
            TotalDifference: totalDifference,
            AverageDifference: averageDifference,
            MedianDifference: medianDifference,
            StandardDeviation: variance is null ? null : Math.Sqrt(variance.Value),
            TotalGames: totalGames,
            AverageGames: unitCount == 0 ? 0 : (double)totalGames / unitCount,
            TotalSpins: totalSpins,
            DetailSpins: detailValues.Sum(row => row.TotalSpins),
            Wins: wins,
            WinRate: wins is null || unitCount == 0
                ? null
                : (double?)wins.Value / unitCount * 100,
            CalculatedRate: totalGames == 0 || totalDifference is null
                ? null
                : 100d + (double)totalDifference.Value / (totalGames * 3d) * 100d,
            AveragePayoutRate: knownRateCount != unitCount ? null : knownRates.Average(),
            Rate105OrMore: knownRateCount != unitCount
                ? null
                : (double)knownRates.Count(rate => rate >= 105) / knownRateCount * 100,
            Rate110OrMore: knownRateCount != unitCount
                ? null
                : (double)knownRates.Count(rate => rate >= 110) / knownRateCount * 100,
            BbTotal: values.Sum(row => row.Bb ?? 0),
            RbTotal: values.Sum(row => row.Rb ?? 0),
            DetailRows: values.Count(row => row.Bb is not null || row.Rb is not null),
            ZeroGameRows: values.Count(row => row.Games == 0),
            SourceUrls: string.Join(
                " ",
                values.Select(row => row.SourceUrl)
                    .Distinct()
                    .Order(StringComparer.Ordinal)));
    }

    private static string[] DailyHeaders(string axis)
        =>
        [
            "店舗", "日付", axis, "対象台数", "稼働台数",
            "差枚取得台数", "差枚非掲載台数", "総差枚",
            "平均差枚", "平均G数", "勝ち台数", "勝率(%)",
            "計算出率(%)", "出典URL",
        ];

    private static string[] PeriodHeaders(string axis)
        =>
        [
            "店舗", axis, "対象日数", "台日数", "稼働台日数",
            "差枚取得台日数", "差枚非掲載台日数", "総差枚",
            "1台1日平均差枚", "1台1日平均G数", "勝ち台日数",
            "勝率(%)", "計算出率(%)", "出典URL",
        ];

    private static void WriteCsv(
        string path,
        IReadOnlyList<string> headers,
        IEnumerable<object?[]> records)
    {
        using var writer = new StreamWriter(
            path,
            append: false,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        writer.WriteLine(string.Join(",", headers.Select(EscapeCsv)));
        foreach (var record in records)
        {
            writer.WriteLine(string.Join(",", record.Select(FormatCsvValue).Select(EscapeCsv)));
        }
    }

    private static string FormatCsvValue(object? value)
        => value switch
        {
            null => string.Empty,
            DateOnly date => date.ToString("yyyy-MM-dd", Invariant),
            double number => number.ToString("0.00", Invariant),
            float number => number.ToString("0.00", Invariant),
            IFormattable formattable => formattable.ToString(null, Invariant),
            _ => value.ToString() ?? string.Empty,
        };

    private static string EscapeCsv(string value)
    {
        if (value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0)
        {
            return value;
        }

        var escapedValue = value.Replace("\"", "\"\"");
        return "\"" + escapedValue + "\"";
    }

    private sealed record MachineDay(
        (string Store, DateOnly ReportDate, string Machine) Key,
        IReadOnlyList<SlotRow> Rows);

    private sealed record PeriodBucket(string Label, DateOnly Start, DateOnly End);

    private sealed record AggregateSummary(
        int UnitCount,
        int ActiveCount,
        int KnownDifferenceCount,
        int MissingDifferenceCount,
        int? TotalDifference,
        double? AverageDifference,
        double? MedianDifference,
        double? StandardDeviation,
        int TotalGames,
        double AverageGames,
        int TotalSpins,
        int DetailSpins,
        int? Wins,
        double? WinRate,
        double? CalculatedRate,
        double? AveragePayoutRate,
        double? Rate105OrMore,
        double? Rate110OrMore,
        int BbTotal,
        int RbTotal,
        int DetailRows,
        int ZeroGameRows,
        string SourceUrls);
}

/// <summary>
/// 1件の取得・解析失敗。成功分は破棄せず、取得情報JSONへ記録します。
/// </summary>
public sealed record ExtractionFailure(
    string Url,
    string Error,
    string Location = "処理全体");
