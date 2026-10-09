using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using MinRepoMobile.Models;
using MinRepoMobile.Services;
using Microsoft.Maui.Storage;

// 例外が1件でも発生すると非ゼロ終了し、GitHub Actionsのマージ前検証を失敗させます。
var passed = 0;
try
{
    await Test("既存の分割表・差枚復元・全CSV自己テスト", async () =>
    {
        var result = await new MinRepoExtractionService().RunSelfTestAsync(null, default);
        Check(result.RowCount == 3 && result.FailureCount == 0, "自己テスト結果");
    });

    await Test("出率だけ欠損した台を詳細ページで補完", async () =>
    {
        var (service, handler) = ExtractionFixture("50", "1000", "-", "105%");
        var result = await service.ExtractAsync(Request(), null, default);
        Check(result.RowCount == 1 && result.FailureCount == 0, "補完後に完全取得");
        Check(handler.Requests.Any(r => r.Uri.Query == "?kishu=test"), "BB未選択でも詳細を取得");
        Check(ReadZip(result.ZipPath, "01_台別詳細.csv").Contains(",50,1000,1000,105.00,"), "正確な出率");
    });

    await Test("0G台の出率空欄は正常として保持", async () =>
    {
        var (service, handler) = ExtractionFixture("0", "0", "-", "-", "0");
        var result = await service.ExtractAsync(Request(), null, default);
        Check(result.RowCount == 1 && result.FailureCount == 0, "0G台の完全取得");
        Check(!handler.Requests.Any(r => r.Uri.Query == "?kishu=test"), "不要な詳細通信なし");
    });

    await Test("欠損した差枚の負数復元", async () =>
    {
        var (service, _) = ExtractionFixture("-", "1000", "-", "90%", "-300");
        var result = await service.ExtractAsync(Request(), null, default);
        Check(ReadZip(result.ZipPath, "01_台別詳細.csv").Contains(",-300,1000,1000,90.00,"), "負数の保持");
    });

    await Test("解析不能なG数を詳細から復元", async () =>
    {
        var (service, _) = ExtractionFixture("50", "解析不能", "105%", "105%");
        var result = await service.ExtractAsync(Request(), null, default);
        Check(result.RowCount == 1 && result.FailureCount == 0, "保留行の復元");
    });

    await Test("詳細取得失敗は完全取得モードで拒否", async () =>
    {
        var (service, _) = ExtractionFixture("50", "1000", "-", "105%", detailFails: true);
        await Throws<InvalidOperationException>(() => service.ExtractAsync(Request(), null, default));
    });

    await Test("詳細取得失敗でも部分モードで台と警告を保持", async () =>
    {
        var (service, _) = ExtractionFixture("50", "1000", "-", "105%", detailFails: true);
        var result = await service.ExtractAsync(Request() with { CreatePartialOutput = true }, null, default);
        Check(result.RowCount == 1 && result.FailureCount > 0, "台を消さず警告を記録");
        Check(ReadZip(result.ZipPath, "01_台別詳細.csv").Contains("出率"), "未取得項目");
    });

    await Test("総差枚照合の不一致を拒否", async () =>
    {
        var (service, _) = ExtractionFixture("50", "1000", "105%", "105%", "999");
        await Throws<InvalidOperationException>(() => service.ExtractAsync(Request(), null, default));
    });

    foreach (var seconds in new[] { 5, 60 })
    {
        await Test($"HTTP再試行・robots確認でも{seconds}秒間隔", async () =>
        {
            var clock = new TestClock();
            var attempts = 0;
            var handler = new ScriptedHandler(clock, uri => uri.AbsolutePath == "/robots.txt"
                ? Html("User-agent: *\nAllow: /")
                : ++attempts < 3 ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : Html("OK"));
            var client = Client(handler, clock);
            Check(await client.GetTextAsync(new("https://min-repo.com/1/"), TimeSpan.FromSeconds(seconds), default) == "OK", "再試行成功");
            Check(handler.Requests.Count == 4, "robots+3回");
            CheckIntervals(handler, seconds);
        });
    }

    foreach (var useDate in new[] { false, true })
    {
        await Test($"Retry-After { (useDate ? "日付" : "秒数") }の120秒を尊重", async () =>
        {
            var clock = new TestClock();
            var attempts = 0;
            var handler = new ScriptedHandler(clock, uri =>
            {
                if (uri.AbsolutePath == "/robots.txt") return Html("User-agent: *\nAllow: /");
                if (++attempts > 1) return Html("OK");
                var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                response.Headers.RetryAfter = useDate
                    ? new RetryConditionHeaderValue(clock.Now.AddSeconds(120))
                    : new RetryConditionHeaderValue(TimeSpan.FromSeconds(120));
                return response;
            });
            await Client(handler, clock).GetTextAsync(new("https://min-repo.com/1/"), TimeSpan.FromSeconds(5), default);
            Check((handler.Requests[2].At - handler.Requests[1].At).TotalSeconds >= 120, "待機を短縮しない");
        });
    }

    await Test("再試行待機中の中止で追加通信しない", async () =>
    {
        using var cancellation = new CancellationTokenSource();
        var clock = new TestClock();
        var handler = new ScriptedHandler(clock, uri =>
        {
            if (uri.AbsolutePath == "/robots.txt") return Html("User-agent: *\nAllow: /");
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(120));
            return response;
        });
        var client = new RespectfulMinRepoClient(handler, () => clock.Now, (delay, token) =>
        {
            if (delay.TotalSeconds >= 120) cancellation.Cancel();
            return clock.Wait(delay, token);
        });
        await Throws<OperationCanceledException>(() => client.GetTextAsync(new("https://min-repo.com/1/"), TimeSpan.FromSeconds(5), cancellation.Token));
        Check(handler.Requests.Count == 2, "再試行なし");
    });

    await Test("許可外ホストへのリダイレクトを通信前に拒否", async () =>
    {
        var clock = new TestClock();
        var handler = new ScriptedHandler(clock, uri => uri.AbsolutePath == "/robots.txt"
            ? Html("User-agent: *\nAllow: /") : Redirect("https://example.com/"));
        await Throws<HttpRequestException>(() => Client(handler, clock).GetTextAsync(new("https://min-repo.com/1/"), TimeSpan.FromSeconds(5), default));
        Check(handler.Requests.All(r => MinRepoUrl.IsAllowedHost(r.Uri)), "外部へ送信なし");
    });

    await Test("同一サイトのリダイレクトにも取得間隔を適用", async () =>
    {
        var clock = new TestClock();
        var handler = new ScriptedHandler(clock, uri => uri.AbsolutePath switch
        {
            "/robots.txt" => Html("User-agent: *\nAllow: /"),
            "/1/" => Redirect("/2/"),
            _ => Html("OK"),
        });
        Check(await Client(handler, clock).GetTextAsync(new("https://min-repo.com/1/"), TimeSpan.FromSeconds(5), default) == "OK", "許可リダイレクト");
        CheckIntervals(handler, 5);
    });

    await Test("リダイレクトループを上限で停止", async () =>
    {
        var clock = new TestClock();
        var handler = new ScriptedHandler(clock, uri => uri.AbsolutePath == "/robots.txt"
            ? Html("User-agent: *\nAllow: /") : Redirect("/1/"));
        await Throws<HttpRequestException>(() => Client(handler, clock).GetTextAsync(new("https://min-repo.com/1/"), TimeSpan.FromSeconds(5), default));
        Check(handler.Requests.Count == 7, "robotsと最大6応答");
    });

    await Test("robotsのクエリ・ワイルドカード・終端を尊重", async () =>
    {
        var clock = new TestClock();
        var handler = new ScriptedHandler(clock, uri => uri.AbsolutePath == "/robots.txt"
            ? Html("User-agent: *\nDisallow: /*?kishu=blocked$\nAllow: /") : Html("OK"));
        var client = Client(handler, clock);
        await Throws<InvalidOperationException>(() => client.GetTextAsync(new("https://min-repo.com/1/?kishu=blocked"), TimeSpan.FromSeconds(5), default));
        Check(handler.Requests.Count == 1, "禁止ページへ通信なし");
        await client.GetTextAsync(new("https://min-repo.com/1/?kishu=blocked-extra"), TimeSpan.FromSeconds(5), default);
    });

    await Test("公開Cookie更新時のみ1回再取得", async () =>
    {
        var clock = new TestClock();
        var html = "<script>$.cookie('_d2','abc=='); $.cookie('unrelated','ignored');</script>";
        var handler = new ScriptedHandler(clock, uri => Html(uri.AbsolutePath == "/robots.txt" ? "User-agent: *\nAllow: /" : html));
        var client = Client(handler, clock);
        await client.GetTextAsync(new("https://min-repo.com/1/"), TimeSpan.FromSeconds(5), default);
        await client.GetTextAsync(new("https://min-repo.com/1/"), TimeSpan.FromSeconds(5), default);
        Check(handler.Requests.Count == 4, "robots+初回+再取得+次回");
        Check(RespectfulMinRepoClient.ExtractPublicPageCookies(html).Count == 1, "対象Cookieのみ抽出");
        CheckIntervals(handler, 5);
    });

    foreach (var status in new[] { BackgroundExtractionStatus.Completed, BackgroundExtractionStatus.Failed, BackgroundExtractionStatus.Cancelled })
    {
        await Test($"{status}後の遅延進捗を破棄し次回は更新可能", () =>
        {
            var coordinator = new BackgroundExtractionCoordinator();
            coordinator.Begin();
            if (status == BackgroundExtractionStatus.Completed) coordinator.Complete(new("unused.zip", 1, 1, 0));
            else if (status == BackgroundExtractionStatus.Failed) coordinator.Fail("failure");
            else coordinator.Cancel();
            var ended = coordinator.Current;
            coordinator.Report(new("遅延進捗", 0.5));
            Check(ReferenceEquals(ended, coordinator.Current), "終了状態を保持");
            coordinator.Begin();
            coordinator.Report(new("新規取得", 0.1));
            Check(coordinator.Current.Status == BackgroundExtractionStatus.Running && coordinator.Current.Message == "新規取得", "次回の進捗を受付");
            return Task.CompletedTask;
        });
    }

    await Test("取得0件は失敗として診断ZIPを保持・復元", () =>
    {
        Directory.CreateDirectory(FileSystem.CacheDirectory);
        var diagnostic = Path.Combine(FileSystem.CacheDirectory, "diagnostic.zip");
        File.WriteAllText(diagnostic, "fixture");
        var coordinator = new BackgroundExtractionCoordinator();
        coordinator.Begin();
        coordinator.Complete(new(diagnostic, 0, 0, 3));
        Check(coordinator.Current.Status == BackgroundExtractionStatus.Failed && coordinator.Current.ZipPath == diagnostic, "失敗でもZIPを共有可能");
        var restored = new BackgroundExtractionCoordinator().Current;
        Check(restored.Status == BackgroundExtractionStatus.Failed && restored.FailureCount == 3, "再起動後も失敗状態");
        return Task.CompletedTask;
    });

    await Test("HTTP 200の確認用JSを実レポートとして採用しない", () =>
    {
        const string challenge = "<script>fetch('/wp-admin/admin-ajax.php',{method:'POST'}).then(()=>location.reload())</script>";
        foreach (var url in new[] { "https://min-repo.com/1/", "https://min-repo.com/1/?kishu=all", "https://min-repo.com/1/?kishu=test", "https://min-repo.com/tag/test/" })
            Check(MinRepoBrowserReadiness.GetFingerprint(challenge, new(url)) is null, "JSだけでは未完成");
        return Task.CompletedTask;
    });

    await Test("現行トップの371台・総差枚34650を識別", () =>
    {
        var fingerprint = MinRepoBrowserReadiness.GetFingerprint(
            "<h1>10/8(木) 楽園池袋店グリーンサイド</h1><div>2026年10月9日</div><table><tr><td>勝率</td><td>149/371</td></tr><tr><td>総差枚</td><td>+34,650</td></tr></table>", new("https://min-repo.com/3398462/"));
        Check(fingerprint == "2026-10-08|371|34650", "対象日・台数・差枚");
        return Task.CompletedTask;
    });

    await Test("見出しだけの描画途中は全台表と判定しない", () =>
    {
        Check(MinRepoBrowserReadiness.GetFingerprint(
            "<h1>10/8(木) テスト店</h1><div>2026年10月9日</div><h2>全台 データ一覧</h2>", new("https://min-repo.com/1/?kishu=all")) is null, "必須表を待機");
        return Task.CompletedTask;
    });

    await Test("別レポート・別クエリ・外部ページのHTMLを拒否", () =>
    {
        var requested = new Uri("https://min-repo.com/1/?kishu=all");
        Check(MinRepoBrowserReadiness.IsRequestedDocument(requested, new("https://www.min-repo.com/1/?kishu=all#data")), "wwwとフラグメントを許容");
        foreach (var url in new[] { "https://min-repo.com/2/?kishu=all", "https://min-repo.com/1/?kishu=test", "https://example.com/1/?kishu=all" })
            Check(!MinRepoBrowserReadiness.IsRequestedDocument(requested, new(url)), "混入を拒否");
        return Task.CompletedTask;
    });

    await Test("ブラウザー遷移もrobots確認と5秒間隔を共有", async () =>
    {
        var clock = new TestClock();
        var handler = new ScriptedHandler(clock, _ => Html("User-agent: *\nAllow: /"));
        var client = Client(handler, clock);
        var uri = new Uri("https://min-repo.com/1/");
        await client.AuthorizeBrowserNavigationAsync(uri, TimeSpan.FromSeconds(5), default);
        var first = clock.Now;
        await client.AuthorizeBrowserNavigationAsync(uri, TimeSpan.FromSeconds(5), default);
        Check(handler.Requests.Count == 1, "HTMLはWebViewのみが取得");
        Check((clock.Now - first).TotalSeconds >= 5, "ブラウザー再読込の取得間隔");
    });

    await Test("robotsの代わりに確認用HTMLが返ったら停止", async () =>
    {
        var clock = new TestClock();
        var handler = new ScriptedHandler(clock, _ => Html("<script>location.reload()</script>"));
        await Throws<PageAcquisitionException>(() => Client(handler, clock).AuthorizeBrowserNavigationAsync(new("https://min-repo.com/1/"), TimeSpan.FromSeconds(5), default));
        Check(handler.Requests.Count == 1, "不明なポリシーで本文取得しない");
    });

    await Test("ブラウザー確認の継続時は部分モードでも全日分の再試行を停止", async () =>
    {
        var source = new ScriptedPageSource(uri => uri.AbsolutePath.StartsWith("/tag/")
            ? "<h2>店舗 データ一覧</h2><table><tr><td><a href='/1/'>10/8</a></td><td><a href='/2/'>10/7</a></td></tr></table>"
            : throw new PageAcquisitionException("確認画面が継続"));
        var request = Request() with { SourceUrl = "https://min-repo.com/tag/test/", IsStoreMode = true, CreatePartialOutput = true, MaxReports = 2 };
        var result = await new MinRepoExtractionService(source).ExtractAsync(request, null, default);
        Check(result.RowCount == 0 && result.FailureCount == 1, "単一の診断を保存");
        Check(source.Requests.Count == 2 && source.Requests[1].AbsolutePath == "/1/", "残りの全台・次の日を取得しない");
    });

    Console.WriteLine($"PASS: {passed} regression checks");
}
finally
{
    // テストが生成した専用ディレクトリだけを後片付けします。
    if (Directory.Exists(FileSystem.CacheDirectory)) Directory.Delete(FileSystem.CacheDirectory, true);
}

