namespace MinRepoMobile.Services;

/// <summary>
/// Androidが生成するServiceからMAUIのDIコンテナーを参照するための入口です。
/// </summary>
public static class AppServiceProvider
{
    public static IServiceProvider? Current { get; internal set; }
}

