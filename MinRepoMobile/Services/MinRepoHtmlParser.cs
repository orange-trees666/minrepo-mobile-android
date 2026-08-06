using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using MinRepoMobile.Models;

namespace MinRepoMobile.Services;

/// <summary>
/// みんレポの公開HTMLを、外部ライブラリなしで必要部分だけ解析します。
/// ページ全体を汎用DOM化せず、対象見出し直後の表へ範囲を限定することで、
/// 画面下部の別店舗ランキングを誤取得しないようにしています。
/// </summary>
public sealed partial class MinRepoHtmlParser
{
    private static readonly RegexOptions CommonOptions =
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled;

    private static readonly Regex RowRegex =
        new(@"<tr\b[^>]*>(.*?)</tr\s*>", CommonOptions);

    private static readonly Regex CellRegex =
        new(@"<t[dh]\b[^>]*>(.*?)</t[dh]\s*>", CommonOptions);

    private static readonly Regex TableRegex =
        new(@"<table\b[^>]*>.*?</table\s*>", CommonOptions);

    private static readonly Regex HeadingRegex =
        new(@"<h[1-4]\b[^>]*>(.*?)</h[1-4]\s*>", CommonOptions);

    private static readonly Regex AnchorRegex =
        new(
            @"<a\b[^>]*\bhref\s*=\s*([""'])(.*?)\1[^>]*>(.*?)</a\s*>",
            CommonOptions);

    private static readonly Regex TagRegex =
        new(@"<[^>]+>", RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex SpaceRegex =
        new(@"\s+", RegexOptions.Compiled);

    private static readonly Regex ReportPathRegex =
        new(@"^/\d+/?$", RegexOptions.Compiled);

    /// <summary>
    /// 店舗ページのデータ一覧表から日別レポートURLだけを取り出します。
    /// </summary>
    public IReadOnlyList<Uri> ExtractReportLinks(string pageHtml, Uri baseUri)
    {
        var tableHtml = ExtractFirstTableAfterHeading(pageHtml, @"データ\s*一覧");
        var results = new List<Uri>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in AnchorRegex.Matches(tableHtml))
        {
            var href = WebUtility.HtmlDecode(match.Groups[2].Value).Trim();
            var label = CleanText(match.Groups[3].Value);

            if (!Uri.TryCreate(baseUri, href, out var absolute) ||
                !MinRepoUrl.IsAllowedHost(absolute) ||
                !ReportPathRegex.IsMatch(absolute.AbsolutePath) ||
                !Regex.IsMatch(label, @"\d{1,2}/\d{1,2}"))
            {
                continue;
            }

            var canonical = MinRepoUrl.Normalize(absolute.ToString(), allowAllQuery: false);
            if (seen.Add(canonical.AbsoluteUri))
            {
                results.Add(canonical);
            }
        }

        return results;
    }

    /// <summary>
    /// 日別レポートトップの「勝率 勝ち台数/全台数」から全台数を取得します。
    /// 全台ページの解析件数と照合し、途中までしか取得できない状態を検出します。
    /// </summary>
    public int? ExtractExpectedUnitCount(string pageHtml)
    {
        var visibleText = CleanText(pageHtml);
        var match = Regex.Match(
            visibleText,
            @"勝率\s*\d[\d,]*\s*/\s*(?<total>\d[\d,]*)",
            RegexOptions.IgnoreCase);
        if (!match.Success ||
            !TryParseInteger(match.Groups["total"].Value, out var total) ||
            total <= 0)
        {
            return null;
        }

        return total;
    }

