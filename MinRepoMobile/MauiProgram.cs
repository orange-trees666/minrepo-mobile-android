using MinRepoMobile.Services;
using Microsoft.Extensions.Logging;

namespace MinRepoMobile;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();

        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                // Android標準フォントを利用するため、追加フォントは不要です。
            });

        // 取得処理と画面をDIへ登録し、画面側で直接newしない構成にします。
        builder.Services.AddSingleton<StoreCatalogService>();
        builder.Services.AddSingleton<MinRepoExtractionService>();
        builder.Services.AddSingleton<MainPage>();

#if DEBUG
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }
}
