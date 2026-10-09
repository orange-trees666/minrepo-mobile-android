// テスト専用の保存先・設定。実アプリには含めず、端末のMAUI実装は置き換えません。
namespace Microsoft.Maui.Storage;

public static class FileSystem
{
    public static string CacheDirectory { get; } = Path.Combine(
        Path.GetTempPath(), "minrepo-regression-" + Guid.NewGuid().ToString("N"));
}

public sealed class Preferences
{
    public static Preferences Default { get; } = new();
    private readonly Dictionary<string, string> _values = new();
    public string Get(string key, string fallback) => _values.GetValueOrDefault(key, fallback);
    public void Set(string key, string value) => _values[key] = value;
    public void Remove(string key) => _values.Remove(key);
}