    /// <summary>
    /// 日別レポートトップに掲載された店舗全体の総差枚を取得します。
    /// 全台明細の合計と照合し、符号や行の取りこぼしを検出します。
    /// </summary>
    public int? ExtractReportedTotalDifference(string pageHtml)
    {
        var visibleText = CleanText(pageHtml);
        var match = Regex.Match(
            visibleText,
            @"総差枚\s*(?<difference>[+\-−ー]?\s*\d[\d,]*)",
            RegexOptions.IgnoreCase);
        if (!match.Success ||
            !TryParseInteger(
                match.Groups["difference"].Value,
                out var totalDifference))
        {
            return null;
        }

        return totalDifference;
    }

    /// <summary>
    /// 全台データページを店舗名・日付・台明細へ変換します。
    /// </summary>
    public ReportResult ParseReport(
        string pageHtml,
        Uri sourceUri,
        bool continueOnRowError = false,
        Action<string>? reportIssue = null)
    {
        var (store, reportDate) = ParseReportIdentity(pageHtml);

        var reportTables = ExtractAllReportTables(
            pageHtml,
            @"全台(?:\s|&nbsp;|&#x3000;|　)*データ\s*一覧");

        var rows = new List<SlotRow>();
        var rowsByUnitNumber = new Dictionary<int, SlotRow>();
        var pendingRows = new List<PendingSlotRow>();
        var pendingByUnitNumber = new Dictionary<int, PendingSlotRow>();
        var matchedTableCount = 0;

        foreach (var tableHtml in reportTables)
        {
            // 広告表や集計表を誤取得しないよう、必要な列見出しを持つ表だけを対象にします。
            // 列番号は見出しから求めるため、サイト側で列順が変わっても解析できます。
            if (!TryFindColumnIndexes(
                    tableHtml,
                    out var columns,
                    new[] { "機種" },
                    new[] { "台番", "台番号" },
                    new[] { "差枚", "差枚数" },
                    new[] { "G数", "ゲーム数", "総回転数" },
                    new[] { "出率", "機械割" }))
            {
                continue;
            }

            matchedTableCount++;
            var machineColumn = columns[0];
            var unitColumn = columns[1];
            var differenceColumn = columns[2];
            var gamesColumn = columns[3];
            var payoutColumn = columns[4];
            var largestRequiredColumn = columns.Max();

            foreach (Match rowMatch in RowRegex.Matches(tableHtml))
            {
                var cellFragments = CellRegex
                    .Matches(rowMatch.Groups[1].Value)
                    .ToArray();
                var cells = cellFragments
                    .Select(match => CleanText(match.Groups[1].Value))
                    .ToArray();

                // 台番列がない行や、台番が整数でない繰り返し見出しは除外します。
                if (cells.Length <= unitColumn ||
                    !TryParseInteger(cells[unitColumn], out var unitNumber))
                {
                    continue;
                }

                var machine = cells.Length > machineColumn
                    ? cells[machineColumn]
                    : string.Empty;
                string? detailUrl = null;
                if (cellFragments.Length > machineColumn)
                {
                    var hrefMatch = AnchorRegex.Match(
                        cellFragments[machineColumn].Groups[1].Value);
                    if (hrefMatch.Success &&
                        Uri.TryCreate(
                            sourceUri,
                            WebUtility.HtmlDecode(hrefMatch.Groups[2].Value).Trim(),
                            out var candidate) &&
                        MinRepoUrl.TryNormalizeDetailLink(
                            candidate,
                            out var normalizedDetail))
                    {
                        detailUrl = normalizedDetail.AbsoluteUri;
                    }
                }

                // みんレポの全台ページでは、マイナス差枚台の差枚・出率が
                // AndroidのHTTP取得結果で「-」だけになる場合があります。
                // 機種名・台番・機種詳細URLが取れていれば行を破棄せず保留し、
                // 詳細ページの完全な値から後段で復元します。
                if (cells.Length <= largestRequiredColumn ||
                    !TryParseInteger(cells[differenceColumn], out var difference) ||
                    !TryParseInteger(cells[gamesColumn], out var games) ||
                    !TryParsePercent(cells[payoutColumn], out var payoutRate))
                {
                    var joinedCells = string.Join(" | ", cells);
                    var message =
                        $"台番{unitNumber}の全台データを解析できませんでした。" +
                        $" 列内容: {joinedCells}";
                    if (!string.IsNullOrWhiteSpace(machine) &&
                        !string.IsNullOrWhiteSpace(detailUrl))
                    {
                        if (!pendingByUnitNumber.ContainsKey(unitNumber) &&
                            !rowsByUnitNumber.ContainsKey(unitNumber))
                        {
                            // 後続のRemoveで受け取る変数と名前を分けます。
                            // C#では内側のブロックであっても、同じ外側スコープ内に
                            // 同名のローカル変数があるとCS0136になるためです。
                            var pendingRow = new PendingSlotRow(
                                Store: store,
                                ReportDate: reportDate,
                                Machine: machine,
                                UnitNumber: unitNumber,
                                Ending: Math.Abs(unitNumber % 10),
                                SourceUrl: sourceUri.AbsoluteUri,
                                DetailUrl: detailUrl,
                                OriginalCells: joinedCells);
                            pendingByUnitNumber.Add(unitNumber, pendingRow);
                            pendingRows.Add(pendingRow);
                        }

                        continue;
                    }

                    if (!continueOnRowError)
                    {
                        throw new InvalidDataException(message);
                    }

                    reportIssue?.Invoke(message);
                    continue;
                }

                if (string.IsNullOrWhiteSpace(machine))
                {
                    var joinedCells = string.Join(" | ", cells);
                    var message =
                        $"台番{unitNumber}の機種名が空です。 列内容: {joinedCells}";
                    if (!continueOnRowError)
                    {
                        throw new InvalidDataException(message);
                    }

                    reportIssue?.Invoke(message);
                    continue;
                }

                var parsedRow = new SlotRow(
                    Store: store,
                    ReportDate: reportDate,
                    Machine: machine,
                    UnitNumber: unitNumber,
                    Ending: Math.Abs(unitNumber % 10),
                    Difference: difference,
                    Games: games,
                    TotalSpins: games,
                    PayoutRate: payoutRate,
                    Bb: null,
                    Rb: null,
                    CombinedRate: null,
                    BbRate: null,
                    RbRate: null,
                    SourceUrl: sourceUri.AbsoluteUri,
                    DetailUrl: detailUrl);

                if (rowsByUnitNumber.TryGetValue(unitNumber, out var existing))
                {
                    // レスポンシブ表示用に同一表が重複している場合は1台として扱います。
                    // 同じ台番で内容が違う場合は、どちらを採用するか推測せずエラーにします。
                    if (!IsSameUnitRow(existing, parsedRow))
                    {
                        var message =
                            $"台番{unitNumber}が異なる内容で複数回掲載されています。";
                        if (!continueOnRowError)
                        {
                            throw new InvalidDataException(message);
                        }

                        reportIssue?.Invoke(
                            message + " 部分出力のため先に取得した行を採用しました。");
                    }

                    continue;
                }

                rowsByUnitNumber.Add(unitNumber, parsedRow);
                rows.Add(parsedRow);
                if (pendingByUnitNumber.Remove(
                        unitNumber,
                        out var removedPendingRow))
                {
                    pendingRows.Remove(removedPendingRow);
                }
            }
        }

        if (matchedTableCount == 0)
        {
            throw new InvalidDataException(
                "「機種・台番・差枚・G数・出率」の列を持つ全台表が見つかりません。");
        }

        if (rows.Count == 0 && pendingRows.Count == 0)
        {
            throw new InvalidDataException(
                "全台表から台データを読み取れませんでした。サイト構造変更の可能性があります。");
        }

        return new ReportResult(
            store,
            reportDate,
            sourceUri.AbsoluteUri,
            rows,
            pendingRows);
    }

