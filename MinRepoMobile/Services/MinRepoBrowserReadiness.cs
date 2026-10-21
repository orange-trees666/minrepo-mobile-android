using System.Globalization;

namespace MinRepoMobile.Services;

/// <summary>
/// HTTP成功や画面読込完了だけでなく、取得対象の実データを検査します。
/// 対象表だけの指紋を使い、広告・時計の更新で待機が終わらなくなることを防ぎます。
/// </summary>
public static class MinRepoBrowserReadiness
{
    public static string? GetFingerprint(string html, Uri uri)
    {
        var parser = new MinRepoHtmlParser();
        try
        {
            if (uri.AbsolutePath.StartsWith("/tag/", StringComparison.OrdinalIgnoreCase))
            {
                var links = parser.ExtractReportLinks(html, uri);
                return links.Count == 0 ? null : string.Join("|", links);
            }

            var date = parser.ExtractReportDate(html);
            if (uri.Query.Equals("?kishu=all", StringComparison.OrdinalIgnoreCase))
            {
                var report = parser.ParseReport(html, uri, continueOnRowError: true);
                if (report.Rows.Count + report.PendingRows.Count == 0) return null;
                return date.ToString("O", CultureInfo.InvariantCulture) + "|" +
                    string.Join("|", report.Rows.Select(row => row.ToString())) + "|" +
                    string.Join("|", report.PendingRows.Select(row => row.ToString()));
            }

            if (uri.Query.StartsWith("?kishu=", StringComparison.OrdinalIgnoreCase))
            {
                var details = parser.ParseMachineDetails(html, uri, continueOnRowError: true);
                return details.Count == 0 ? null : string.Join("|", details.Values);
            }

            // トップの勝率分母が現れた後で返し、下部ランキングだけの誤採用を防ぎます。
            var units = parser.ExtractExpectedUnitCount(html);
            return units is null ? null : $"{date:yyyy-MM-dd}|{units}|{parser.ExtractReportedTotalDifference(html)}";
        }
        catch (Exception ex) when (ex is InvalidDataException or FormatException or ArgumentOutOfRangeException)
        {
            return null; // 確認用ページ・描画途中の表は完成を待ちます。
        }
    }

    /// <summary>
    /// 別日・別機種・広告へ移動した画面を、要求したレポートとして扱いません。
    /// www有無とフラグメントだけを許容し、パスとクエリは同一であることを要求します。
    /// </summary>
    public static bool IsRequestedDocument(Uri requested, Uri actual)
        => MinRepoUrl.IsAllowedHost(actual) &&
           requested.AbsolutePath.TrimEnd('/').Equals(actual.AbsolutePath.TrimEnd('/'), StringComparison.Ordinal) &&
           requested.Query.Equals(actual.Query, StringComparison.Ordinal);
}
