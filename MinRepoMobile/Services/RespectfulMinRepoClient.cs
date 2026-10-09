using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;

namespace MinRepoMobile.Services;

/// <summary>
/// robots.txt確認、アクセス間隔、限定的再試行を担当します。
/// 同時実行をSemaphoreSlimで直列化し、端末から短時間に大量要求しないようにします。
/// </summary>
public sealed class RespectfulMinRepoClient
{
    private const string UserAgentProduct = "MinRepoMobileExtractor";
    private const string UserAgentVersion = "1.0.13";

    // みんレポの公開ページは、ブラウザー上のJavaScriptでこの2つのCookieを設定後、
    // 同じ公開ページを再表示したときに完全な差枚・出率を返します。
    // 任意のCookieは取り込まず、ページ自身が公開している対象名だけを許可します。
    private static readonly Regex PublicPageCookieRegex = new(
        """\$\.cookie\(\s*['"](?<name>_d2|_d_a2)['"]\s*,\s*['"](?<value>[^'"]+)['"]""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Uri SiteRoot = new("https://min-repo.com/");

    private readonly HttpClient _httpClient;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Func<TimeSpan, CancellationToken, Task> _waitAsync;
    private readonly CookieContainer _cookieContainer = new();
    private readonly Dictionary<string, string> _pageCookieValues =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _requestLock = new(1, 1);
    private DateTimeOffset _lastRequestAt = DateTimeOffset.MinValue;
    private RobotsPolicy? _robotsPolicy;

    public RespectfulMinRepoClient() : this(null) { }

    // テストでは時計と待機を差し替え、長いRetry-Afterも実時間を待たず検証できます。
    internal RespectfulMinRepoClient(
        HttpMessageHandler? handler,
        Func<DateTimeOffset>? utcNow = null,
        Func<TimeSpan, CancellationToken, Task>? waitAsync = null)
    {
        handler ??= new HttpClientHandler
        {
            // リダイレクト先も許可ホスト・robots・取得間隔を検証してから通信します。
            AllowAutoRedirect = false,
            UseCookies = true,
            CookieContainer = _cookieContainer,
            AutomaticDecompression =
                DecompressionMethods.GZip |
                DecompressionMethods.Deflate |
                DecompressionMethods.Brotli,
        };

        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _waitAsync = waitAsync ?? ((delay, token) => Task.Delay(delay, token));

        _httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
        _httpClient.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue(UserAgentProduct, UserAgentVersion));
        _httpClient.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("text/html"));
        _httpClient.DefaultRequestHeaders.AcceptLanguage.ParseAdd("ja,en;q=0.5");
    }

    public async Task<string> GetTextAsync(
        Uri uri,
        TimeSpan delay,
        CancellationToken cancellationToken)
    {
        if (!MinRepoUrl.IsAllowedHost(uri))
        {
            throw new ArgumentException("許可されていない取得先です。", nameof(uri));
        }

        await _requestLock.WaitAsync(cancellationToken);
        try
        {
            // robots確認も同じロック・間隔に含め、複数呼出しによる初回の集中を防ぎます。
            await EnsureRobotsPolicyAsync(delay, cancellationToken);
            if (_robotsPolicy is null || !_robotsPolicy.CanFetch(uri.PathAndQuery))
            {
                throw new InvalidOperationException(
                    $"robots.txtにより自動取得が許可されていません: {uri}");
            }

            // 初回HTMLに公開Cookie設定が含まれていた場合だけ、ブラウザーと同じく
            // Cookieを反映して同じURLを1回再取得します。
            for (var pageAttempt = 0; pageAttempt < 2; pageAttempt++)
            {
                using var response = await SendWithRetryAsync(uri, delay, cancellationToken);
                response.EnsureSuccessStatusCode();
                var html = await response.Content.ReadAsStringAsync(cancellationToken);
                var cookieChanged = ApplyPublicPageCookies(html);
                if (pageAttempt == 0 && cookieChanged)
                {
                    await WaitForRequestIntervalAsync(delay, cancellationToken);
                    continue;
                }

                // 日別レポートは公開直後に台数が増える場合があるため、
                // 永続HTMLキャッシュは使用せず、実行ごとに最新ページを読みます。
                return html;
            }
        }
        finally
        {
            _requestLock.Release();
        }

        throw new HttpRequestException("ページを取得できませんでした。");
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(
        Uri uri,
        TimeSpan delay,
        CancellationToken cancellationToken)
    {
        var currentUri = uri;
        var redirects = 0;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await WaitForRequestIntervalAsync(delay, cancellationToken);
            _lastRequestAt = _utcNow();
            var response = await _httpClient.GetAsync(
                currentUri,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                var location = response.Headers.Location;
                response.Dispose();
                if (location is null || ++redirects > 5 ||
                    !Uri.TryCreate(currentUri, location.ToString(), out var nextUri) ||
                    !MinRepoUrl.IsAllowedHost(nextUri) ||
                    (_robotsPolicy is not null && !_robotsPolicy.CanFetch(nextUri.PathAndQuery)))
                {
                    throw new HttpRequestException("許可されていない、または過剰なリダイレクトです。");
                }

                currentUri = nextUri;
                attempt--; // リダイレクトは別の上限で管理し、障害再試行枠を消費しません。
                continue;
            }

            if (response.IsSuccessStatusCode)
            {
                return response;
            }

            var retryable =
                response.StatusCode == HttpStatusCode.TooManyRequests ||
                (int)response.StatusCode is 500 or 502 or 503 or 504;
            if (!retryable || attempt == 2)
            {
                return response; // 呼出し側がHTTP状態を検証し、必ずDisposeします。
            }

            var retryAfter = response.Headers.RetryAfter?.Delta
                ?? (response.Headers.RetryAfter?.Date is DateTimeOffset date
                    ? date - _utcNow()
                    : TimeSpan.FromSeconds(Math.Pow(2, attempt + 1)));
            response.Dispose();
            // サーバーの待機要求を60秒へ切り縮めず、入力間隔より短い再試行も防ぎます。
            await _waitAsync(retryAfter > delay ? retryAfter : delay, cancellationToken);
        }

        throw new HttpRequestException("ページを取得できませんでした。");
    }

    private async Task WaitForRequestIntervalAsync(
        TimeSpan delay,
        CancellationToken cancellationToken)
    {
        var elapsed = _utcNow() - _lastRequestAt;
        if (_lastRequestAt != DateTimeOffset.MinValue && elapsed < delay)
        {
            await _waitAsync(delay - elapsed, cancellationToken);
        }
    }

    private bool ApplyPublicPageCookies(string html)
    {
        var changed = false;
        foreach (var pair in ExtractPublicPageCookies(html))
        {
            // Cookie区切り文字や改行を含む異常値はCookieヘッダーへ渡しません。
            // 現在の公開値はBase64形式で、この条件には該当しません。
            if (pair.Value.IndexOfAny(new[] { ';', '\r', '\n' }) >= 0)
            {
                continue;
            }

            if (_pageCookieValues.TryGetValue(pair.Key, out var current) &&
                string.Equals(current, pair.Value, StringComparison.Ordinal))
            {
                continue;
            }

            // SetCookiesを使い、Cookieヘッダー用の書式検証とドメイン設定を
            // CookieContainerへ任せます。値をログやCSVへ出力することはありません。
            _cookieContainer.SetCookies(
                SiteRoot,
                $"{pair.Key}={pair.Value}; Path=/");
            _pageCookieValues[pair.Key] = pair.Value;
            changed = true;
        }

        return changed;
    }

    internal static IReadOnlyDictionary<string, string> ExtractPublicPageCookies(
        string html)
    {
        var results = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);
        foreach (Match match in PublicPageCookieRegex.Matches(html))
        {
            results[match.Groups["name"].Value] =
                WebUtility.HtmlDecode(match.Groups["value"].Value);
        }

        return results;
    }

    private async Task EnsureRobotsPolicyAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        if (_robotsPolicy is not null)
        {
            return;
        }

        var robotsUri = new Uri("https://min-repo.com/robots.txt");
        try
        {
            using var response = await SendWithRetryAsync(robotsUri, delay, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                _robotsPolicy = RobotsPolicy.AllowAll;
                return;
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"robots.txtを確認できません: HTTP {(int)response.StatusCode}");
            }

            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            _robotsPolicy = RobotsPolicy.Parse(text, UserAgentProduct);
        }
        catch (Exception ex) when (
            (ex is HttpRequestException or TaskCanceledException) &&
            !cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                "robots.txtを確認できなかったため、安全のため取得を中止しました。",
                ex);
        }
    }

    /// <summary>
    /// robots.txtのUser-agent、Allow、Disallowのみを解釈する最小ポリシーです。
    /// 最長一致を優先し、同じ長さならAllowを優先します。
    /// </summary>
    private sealed class RobotsPolicy
    {
        private readonly IReadOnlyList<Rule> _rules;

        private RobotsPolicy(IReadOnlyList<Rule> rules)
        {
            _rules = rules;
        }

        public static RobotsPolicy AllowAll { get; } =
            new(Array.Empty<Rule>());

        public static RobotsPolicy Parse(string text, string productName)
        {
            var groups = new List<(List<string> Agents, List<Rule> Rules)>();
            List<string>? currentAgents = null;
            List<Rule>? currentRules = null;

            foreach (var rawLine in text.Split('\n'))
            {
                var line = rawLine.Split('#', 2)[0].Trim();
                if (string.IsNullOrEmpty(line))
                {
                    continue;
                }

                var separator = line.IndexOf(':');
                if (separator < 0)
                {
                    continue;
                }

                var key = line.Substring(0, separator).Trim();
                var value = line.Substring(separator + 1).Trim();
                if (key.Equals("User-agent", StringComparison.OrdinalIgnoreCase))
                {
                    if (currentRules is { Count: > 0 })
                    {
                        currentAgents = null;
                        currentRules = null;
                    }

                    currentAgents ??= new List<string>();
                    currentRules ??= new List<Rule>();
                    if (!groups.Any(group => ReferenceEquals(group.Agents, currentAgents)))
                    {
                        groups.Add((currentAgents, currentRules));
                    }
                    currentAgents.Add(value);
                    continue;
                }

                if (currentRules is null ||
                    string.IsNullOrEmpty(value) ||
                    (!key.Equals("Allow", StringComparison.OrdinalIgnoreCase) &&
                     !key.Equals("Disallow", StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                currentRules.Add(new Rule(
                    Path: value,
                    IsAllowed: key.Equals("Allow", StringComparison.OrdinalIgnoreCase)));
            }

            var specificGroups = groups
                .Where(group => group.Agents.Any(agent =>
                    productName.Contains(agent, StringComparison.OrdinalIgnoreCase) ||
                    agent.Contains(productName, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            var wildcard = groups
                .Where(group => group.Agents.Any(agent => agent == "*"))
                .SelectMany(group => group.Rules)
                .ToList();

            // 専用User-agentグループが空ルールなら「全許可」であり、
            // ワイルドカードの禁止規則へフォールバックしてはいけません。
            return new RobotsPolicy(
                specificGroups.Count > 0
                    ? specificGroups.SelectMany(group => group.Rules).ToList()
                    : wildcard);
        }

        public bool CanFetch(string absolutePath)
        {
            var matching = _rules
                .Where(rule => rule.Matches(absolutePath))
                .OrderByDescending(rule => rule.Path.Count(c => c is not '*' and not '$'))
                .ThenByDescending(rule => rule.IsAllowed)
                .FirstOrDefault();

            return matching is null || matching.IsAllowed;
        }

        private sealed record Rule(string Path, bool IsAllowed)
        {
            // '*'は任意長、末尾'$'はURL終端。クエリ文字列も照合対象に含めます。
            public bool Matches(string pathAndQuery)
            {
                var pattern = Regex.Escape(Path.TrimEnd('$')).Replace("\\*", ".*");
                return Regex.IsMatch(pathAndQuery,
                    "^" + pattern + (Path.EndsWith('$') ? "$" : string.Empty),
                    RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            }
        }
    }
}