    /// <summary>
    /// 日別レポートの表を解析する前に、店舗名と対象日だけを取得します。
    /// 期間外ページを全台・機種詳細解析の前に除外するためにも使用します。
    /// </summary>
    public DateOnly ExtractReportDate(string pageHtml)
        => ParseReportIdentity(pageHtml).ReportDate;

    private static (string Store, DateOnly ReportDate) ParseReportIdentity(
        string pageHtml)
    {
        var headingMatch = Regex.Match(
            pageHtml,
            @"<h1\b[^>]*>(.*?)</h1\s*>",
            CommonOptions);
        if (!headingMatch.Success)
        {
            throw new InvalidDataException("レポート名（h1）が見つかりません。");
        }

        var title = CleanText(headingMatch.Groups[1].Value);
        var reportDayMatch = Regex.Match(
            title,
            @"(?<month>\d{1,2})/(?<day>\d{1,2})");
        var publishedMatch = Regex.Match(
            CleanText(pageHtml),
            @"(?<year>20\d{2})年\s*(?<month>\d{1,2})月\s*(?<day>\d{1,2})日");
        if (!reportDayMatch.Success || !publishedMatch.Success)
        {
            throw new InvalidDataException("レポート日または掲載年を判定できません。");
        }

        var reportMonth = ParseGroupInt(reportDayMatch, "month");
        var reportDay = ParseGroupInt(reportDayMatch, "day");
        var publishedYear = ParseGroupInt(publishedMatch, "year");
        var publishedMonth = ParseGroupInt(publishedMatch, "month");
        var reportYear = reportMonth - publishedMonth > 6
            ? publishedYear - 1
            : publishedYear;

        var store = Regex.Replace(
            title,
            @"^\s*\d{1,2}/\d{1,2}\s*(?:\([^)]*\))?\s*",
            string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(store))
        {
            throw new InvalidDataException("店舗名を判定できません。");
        }

        return (store, new DateOnly(reportYear, reportMonth, reportDay));
    }

