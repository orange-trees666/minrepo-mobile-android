using System.Text.Json;
using MinRepoMobile.Models;

namespace MinRepoMobile.Services;

/// <summary>
/// アプリに同梱したstores.jsonを読み込み、画面で安全に利用できる店舗だけを返します。
/// URLや重複をここで検証することで、画面と取得処理へ不正な設定を渡しません。
/// </summary>
public sealed class StoreCatalogService
{
    private const string CatalogFileName = "stores.json";

    private IReadOnlyList<StoreDefinition>? _cachedStores;

    /// <summary>
    /// 設定ファイルはアプリ内で変更されないため、初回読込結果をメモリへ保持します。
    /// </summary>
    public async Task<IReadOnlyList<StoreDefinition>> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        if (_cachedStores is not null)
        {
            return _cachedStores;
        }

        await using var stream =
            await FileSystem.OpenAppPackageFileAsync(CatalogFileName);

        var catalog = await JsonSerializer.DeserializeAsync<StoreCatalog>(
            stream,
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            },
            cancellationToken);

        if (catalog is null || catalog.Version < 1)
        {
            throw new InvalidDataException(
                $"{CatalogFileName}のversionが正しくありません。");
        }

        var enabledStores = catalog.Stores
            .Where(store => store.Enabled)
            .ToArray();

        Validate(enabledStores);

        _cachedStores = enabledStores
            .OrderBy(store => store.Prefecture, StringComparer.CurrentCulture)
            .ThenBy(store => store.Name, StringComparer.CurrentCulture)
            .ToArray();

        return _cachedStores;
    }

    /// <summary>
    /// 店舗設定の必須値、ID・URL重複、通信先を検証します。
    /// みんレポ以外のURLを設定ファイル経由で取得しないための境界でもあります。
    /// </summary>
    private static void Validate(IReadOnlyCollection<StoreDefinition> stores)
    {
        if (stores.Count == 0)
        {
            throw new InvalidDataException(
                $"{CatalogFileName}に有効な店舗がありません。");
        }

        foreach (var store in stores)
        {
            if (string.IsNullOrWhiteSpace(store.Id) ||
                string.IsNullOrWhiteSpace(store.Prefecture) ||
                string.IsNullOrWhiteSpace(store.Name) ||
                !Uri.TryCreate(store.Url, UriKind.Absolute, out var uri) ||
                uri.Scheme != Uri.UriSchemeHttps ||
                !uri.Host.Equals("min-repo.com", StringComparison.OrdinalIgnoreCase) ||
                !uri.AbsolutePath.StartsWith(
                    "/tag/",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"店舗設定が正しくありません: {store.Name} ({store.Id})");
            }
        }

        var duplicateId = stores
            .GroupBy(store => store.Id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateId is not null)
        {
            throw new InvalidDataException(
                $"店舗IDが重複しています: {duplicateId.Key}");
        }

        var duplicateUrl = stores
            .GroupBy(store => store.Url, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateUrl is not null)
        {
            throw new InvalidDataException(
                $"店舗URLが重複しています: {duplicateUrl.Key}");
        }
    }
}