async Task Test(string name, Func<Task> test)
{
    await test();
    passed++;
    Console.WriteLine($"PASS {passed}: {name}");
}

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException("FAIL: " + message);
}

static async Task Throws<T>(Func<Task> action) where T : Exception
{
    try { await action(); }
    catch (T) { return; }
    throw new InvalidOperationException($"Expected {typeof(T).Name}");
}

static ExtractionRequest Request() => new("https://min-repo.com/1/", false, null, null,
    1, 1, TimeSpan.FromSeconds(5), AggregationPeriods.Day, AggregationKeys.Machine, false, false);

static string ReadZip(string path, string entry)
{
    using var archive = ZipFile.OpenRead(path);
    using var reader = new StreamReader(archive.GetEntry(entry)!.Open());
    return reader.ReadToEnd();
}

static HttpResponseMessage Html(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text) };

static HttpResponseMessage Redirect(string target)
{
    var response = new HttpResponseMessage(HttpStatusCode.Found);
    response.Headers.Location = new Uri(target, UriKind.RelativeOrAbsolute);
    return response;
}

static RespectfulMinRepoClient Client(ScriptedHandler handler, TestClock clock)
    => new(handler, () => clock.Now, clock.Wait);

static void CheckIntervals(ScriptedHandler handler, int seconds)
{
    for (var i = 1; i < handler.Requests.Count; i++)
        Check((handler.Requests[i].At - handler.Requests[i - 1].At).TotalSeconds >= seconds, "全通信に最小間隔を適用");
}

