namespace MinRepoMobile.Models;

[Flags]
public enum AggregationPeriods
{
    None = 0,
    Day = 1,
    Week = 2,
    Month = 4,
}

[Flags]
public enum AggregationKeys
{
    None = 0,
    Machine = 1,
    PayoutBand = 2,
    Ending = 4,
    InstalledUnits = 8,
}

/// <summary>
/// 画面からサービスへ渡す取得条件。
/// UI型を含めないため、将来別画面や自動化処理からも再利用できます。
/// </summary>
public sealed record ExtractionRequest(
    string SourceUrl,
    bool IsStoreMode,
    DateOnly? FromDate,
    DateOnly? ToDate,
    int MaxListPages,
    int MaxReports,
    TimeSpan Delay,
    AggregationPeriods Periods,
    AggregationKeys Keys,
    bool FetchBonusDetails,
    bool CreatePartialOutput);

/// <summary>
/// 1日・1台単位の明細。全ての集計はこのデータから作成します。
/// </summary>
public sealed record SlotRow(
    string Store,
    DateOnly ReportDate,
    string Machine,
    int UnitNumber,
    int Ending,
    int Difference,
    int Games,
    int TotalSpins,
    double? PayoutRate,
    int? Bb,
    int? Rb,
    string? CombinedRate,
    string? BbRate,
    string? RbRate,
    string SourceUrl,
    string? DetailUrl);

/// <summary>
/// 機種別詳細ページから取得するBB/RB等の台別補足情報。
/// </summary>
public sealed record MachineDetail(
    int UnitNumber,
    int Difference,
    int Games,
    int TotalSpins,
    double? PayoutRate,
    int? Bb,
    int? Rb,
    string? CombinedRate,
    string? BbRate,
    string? RbRate,
    string SourceUrl);

/// <summary>
/// 全台ページで差枚または出率が省略表示されていた台。
/// 台番・機種・詳細URLを保持し、機種詳細ページから完全な値を復元します。
/// </summary>
public sealed record PendingSlotRow(
    string Store,
    DateOnly ReportDate,
    string Machine,
    int UnitNumber,
    int Ending,
    string SourceUrl,
    string DetailUrl,
    string OriginalCells);

/// <summary>
/// 日別レポート1件の解析結果。
/// </summary>
public sealed record ReportResult(
    string Store,
    DateOnly ReportDate,
    string SourceUrl,
    IReadOnlyList<SlotRow> Rows,
    IReadOnlyList<PendingSlotRow> PendingRows);

/// <summary>
/// 画面へ通知する進捗。Ratioは0～1の範囲です。
/// </summary>
public sealed record ExtractionProgress(string Message, double Ratio);

/// <summary>
/// 完了時に画面へ返す結果。ZIPの共有に必要なパスと件数を保持します。
/// </summary>
public sealed record ExtractionResult(
    string ZipPath,
    int ReportCount,
    int RowCount,
    int FailureCount);
