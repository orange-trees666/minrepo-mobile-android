namespace MinRepoMobile.Services;

/// <summary>
/// HTTPとブラウザーの違いを解析・CSV出力から切り離す、公開HTMLの取得口です。
/// Androidでは同じWebViewセッションを使い、テストでは通信を差し替えます。
/// </summary>
public interface IMinRepoPageSource
{
    Task<string> GetTextAsync(Uri uri, TimeSpan delay, CancellationToken cancellationToken);
}

/// <summary>
/// ブラウザー確認の継続や描画プロセスの停止など、次の日へ進めても改善しない障害。
/// 部分出力モードでも以後のアクセスを止め、取得済みデータだけを保存します。
/// </summary>
public sealed class PageAcquisitionException(string message, Exception? inner = null)
    : IOException(message, inner);
