using Microsoft.Extensions.DependencyInjection;

namespace MinRepoMobile;

public partial class App : Application
{
    private readonly IServiceProvider _services;

    public App(IServiceProvider services)
    {
        // App.xamlに定義した色・スタイルを先に読み込みます。
        //
        // AppのコンストラクターでMainPageを直接受け取ると、DIコンテナーは
        // Appより先にMainPageを生成します。その時点ではApp.xamlの
        // StaticResourceがまだ存在しないため、Android起動時に発生した
        // XAML例外がJavaProxyThrowableとして表示される場合があります。
        InitializeComponent();
        _services = services;
    }

    /// <summary>
    /// App.xamlの初期化完了後にMainPageをDIから取得し、画面を生成します。
    /// 画面生成で例外が発生した場合は、Android側の包括例外だけで終了させず、
    /// 実際の.NET例外を端末画面へ表示します。
    /// </summary>
    protected override Window CreateWindow(IActivationState? activationState)
    {
        try
        {
            var mainPage = _services.GetRequiredService<MainPage>();
            return new Window(new NavigationPage(mainPage));
        }
        catch (Exception ex)
        {
            return new Window(CreateStartupErrorPage(ex));
        }
    }

    /// <summary>
    /// 起動失敗時の内部例外を確認するためのフォールバック画面を作成します。
    /// App.xamlのリソースには依存せず、リソース初期化に問題があっても表示できます。
    /// </summary>
    private static ContentPage CreateStartupErrorPage(Exception exception)
    {
        var exceptionText = exception.ToString();

        return new ContentPage
        {
            Title = "起動エラー",
            BackgroundColor = Color.FromArgb("#FFF8E1"),
            Content = new ScrollView
            {
                Content = new VerticalStackLayout
                {
                    Padding = new Thickness(20),
                    Spacing = 12,
                    Children =
                    {
                        new Label
                        {
                            Text = "アプリの画面を生成できませんでした。",
                            FontSize = 20,
                            FontAttributes = FontAttributes.Bold,
                            TextColor = Color.FromArgb("#8A4B00"),
                        },
                        new Label
                        {
                            Text =
                                "下の.NET例外をコピーして確認してください。" +
                                "JavaProxyThrowableより具体的な原因が表示されます。",
                            TextColor = Color.FromArgb("#5D4037"),
                        },
                        new Editor
                        {
                            Text = exceptionText,
                            IsReadOnly = true,
                            AutoSize = EditorAutoSizeOption.TextChanges,
                            BackgroundColor = Colors.White,
                            TextColor = Colors.Black,
                        },
                    },
                },
            },
        };
    }
}