    /// <summary>
    /// 機種別詳細ページの「台番・差枚・G数・出率・BB・RB・合成・BB率・RB率」を
    /// 台番キーの補足情報へ変換します。
    /// </summary>
    public IReadOnlyDictionary<int, MachineDetail> ParseMachineDetails(
        string pageHtml,
        Uri sourceUri,
        bool continueOnRowError = false,
        Action<string>? reportIssue = null)
    {
        var reportTables = ExtractAllReportTables(pageHtml, @"データ\s*一覧");
        var details = new Dictionary<int, MachineDetail>();
        var matchedTableCount = 0;

        foreach (var tableHtml in reportTables)
        {
            // グラフ用の「差枚・G数・合成」表は除外し、
            // 台番・差枚・G数・出率を持つデータ表だけを解析します。
            if (!TryFindColumnIndexes(
                    tableHtml,
                    out var requiredColumns,
                    new[] { "台番", "台番号" },
                    new[] { "差枚", "差枚数" },
                    new[] { "G数", "ゲーム数", "総回転数" },
                    new[] { "出率", "機械割" }))
            {
                continue;
            }

            matchedTableCount++;
            var unitColumn = requiredColumns[0];
            var differenceColumn = requiredColumns[1];
            var gamesColumn = requiredColumns[2];
            var payoutColumn = requiredColumns[3];
            var largestRequiredColumn = requiredColumns.Max();

            var headers = ReadHeaderCells(tableHtml);
            var bbColumn = FindHeaderIndex(headers, "BB", "BIG");
            var rbColumn = FindHeaderIndex(headers, "RB", "REG");
            var combinedColumn = FindHeaderIndex(headers, "合成", "合算");
            var bbRateColumn = FindHeaderIndex(headers, "BB率", "BIG率");
            var rbRateColumn = FindHeaderIndex(headers, "RB率", "REG率");

            foreach (Match rowMatch in RowRegex.Matches(tableHtml))
            {
                var cells = CellRegex
                    .Matches(rowMatch.Groups[1].Value)
                    .Select(match => CleanText(match.Groups[1].Value))
                    .ToArray();

                // 「平均」行や繰り返し見出しは台番が整数ではないため除外します。
                if (cells.Length <= unitColumn ||
                    !TryParseInteger(cells[unitColumn], out var unitNumber))
                {
                    continue;
                }

                if (cells.Length <= largestRequiredColumn ||
                    !TryParseInteger(cells[differenceColumn], out var difference) ||
                    !TryParseInteger(cells[gamesColumn], out var games) ||
                    !TryParsePercent(cells[payoutColumn], out var payoutRate) ||
                    !TryParseOptionalIntegerCell(cells, bbColumn, out var bb) ||
                    !TryParseOptionalIntegerCell(cells, rbColumn, out var rb))
                {
                    var joinedCells = string.Join(" | ", cells);
                    var message =
                        $"台番{unitNumber}の機種詳細を解析できませんでした。" +
                        $" 列内容: {joinedCells}";
                    if (!continueOnRowError)
                    {
                        throw new InvalidDataException(message);
                    }

                    reportIssue?.Invoke(message);
                    continue;
                }

                var parsedDetail = new MachineDetail(
                    UnitNumber: unitNumber,
                    Difference: difference,
                    Games: games,
                    TotalSpins: games,
                    PayoutRate: payoutRate,
                    Bb: bb,
                    Rb: rb,
                    CombinedRate: ReadOptionalRate(cells, combinedColumn),
                    BbRate: ReadOptionalRate(cells, bbRateColumn),
                    RbRate: ReadOptionalRate(cells, rbRateColumn),
                    SourceUrl: sourceUri.AbsoluteUri);

                if (details.TryGetValue(unitNumber, out var existing))
                {
                    if (existing != parsedDetail)
                    {
                        var message =
                            $"台番{unitNumber}の機種詳細が異なる内容で複数回掲載されています。";
                        if (!continueOnRowError)
                        {
                            throw new InvalidDataException(message);
                        }

                        reportIssue?.Invoke(
                            message + " 部分出力のため先に取得した行を採用しました。");
                    }

                    continue;
                }

                details.Add(unitNumber, parsedDetail);
            }
        }

        if (matchedTableCount == 0)
        {
            throw new InvalidDataException(
                "「台番・差枚・G数・出率」の列を持つ機種詳細表が見つかりません。");
        }

        if (details.Count == 0)
        {
            throw new InvalidDataException(
                "機種別詳細ページからBB/RBデータを読み取れませんでした。");
        }

        return details;
    }

