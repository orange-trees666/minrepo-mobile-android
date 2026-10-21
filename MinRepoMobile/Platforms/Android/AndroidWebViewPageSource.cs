using System.Net;
using System.Text;
using System.Text.Json;
using Android.Webkit;
using MinRepoMobile.Services;
using Microsoft.Maui.ApplicationModel;
using MauiWebView = Microsoft.Maui.Controls.WebView;
using NativeWebView = Android.Webkit.WebView;

namespace MinRepoMobile.Platforms.Android;

/// <summary>
/// 画面内の実ブラウザーでJavaScript・Cookie・再読込を実行し、完成したHTMLを返します。
/// Cookie値やサイト内の確認用トークンを解析・複製せず、WebView自身に管理させます。
/// 全取得を同じブラウザーへ直列化し、確認画面もその場でユーザー操作できます。
/// </summary>
public sealed class AndroidWebViewPageSource : IMinRepoPageSource
{
    private RespectfulMinRepoClient _policy = new();
    private readonly SemaphoreSlim _serial = new(1, 1);
    private NativeWebView? _browser;
    private volatile Operation? _active;

    public event Action<string>? StatusChanged;

    /// <summary>MAUIの画面に配置されたWebViewへ接続します。ブラウザー既定UAを保ちます。</summary>
    public void Attach(MauiWebView view)
    {
        view.HandlerChanged += (_, _) => Configure(view);
        Configure(view);
    }

    private void Configure(MauiWebView view)
    {
        if (view.Handler?.PlatformView is not NativeWebView native || ReferenceEquals(native, _browser)) return;
        _browser = native;
        native.Settings.JavaScriptEnabled = true;
        native.Settings.DomStorageEnabled = true;
        native.Settings.CacheMode = CacheModes.NoCache;
        native.Settings.AllowFileAccess = false;
        native.Settings.AllowContentAccess = false;
        native.Settings.MixedContentMode = MixedContentHandling.NeverAllow;
        CookieManager.Instance!.SetAcceptCookie(true);
        // CookieはURLとブラウザーセッションに従い保存されます。JSブリッジは追加しません。
        native.SetWebViewClient(new PageClient(this));
    }

    public async Task<string> GetTextAsync(Uri uri, TimeSpan delay, CancellationToken cancellationToken)
    {
        if (!MinRepoUrl.IsAllowedHost(uri)) throw new ArgumentException("許可されていない取得先です。", nameof(uri));
        await _serial.WaitAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        var operation = new Operation(uri, delay, timeout.Token);
        try
        {
            await _policy.AuthorizeBrowserNavigationAsync(uri, delay, timeout.Token);
            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                if (_browser is null) throw new PageAcquisitionException("取得画面を開いてから再度実行してください。");
                _active = operation;
                _browser.ResumeTimers();
                _browser.OnResume();
                StatusChanged?.Invoke("サイトを読み込み中です。確認画面が出た場合だけ操作してください。");
                _browser.LoadUrl(uri.AbsoluteUri);
            });

