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
    private Operation? _active;

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
        finally
        {
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

    /// <summary>JavaScriptによる再読込・リダイレクトにもページ取得間隔を適用します。</summary>
    private async Task NavigateAsync(Operation operation, Uri uri)
    {
        try
        {
            if (++operation.Navigations > 12)
                throw new PageAcquisitionException("サイトの確認画面が繰り返されたため取得を停止しました。");
            await _policy.AuthorizeBrowserNavigationAsync(uri, operation.Delay, operation.Token);
            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                if (!ReferenceEquals(_active, operation) || operation.Token.IsCancellationRequested) return;
                operation.Loaded = false;
                _browser?.LoadUrl(uri.AbsoluteUri);
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            await MainThread.InvokeOnMainThreadAsync(() => operation.Error = ex);
        }
    }

    private sealed class Operation(Uri uri, TimeSpan delay, CancellationToken token)
    {
        public Uri Uri { get; } = uri;
        public TimeSpan Delay { get; } = delay;
        public CancellationToken Token { get; } = token;
        public bool Loaded { get; set; }
        public int Navigations { get; set; }
        public Exception? Error { get; set; }
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
            // native.LoadUrlによる遷移はこのイベントを経由しないため、再帰しません。
            _ = owner.NavigateAsync(operation, uri);
            return true;
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

#if DEBUG
        // 実AndroidのJS・Cookie・再読込を通信なしで試験する、CI専用の応答差替えです。
        public override WebResourceResponse? ShouldInterceptRequest(NativeWebView? view, IWebResourceRequest? request)
        {
            if (owner._testResponses is null || request?.Url is null ||
                !Uri.TryCreate(request.Url.ToString(), UriKind.Absolute, out var uri)) return null;
            var html = owner._testResponses(uri);
            return new WebResourceResponse("text/html", "UTF-8", new MemoryStream(Encoding.UTF8.GetBytes(html)));
        }
#endif
    }

#if DEBUG
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