    private static string ExtractFirstTableAfterHeading(
        string pageHtml,
        string headingKeywordPattern)
    {
        var heading = FindHeading(pageHtml, headingKeywordPattern);
        if (!heading.Success)
        {
            throw new InvalidDataException(
                $"対象のデータ見出しを検出できません: {headingKeywordPattern}");
        }

        // 見出しの終了位置から後ろだけを解析対象にします。
        //
        // `pageHtml[heading.Index + heading.Length..]` と記述すると、
        // コンパイラーが `heading.Length..` を Range と解釈し、
        // `int + Range` の加算として扱う場合があります。
        // Substringを使用して開始位置を明示し、C#の構文解釈に依存しない形にします。
        var afterHeading = pageHtml.Substring(heading.Index + heading.Length);
        var table = TableRegex.Match(afterHeading);
        if (!table.Success)
        {
            throw new InvalidDataException("対象見出しの後にデータ表が見つかりません。");
        }

        return table.Value;
    }

    /// <summary>
    /// レポート内の対象見出しから「レポートトップに戻る」リンク直前までにある
    /// 全table要素を取得します。
    /// </summary>
    /// <remarks>
    /// みんレポでは、画面上はひとつの一覧に見えてもHTML内部で複数のtableへ
    /// 分割される場合があります。最初のtableだけを取得すると、差枚順の後半に
    /// ある台が欠落するため、明示的な戻るリンクを終端として全表を結合します。
    /// </remarks>
    private static IReadOnlyList<string> ExtractAllReportTables(
        string pageHtml,
        string headingKeywordPattern)
    {
        var heading = FindHeading(pageHtml, headingKeywordPattern);
        if (!heading.Success)
        {
            throw new InvalidDataException(
                $"対象のデータ見出しを検出できません: {headingKeywordPattern}");
        }

        var afterHeading = pageHtml.Substring(heading.Index + heading.Length);
        // AnchorRegexでa要素を1件ずつ調べます。
        // Singlelineの「.*?」だけで戻るリンクを探すと、最初の機種リンクから
        // 戻るリンクまでをひとつのa要素として誤認する可能性があるためです。
        var reportTopLink = AnchorRegex
            .Matches(afterHeading)
            .FirstOrDefault(match =>
                RemoveWhitespace(CleanText(match.Groups[3].Value))
                    .Equals("レポートトップに戻る", StringComparison.Ordinal));
        if (reportTopLink is null)
        {
            throw new InvalidDataException(
                "データ一覧の終端「レポートトップに戻る」を検出できません。" +
                "サイト構造が変更された可能性があります。");
        }

        // Range構文を避け、古いVisual StudioのC#解析でも誤認されない形にします。
        var dataSection = afterHeading.Substring(0, reportTopLink.Index);
        var tables = TableRegex
            .Matches(dataSection)
            .Select(match => match.Value)
            .ToArray();
        if (tables.Length == 0)
        {
            throw new InvalidDataException(
                "データ一覧の範囲内にtable要素が見つかりません。");
        }

        return tables;
    }

