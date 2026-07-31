namespace MinRepoMobile.Services;

/// <summary>
/// 取得先をmin-repo.comに限定するURL検証クラス。
/// ユーザー入力をそのままHttpClientへ渡さず、想定外ホストへのアクセスを防ぎます。
/// </summary>
public static class MinRepoUrl
{
    private static readonly HashSet<string> AllowedHosts =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "min-repo.com",
            "www.min-repo.com",
        };

    public static bool IsAllowedHost(Uri uri)
        => uri.Scheme == Uri.UriSchemeHttps &&
           AllowedHosts.Contains(uri.Host) &&
           uri.IsDefaultPort &&
           string.IsNullOrEmpty(uri.UserInfo);

    public static Uri Normalize(
        string value,
        bool requireStorePath = false,
        bool allowAllQuery = true)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var input) ||
            !IsAllowedHost(input))
        {
            throw new ArgumentException(
                "URLは https://min-repo.com/ 配下を指定してください。");
        }

        if (requireStorePath &&
            !input.AbsolutePath.StartsWith("/tag/", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "店舗URLには /tag/ を含むレポート一覧URLを指定してください。");
        }

        var query = input.Query.TrimStart('?');
        var hasAllQuery = query.Equals("kishu=all", StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(query) && (!allowAllQuery || !hasAllQuery))
        {
            throw new ArgumentException(
                "URLのクエリは kishu=all 以外指定できません。");
        }

        var path = input.AbsolutePath.EndsWith('/')
            ? input.AbsolutePath
            : $"{input.AbsolutePath}/";
        var builder = new UriBuilder(Uri.UriSchemeHttps, "min-repo.com")
        {
            Path = path,
            Query = hasAllQuery ? "kishu=all" : string.Empty,
            Fragment = string.Empty,
        };
        return builder.Uri;
    }

    public static Uri ToAllMachines(Uri reportUri)
    {
        var canonical = Normalize(reportUri.AbsoluteUri, allowAllQuery: true);
        var builder = new UriBuilder(canonical) { Query = "kishu=all" };
        return builder.Uri;
    }

    /// <summary>
    /// 全台ページや機種別ページのURLから、同じ日別レポートのトップURLを作成します。
    /// トップページの勝率分母と全台取得件数を照合するために使用します。
    /// </summary>
    public static Uri ToReportTop(Uri reportUri)
    {
        var canonical = Normalize(reportUri.AbsoluteUri, allowAllQuery: true);
        var builder = new UriBuilder(canonical)
        {
            Query = string.Empty,
            Fragment = string.Empty,
        };
        return builder.Uri;
    }

    /// <summary>
    /// 全台ページ内に掲載された機種別リンクだけを詳細取得先として許可します。
    /// パスは数値レポートID、クエリはkishu 1件だけに限定します。
    /// </summary>
    public static bool TryNormalizeDetailLink(Uri input, out Uri normalized)
    {
        normalized = null!;
        if (!IsAllowedHost(input))
        {
            return false;
        }

        var reportId = input.AbsolutePath.Trim('/');
        if (string.IsNullOrEmpty(reportId) || reportId.Any(character => !char.IsDigit(character)))
        {
            return false;
        }

        var query = input.Query.TrimStart('?');
        var separator = query.IndexOf('=');
        if (separator <= 0 ||
            query.Contains('&') ||
            !query.Substring(0, separator)
                .Equals("kishu", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var value = query.Substring(separator + 1);
        if (string.IsNullOrWhiteSpace(value) || value.Length > 500)
        {
            return false;
        }

        var builder = new UriBuilder(Uri.UriSchemeHttps, "min-repo.com")
        {
            Path = $"/{reportId}/",
            Query = $"kishu={value}",
            Fragment = string.Empty,
        };
        normalized = builder.Uri;
        return true;
    }
}