static (MinRepoExtractionService, ScriptedHandler) ExtractionFixture(
    string difference, string games, string payout, string detailPayout,
    string total = "50", bool detailFails = false)
{
    var identity = $"<h1>7/27(月) テスト店舗</h1><div>2026年7月28日</div><div>勝率 1/1</div><div>総差枚 {total}</div>";
    var all = identity + $"""
        <h2>全台 データ一覧</h2><table>
        <tr><th>機種</th><th>台番</th><th>差枚</th><th>G数</th><th>出率</th></tr>
        <tr><td><a href="/1/?kishu=test">機種A</a></td><td>101</td><td>{difference}</td><td>{games}</td><td>{payout}</td></tr>
        </table><a href="/1/">レポートトップに戻る</a>
        """;
    var detail = identity + $"""
        <h2>機種A データ一覧</h2><table>
        <tr><th>台番</th><th>差枚</th><th>G数</th><th>出率</th><th>BB</th><th>RB</th></tr>
        <tr><td>101</td><td>{total}</td><td>1000</td><td>{detailPayout}</td><td>2</td><td>1</td></tr>
        </table><a href="/1/">レポートトップに戻る</a>
        """;
    var clock = new TestClock();
    var handler = new ScriptedHandler(clock, uri => uri.AbsolutePath == "/robots.txt"
        ? Html("User-agent: *\nAllow: /")
        : uri.Query == "?kishu=all" ? Html(all)
        : uri.Query == "?kishu=test" ? detailFails ? new(HttpStatusCode.NotFound) : Html(detail)
        : Html(identity));
    return (new MinRepoExtractionService(Client(handler, clock)), handler);
}

// 仮想時計は待機時間だけ進めます。テストでは実サイトにもタイマーにも依存しません。
sealed class TestClock
{
    public DateTimeOffset Now { get; private set; } = new(2026, 7, 27, 0, 0, 0, TimeSpan.Zero);
    public Task Wait(TimeSpan delay, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Now += delay;
        return Task.CompletedTask;
    }
}

sealed class ScriptedHandler(TestClock clock, Func<Uri, HttpResponseMessage> response) : HttpMessageHandler
{
    public List<(Uri Uri, DateTimeOffset At)> Requests { get; } = [];
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Requests.Add((request.RequestUri!, clock.Now));
        return Task.FromResult(response(request.RequestUri!));
    }
}

// ブラウザー取得口の失敗を、Androidや実通信に依存せずサービス全体へ流します。
sealed class ScriptedPageSource(Func<Uri, string> response) : IMinRepoPageSource
{
    public List<Uri> Requests { get; } = [];
    public Task<string> GetTextAsync(Uri uri, TimeSpan delay, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Requests.Add(uri);
        return Task.FromResult(response(uri));
    }
}