    /// <summary>
    /// h1～h4を1要素ずつ確認し、指定文字列を含む見出しを返します。
    /// 複数の見出しをまたいで誤一致しないよう、見出し単位で判定します。
    /// </summary>
    private static Match FindHeading(
        string pageHtml,
        string headingKeywordPattern)
    {
        foreach (Match heading in HeadingRegex.Matches(pageHtml))
        {
            if (Regex.IsMatch(
                    heading.Groups[1].Value,
                    headingKeywordPattern,
                    RegexOptions.IgnoreCase | RegexOptions.Singleline))
            {
                return heading;
            }
        }

        return Match.Empty;
    }

    /// <summary>
    /// 表の見出し行から、指定された各列の実際の列番号を取得します。
    /// 各引数は同じ意味として許容する見出し名の配列です。
    /// </summary>
    private static bool TryFindColumnIndexes(
        string tableHtml,
        out int[] indexes,
        params string[][] aliasesByColumn)
    {
        var headers = ReadHeaderCells(tableHtml);
        if (headers.Length == 0)
        {
            indexes = Array.Empty<int>();
            return false;
        }

        indexes = new int[aliasesByColumn.Length];
        for (var index = 0; index < aliasesByColumn.Length; index++)
        {
            indexes[index] = FindHeaderIndex(headers, aliasesByColumn[index]);
            if (indexes[index] < 0)
            {
                indexes = Array.Empty<int>();
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// table内で最初に見つかったth行を見出しとして読み取ります。
    /// 分割表ごとに見出しを確認するため、列順が表ごとに異なっても対応できます。
    /// </summary>
    private static string[] ReadHeaderCells(string tableHtml)
    {
        foreach (Match rowMatch in RowRegex.Matches(tableHtml))
        {
            if (!Regex.IsMatch(
                    rowMatch.Groups[1].Value,
                    @"<th\b",
                    RegexOptions.IgnoreCase))
            {
                continue;
            }

            return CellRegex
                .Matches(rowMatch.Groups[1].Value)
                .Select(match => NormalizeHeader(match.Groups[1].Value))
                .ToArray();
        }

        return Array.Empty<string>();
    }

    /// <summary>
    /// 許容する見出し名のいずれかと一致する列番号を返します。
    /// </summary>
    private static int FindHeaderIndex(
        IReadOnlyList<string> headers,
        params string[] aliases)
    {
        for (var headerIndex = 0; headerIndex < headers.Count; headerIndex++)
        {
            foreach (var alias in aliases)
            {
                if (headers[headerIndex].Equals(
                        NormalizeHeader(alias),
                        StringComparison.OrdinalIgnoreCase))
                {
                    return headerIndex;
                }
            }
        }

        return -1;
    }

    /// <summary>
    /// BB/RB列がページに存在する場合だけ値を解析します。
    /// 列そのものがない機種ではnullとし、G数や出率の取得は継続します。
    /// </summary>
    private static bool TryParseOptionalIntegerCell(
        IReadOnlyList<string> cells,
        int columnIndex,
        out int? result)
    {
        if (columnIndex < 0)
        {
            result = null;
            return true;
        }

        if (cells.Count <= columnIndex)
        {
            result = null;
            return false;
        }

        return TryParseOptionalInteger(cells[columnIndex], out result);
    }

    /// <summary>
    /// 合成・BB率・RB率の列がある場合だけ値を返します。
    /// </summary>
    private static string? ReadOptionalRate(
        IReadOnlyList<string> cells,
        int columnIndex)
        => columnIndex >= 0 && cells.Count > columnIndex
            ? NormalizeRateText(cells[columnIndex])
            : null;

    private static string NormalizeHeader(string value)
        => RemoveWhitespace(CleanText(value))
            .Replace('（', '(')
            .Replace('）', ')');

    /// <summary>
    /// 同じ台番が複数のtableに現れた場合に、同一データかを判定します。
    /// 出典URLと詳細URLを含め、出力へ影響する全項目を比較します。
    /// </summary>
    private static bool IsSameUnitRow(SlotRow left, SlotRow right)
        => left == right;

    /// <summary>
    /// リンク文字列の改行・半角空白・全角空白を除去し、表示上の折り返しに
    /// 影響されず終端リンクを判定できるようにします。
    /// </summary>
    private static string RemoveWhitespace(string value)
        => Regex.Replace(value, @"[\s　]+", string.Empty);

    private static string CleanText(string fragment)
    {
        var withoutTags = TagRegex.Replace(fragment, " ");
        var decoded = WebUtility.HtmlDecode(withoutTags).Replace('　', ' ');
        return SpaceRegex.Replace(decoded, " ").Trim();
    }

    private static int ParseGroupInt(Match match, string groupName)
        => int.Parse(match.Groups[groupName].Value, CultureInfo.InvariantCulture);

    private static bool TryParseInteger(string value, out int result)
    {
        var normalized = value
            .Replace(",", string.Empty)
            .Replace(" ", string.Empty)
            .Replace("+", string.Empty)
            .Replace('−', '-')
            .Replace('ー', '-');

        return int.TryParse(
            normalized,
            NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture,
            out result);
    }

    private static bool TryParsePercent(string value, out double? result)
    {
        var normalized = value
            .Replace("%", string.Empty)
            .Replace(",", string.Empty)
            .Trim();

        if (string.IsNullOrEmpty(normalized) || normalized is "-" or "－")
        {
            result = null;
            return true;
        }

        if (double.TryParse(
            normalized,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var parsed))
        {
            result = parsed;
            return true;
        }

        result = null;
        return false;
    }

    private static bool TryParseOptionalInteger(string value, out int? result)
    {
        if (value.Trim() is "" or "-" or "－")
        {
            result = null;
            return true;
        }

        if (TryParseInteger(value, out var parsed))
        {
            result = parsed;
            return true;
        }

        result = null;
        return false;
    }

    private static string? NormalizeRateText(string value)
        => value.Trim() is "" or "-" or "－" ? null : value.Trim();
}
