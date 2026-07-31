namespace MinRepoMobile.Models;

/// <summary>
/// stores.json全体を表す設定モデルです。
/// 店舗データの出典と更新日も保持し、設定の鮮度をREADME以外からも確認できます。
/// </summary>
public sealed class StoreCatalog
{
    public int Version { get; init; }

    public string SourceUrl { get; init; } = string.Empty;

    public string VerifiedOn { get; init; } = string.Empty;

    public string ScopeNote { get; init; } = string.Empty;

    public List<StoreDefinition> Stores { get; init; } = [];
}

/// <summary>
/// 画面の店舗プルダウンへ表示する1店舗分の設定です。
/// Idは将来店舗名が変わった場合でも同じ店舗を識別するための固定値です。
/// </summary>
public sealed class StoreDefinition
{
    public string Id { get; init; } = string.Empty;

    public string Prefecture { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string Url { get; init; } = string.Empty;

    public bool Enabled { get; init; } = true;

    public override string ToString() => Name;
}