            string? previous = null;
            while (true)
            {
                timeout.Token.ThrowIfCancellationRequested();
                var snapshot = await MainThread.InvokeOnMainThreadAsync(async () =>
                {
                    if (operation.Error is not null) throw operation.Error;
                    if (!operation.Loaded || _browser is null) return null;
                    return await SnapshotAsync(_browser, timeout.Token);
                });
                if (snapshot is not null && snapshot.Ready != "loading" &&
                    Uri.TryCreate(snapshot.Url, UriKind.Absolute, out var actual) &&
                    MinRepoBrowserReadiness.IsRequestedDocument(uri, actual))
                {
                    var fingerprint = MinRepoBrowserReadiness.GetFingerprint(snapshot.Html, uri);
#if DEBUG
                    var diagnosticShape = $"{operation.Navigations}:{snapshot.Html.Length / 1000}";
                    if (DiagnosticsEnabled && fingerprint is null && operation.DiagnosticShape != diagnosticShape)
                    {
                        operation.DiagnosticShape = diagnosticShape;
                        var cookieNames = await MainThread.InvokeOnMainThreadAsync(() =>
                            string.Join(",", (CookieManager.Instance!.GetCookie(uri.AbsoluteUri) ?? "")
                                .Split(';', StringSplitOptions.RemoveEmptyEntries).Select(c => c.Split('=')[0].Trim())));
                        global::Android.Util.Log.Info("MinRepoBrowserTest",
                            $"Incomplete DOM: url={snapshot.Url} length={snapshot.Html.Length} reloads={operation.Navigations} cookies={cookieNames}");
                    }
#endif
                    // 読込完了だけでは確認用HTMLも成功するため、実表が2回連続で安定した後に採用します。
                    if (fingerprint is not null && fingerprint == previous)
                    {
                        StatusChanged?.Invoke("データを確認しました。自動で取得を続けます。");
                        return snapshot.Html;
                    }
                    previous = fingerprint;
                }
                else previous = null;
                await Task.Delay(750, timeout.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new PageAcquisitionException(
                "サイトの確認画面が続くか、必要なデータ表が表示されず、2分で停止しました。" +
                "確認画面を操作しても進まない場合は、通信回線を確認して再実行してください。");
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not PageAcquisitionException &&
            !(ex is HttpRequestException http && http.StatusCode == HttpStatusCode.NotFound))
        {
            // 通信・robots確認・JS評価の障害を、別日の欠損として大量に繰り返しません。
            // 実在しないレポートの404だけは通常のページ欠損として呼出し側へ返します。
            throw new PageAcquisitionException($"ブラウザーでページを取得できませんでした: {ex.Message}", ex);
        }
        finally
        {
#if DEBUG
            if (DiagnosticsEnabled)
            {
                try
                {
                    using var capture = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    var last = await MainThread.InvokeOnMainThreadAsync(() =>
                        _browser is null ? Task.FromResult<Snapshot?>(null) : SnapshotAsync(_browser, capture.Token));
                    if (last is not null)
                    {
                        await File.WriteAllTextAsync(Path.Combine(Microsoft.Maui.Storage.FileSystem.AppDataDirectory,
                            "browser-final.html"), last.Html);
                        global::Android.Util.Log.Info("MinRepoBrowserTest",
                            $"Final DOM: url={last.Url} ready={last.Ready} length={last.Html.Length}");
                    }
                }
                catch (Exception ex) { global::Android.Util.Log.Info("MinRepoBrowserTest", $"DOM capture: {ex.Message}"); }
            }
#endif
            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                if (ReferenceEquals(_active, operation))
                {
                    _active = null;
                    _browser?.StopLoading();
                }
            });
            _serial.Release();
        }
    }

    /// <summary>現在ページのURLと描画後HTMLをまとめて取得し、別ページの混入を防ぎます。</summary>
    private static async Task<Snapshot?> SnapshotAsync(NativeWebView browser, CancellationToken token)
    {
        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        browser.EvaluateJavascript(
            "JSON.stringify({Url:location.href,Html:document.documentElement.outerHTML,Ready:document.readyState})",
            new ScriptResult(completion));
        var value = await completion.Task.WaitAsync(token);
        if (string.IsNullOrWhiteSpace(value) || value == "null") return null;
        // AndroidはJSの戻り値をJSONで包みます。文字列の内部JSONをもう一度復元します。
        var json = JsonSerializer.Deserialize<string>(value);
        return json is null ? null : JsonSerializer.Deserialize<Snapshot>(json);
    }

    private sealed class Operation(Uri uri, TimeSpan delay, CancellationToken token)
    {
        public Uri Uri { get; } = uri;
        public TimeSpan Delay { get; } = delay;
        public CancellationToken Token { get; } = token;
        public volatile bool Loaded;
        public int Requests;
        public int Navigations => Math.Max(0, Volatile.Read(ref Requests) - 1);
#if DEBUG
        public string? DiagnosticShape { get; set; }
#endif
        public volatile Exception? Error;
    }

    private sealed record Snapshot(string Url, string Html, string Ready);

    private sealed class ScriptResult(TaskCompletionSource<string?> completion) : Java.Lang.Object, IValueCallback
    {
        public void OnReceiveValue(Java.Lang.Object? value)
        {
            completion.TrySetResult(value?.ToString());
            Dispose();
        }
    }

    private sealed class PageClient(AndroidWebViewPageSource owner) : WebViewClient
    {
        public override bool ShouldOverrideUrlLoading(NativeWebView? view, IWebResourceRequest? request)
        {
            var operation = owner._active;
            if (operation is null || request?.Url is null) return true;
            if (!Uri.TryCreate(request.Url.ToString(), UriKind.Absolute, out var uri) ||
                !MinRepoBrowserReadiness.IsRequestedDocument(operation.Uri, uri)) return true;
            // 遷移をキャンセルして作り直すと、同じ文書でのfetch完了・Cookie反映・
            // Refererの連続性が失われます。許可した文書はWebView自身に遷移させます。
            // 通信間隔はUIスレッドではなくShouldInterceptRequestで制御します。
            return false;
        }

        public override void OnPageStarted(NativeWebView? view, string? url, global::Android.Graphics.Bitmap? favicon)
        {
            if (owner._active is { } operation) operation.Loaded = false;
        }

        public override void OnPageFinished(NativeWebView? view, string? url)
        {
            if (owner._active is { } operation && Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
                MinRepoBrowserReadiness.IsRequestedDocument(operation.Uri, uri)) operation.Loaded = true;
        }

        public override void OnPageCommitVisible(NativeWebView? view, string? url)
        {
            // 本文表示後はDOMの実データを検査し、広告等のサブリソース完了を待ちません。
            if (owner._active is { } operation && Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
                MinRepoBrowserReadiness.IsRequestedDocument(operation.Uri, uri)) operation.Loaded = true;
        }

        public override void OnReceivedError(NativeWebView? view, IWebResourceRequest? request, WebResourceError? error)
        {
            if (request?.IsForMainFrame == true && owner._active is { } operation)
                operation.Error = new PageAcquisitionException($"ページを読み込めませんでした: {error?.Description}");
        }

        public override void OnReceivedHttpError(NativeWebView? view, IWebResourceRequest? request, WebResourceResponse? response)
        {
            if (request?.IsForMainFrame == true && owner._active is { } operation)
                operation.Error = response?.StatusCode == 404
                    ? new HttpRequestException("ページが見つかりません: HTTP 404", null, HttpStatusCode.NotFound)
                    : new PageAcquisitionException($"サイトがページ取得を受け付けませんでした: HTTP {response?.StatusCode}。時間を置いて再実行してください。");
        }

        public override bool OnRenderProcessGone(NativeWebView? view, RenderProcessGoneDetail? detail)
        {
            if (owner._active is { } operation)
                operation.Error = new PageAcquisitionException("ブラウザーの描画処理が停止しました。アプリを開き直して再実行してください。");
            owner._browser = null;
            return true;
        }

        /// <summary>
        /// ブラウザーが生成したメイン文書リクエストを、送信前のIOスレッドで調整します。
        /// JS遷移の取り消しやHTMLの再注入を行わず、Cookieと参照元をブラウザーに保持させます。
        /// </summary>
        public override WebResourceResponse? ShouldInterceptRequest(NativeWebView? view, IWebResourceRequest? request)
        {
            if (request?.Url is null || !Uri.TryCreate(request.Url.ToString(), UriKind.Absolute, out var uri))
                return null;
            if (request.IsForMainFrame)
            {
                var operation = owner._active;
                if (operation is null) return EmptyResponse();
                try
                {
                    if (!MinRepoBrowserReadiness.IsRequestedDocument(operation.Uri, uri))
                        throw new PageAcquisitionException("取得対象と異なるページへの移動を停止しました。");
                    var requests = Interlocked.Increment(ref operation.Requests);
                    if (requests > 4)
                        throw new PageAcquisitionException("サイトの確認画面が繰り返されたため取得を停止しました。");
                    // 最初の要求はGetTextAsyncで認可済みです。JS再読込分だけをここで待機します。
                    // このコールバックはAndroidのIOスレッドで呼ばれ、画面操作は妨げません。
                    if (requests > 1)
                        owner._policy.AuthorizeBrowserNavigationAsync(uri, operation.Delay, operation.Token)
                            .GetAwaiter().GetResult();
                    operation.Token.ThrowIfCancellationRequested();
#if DEBUG
                    if (owner.DiagnosticsEnabled)
                        global::Android.Util.Log.Info("MinRepoBrowserTest", $"Document request: {uri} count={requests}");
#endif
                }
                catch (OperationCanceledException) { return EmptyResponse(); }
                catch (Exception ex)
                {
                    operation.Error = ex is PageAcquisitionException ? ex :
                        new PageAcquisitionException($"ページ取得の確認に失敗しました: {ex.Message}", ex);
                    return EmptyResponse();
                }
            }
#if DEBUG
            // 応答だけを差し替え、実WebViewのJS・fetch・Cookie・遷移はそのまま実行します。
            if (owner._testResponses is not null)
            {
                var html = owner._testResponses(uri);
                var mime = uri.AbsolutePath == "/wp-admin/admin-ajax.php" ? "application/json" : "text/html";
                return new WebResourceResponse(mime, "UTF-8", new MemoryStream(Encoding.UTF8.GetBytes(html)));
            }
#endif
            return null;
        }

        private static WebResourceResponse EmptyResponse()
            => new("text/html", "UTF-8", new MemoryStream());

    }

