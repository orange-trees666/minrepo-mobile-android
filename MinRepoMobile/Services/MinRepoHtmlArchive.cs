using System.Text.Json;
using System.Text.RegularExpressions;
using MinRepoMobile.Models;

namespace MinRepoMobile.Services;

/// <summary>
/// 通常のChromeで取得した公開HTMLを、通信せず既存の抽出処理へ渡します。
/// Cookieや認証情報を移し替えず、ページ不足・重複・許可外URLは明示的に拒否します。
/// HTMLは解析するだけで、WebViewへの読込やスクリプト実行は行いません。
/// </summary>
public sealed class MinRepoHtmlArchive : IMinRepoPageSource
{
    public const int MaximumBytes = 128 * 1024 * 1024;
    private readonly Dictionary<string, string> _pages;
    private readonly ArchiveDocument _document;

    private MinRepoHtmlArchive(ArchiveDocument document, Dictionary<string, string> pages)
        => (_document, _pages) = (document, pages);

    public string SourceUrl => _document.SourceUrl;
    public int PageCount => _pages.Count;

    /// <summary>Androidのファイルストリームでも、サイズ上限と中止を守って読み込みます。</summary>
    public static async Task<MinRepoHtmlArchive> LoadAsync(Stream input, CancellationToken token)
    {
        using var data = new MemoryStream();
        var buffer = new byte[81920];
        int count;
        while ((count = await input.ReadAsync(buffer, token)) != 0)
        {
            if (data.Length + count > MaximumBytes)
                throw new InvalidDataException("取得ファイルが128MBを超えています。期間を短くして取得してください。");
            await data.WriteAsync(buffer.AsMemory(0, count), token);
        }
        data.Position = 0;
        ArchiveDocument? document;
        try
        {
            document = await JsonSerializer.DeserializeAsync<ArchiveDocument>(data,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, token);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("みんレポのChrome取得ファイルを選択してください。", ex);
        }
        if (document is null || document.Format != "minrepo-browser-pages" || document.Version != 1 ||
            document.Pages is null || document.Pages.Count is 0 or > 2000)
            throw new InvalidDataException("対応していない取得ファイルです。Chromeの取得機能で作り直してください。");

        var source = MinRepoUrl.Normalize(document.SourceUrl);
        if (source.Query.Length != 0 || !IsPagePath(source))
            throw new InvalidDataException("取得元URLは店舗一覧または日別レポートを指定してください。");
        document.SourceUrl = source.AbsoluteUri;
        if (document.FromDate > document.ToDate)
            throw new InvalidDataException("取得ファイルの開始日と終了日が逆になっています。");

        var pages = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var page in document.Pages)
        {
            token.ThrowIfCancellationRequested();
            if (page is null || string.IsNullOrWhiteSpace(page.Html) || page.Html.Length > 4 * 1024 * 1024 ||
                !Uri.TryCreate(page.Url, UriKind.Absolute, out var uri))
                throw new InvalidDataException("取得ファイルに空または過大なページが含まれています。");
            var key = PageKey(uri);
            if (!Regex.IsMatch(page.Html, @"<h1\b", RegexOptions.IgnoreCase))
                throw new InvalidDataException("確認用ページが含まれています。Chromeでデータが表示される状態で取得してください。");
            if (!pages.TryAdd(key, page.Html))
                throw new InvalidDataException("同じURLのページが重複しています。取得ファイルを作り直してください。");
        }
        if (!pages.ContainsKey(PageKey(source)))
            throw new InvalidDataException("取得元のページがファイルに含まれていません。");
        return new(document, pages);
    }

    public Task<string> GetTextAsync(Uri uri, TimeSpan delay, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!_pages.TryGetValue(PageKey(uri), out var html))
            throw new InvalidDataException($"取得ファイルに必要なページがありません: {uri}。Chromeで対象期間を取得し直してください。");
        return Task.FromResult(html); // 保存済みデータだけを参照し、通信へのフォールバックはしません。
    }

    /// <summary>取得ファイルの対象を採用し、既存の全台数・総差枚照合を含む完全取得を実行します。</summary>
    public ExtractionRequest CreateRequest()
    {
        var source = new Uri(SourceUrl);
        var reports = _pages.Keys.Select(u => new Uri(u))
            .Count(u => Regex.IsMatch(u.AbsolutePath, @"^/\d+/$") && u.Query.Length == 0);
        var lists = _pages.Keys.Select(u => new Uri(u))
            .Count(u => u.AbsolutePath.StartsWith("/tag/", StringComparison.OrdinalIgnoreCase));
        return new(SourceUrl, source.AbsolutePath.StartsWith("/tag/", StringComparison.OrdinalIgnoreCase),
            _document.FromDate, _document.ToDate, Math.Max(1, lists), Math.Max(1, reports), TimeSpan.Zero,
            AggregationPeriods.Day | AggregationPeriods.Week | AggregationPeriods.Month,
            AggregationKeys.Machine | AggregationKeys.PayoutBand | AggregationKeys.Ending | AggregationKeys.InstalledUnits,
            _document.FetchBonusDetails, false);
    }

    private static bool IsPagePath(Uri uri)
        => uri.AbsolutePath.StartsWith("/tag/", StringComparison.OrdinalIgnoreCase) ||
           Regex.IsMatch(uri.AbsolutePath, @"^/\d+/$");

    private static string PageKey(Uri uri)
    {
        if (!MinRepoUrl.IsAllowedHost(uri) || !IsPagePath(uri))
            throw new InvalidDataException("取得ファイルに許可されていないURLが含まれています。");
        if (uri.Query.Length != 0 && !MinRepoUrl.TryNormalizeDetailLink(uri, out _))
            throw new InvalidDataException("取得ファイルのページURLが正しくありません。");
        var builder = new UriBuilder(uri) { Host = "min-repo.com", Fragment = "" };
        return builder.Uri.AbsoluteUri;
    }

    public sealed class ArchiveDocument
    {
        public string Format { get; set; } = "";
        public int Version { get; set; }
        public string SourceUrl { get; set; } = "";
        public DateOnly? FromDate { get; set; }
        public DateOnly? ToDate { get; set; }
        public bool FetchBonusDetails { get; set; }
        public List<ArchivePage>? Pages { get; set; }
    }
    public sealed class ArchivePage
    {
        public string Url { get; set; } = "";
        public string Html { get; set; } = "";
    }
}
