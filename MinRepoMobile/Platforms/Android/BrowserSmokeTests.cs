#if DEBUG
using System.IO.Compression;
using System.Text.Json;
using Android.Webkit;
using MinRepoMobile.Models;
using MinRepoMobile.Services;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Storage;

namespace MinRepoMobile.Platforms.Android;

/// <summary>
/// CIのAndroidエミュレーター上で、JS→Cookie→再読込→HTML解析→ZIPを通して試験します。
/// 明示的なDebug起動フラグがある場合だけ実行し、通常の操作・Releaseには含めません。
/// </summary>
internal static class BrowserSmokeTests
{
    public static async Task RunAsync(MainPage page, MinRepoExtractionService extractor,
        AndroidWebViewPageSource browserPages)
    {
        var results = new List<object>();
        var success = false;
        string? error = null;
        page.ShowBrowserForSmokeTest(true);
        browserPages.DiagnosticsEnabled = true;
        try
        {
            // 最初は表を持たないJSだけを返します。実WebViewがCookieを設定し、再読込した後に表を返します。
            browserPages.SetTestResponses(uri =>
            {
                if (CookieManager.Instance!.GetCookie(uri.AbsoluteUri)?.Contains("browser_fixture=1") != true)
                    return """
                        <html><body>確認中<script>
                        setTimeout(function(){document.cookie='browser_fixture=1; path=/';
                        window.location.href=window.location.href;},300);
                        </script></body></html>
                        """;
                return Fixture(uri.Query);
            });
            var fixture = await Task.Run(() => extractor.ExtractAsync(Request("https://min-repo.com/1/", true), null, default));
            if (fixture.RowCount != 1 || fixture.FailureCount != 0)
                throw new InvalidOperationException("ブラウザーによる模擬レポート取得に失敗しました。");
            using (var zip = ZipFile.OpenRead(fixture.ZipPath))
            {
                using var reader = new StreamReader(zip.GetEntry("01_台別詳細.csv")!.Open());
                var csv = reader.ReadToEnd();
                if (!csv.Contains(",-300,1000,1000,90.00,2,1,"))
                    throw new InvalidOperationException("JS再読込後の差枚・BB/RBがZIPに反映されませんでした。");
            }
            results.Add(new { test = "android-js-cookie-reload-csv", rows = fixture.RowCount, failures = fixture.FailureCount });

            // 公開レポート1日分だけを実取得します。成功判定には行数だけでなく完全取得モードの照合を使います。
            browserPages.SetTestResponses(null);
            // 模擬サイトのCookieを残した状態では、新規インストール時の取得を再現できません。
            var removed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await MainThread.InvokeOnMainThreadAsync(() =>
                CookieManager.Instance!.RemoveAllCookies(new CookieRemoved(removed)));
            await removed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var live = await Task.Run(() => extractor.ExtractAsync(Request("https://min-repo.com/3398462/", false), null, default));
            if (live.RowCount != 371 || live.ReportCount != 1 || live.FailureCount != 0)
                throw new InvalidOperationException($"実サイトの全台照合に失敗しました: {live.RowCount}台 / {live.FailureCount}件");
            results.Add(new { test = "live-report-3398462", rows = live.RowCount, failures = live.FailureCount, zipPath = live.ZipPath });
            success = true;
        }
        catch (Exception ex) { error = ex.ToString(); }
        finally
        {
            browserPages.SetTestResponses(null);
            browserPages.DiagnosticsEnabled = false;
            page.ShowBrowserForSmokeTest(false);
            var json = JsonSerializer.Serialize(new { success, results, error });
            await File.WriteAllTextAsync(Path.Combine(FileSystem.AppDataDirectory, "browser-smoke.json"), json);
            global::Android.Util.Log.Info("MinRepoBrowserTest", json);
        }
    }

    private static ExtractionRequest Request(string url, bool bonus)
        => new(url, false, null, null, 1, 1, TimeSpan.FromSeconds(5),
            AggregationPeriods.Day, AggregationKeys.Machine, bonus, false);

    private sealed class CookieRemoved(TaskCompletionSource completion) : Java.Lang.Object, IValueCallback
    {
        public void OnReceiveValue(Java.Lang.Object? value)
        {
            completion.TrySetResult();
            Dispose();
        }
    }

    private static string Fixture(string query)
    {
        const string identity = "<h1>10/8(木) ブラウザー試験店</h1><div>2026年10月9日</div><div>勝率 0/1</div><div>総差枚 -300</div>";
        return query switch
        {
            "?kishu=all" => identity + """
                <h2>全台 データ一覧</h2><table>
                <tr><th>機種</th><th>台番</th><th>差枚</th><th>G数</th><th>出率</th></tr>
                <tr><td><a href='/1/?kishu=test'>機種A</a></td><td>101</td><td>-300</td><td>1000</td><td>90%</td></tr>
                </table><a href='/1/'>レポートトップに戻る</a>
                """,
            "?kishu=test" => identity + """
                <h2>機種A データ一覧</h2><table>
                <tr><th>台番</th><th>差枚</th><th>G数</th><th>出率</th><th>BB</th><th>RB</th></tr>
                <tr><td>101</td><td>-300</td><td>1000</td><td>90%</td><td>2</td><td>1</td></tr>
                </table><a href='/1/'>レポートトップに戻る</a>
                """,
            _ => identity,
        };
    }
}
#endif