#if DEBUG
    internal bool DiagnosticsEnabled { get; set; }
    private WebChromeClient? _originalChromeClient;
    internal void StartDiagnostics()
    {
        if (_browser is null) return;
        _originalChromeClient = _browser.WebChromeClient;
        _browser.SetWebChromeClient(new DiagnosticChromeClient());
        var package = NativeWebView.CurrentWebViewPackage;
        global::Android.Util.Log.Info("MinRepoBrowserTest", $"WebView version={package?.VersionName}");
    }
    internal void StopDiagnostics()
    {
        if (_browser is not null) _browser.SetWebChromeClient(_originalChromeClient);
    }
    private sealed class DiagnosticChromeClient : WebChromeClient
    {
        public override bool OnConsoleMessage(ConsoleMessage? message)
        {
            global::Android.Util.Log.Info("MinRepoBrowserTest", $"JS: {message?.Message()}");
            return true;
        }
    }
    private Func<Uri, string>? _testResponses;
    internal void SetTestResponses(Func<Uri, string>? responses)
    {
        _testResponses = responses;
        _policy = responses is null ? new() : new RespectfulMinRepoClient(new RobotsFixture());
    }
    private sealed class RobotsFixture : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("User-agent: *\nAllow: /\n") });
    }
#endif
}
