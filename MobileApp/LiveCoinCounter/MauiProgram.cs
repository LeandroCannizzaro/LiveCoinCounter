using LiveCoinCounter.Converters;
using LiveCoinCounter.Services;
using LiveCoinCounter.Services.Recognition;

namespace LiveCoinCounter;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        ImageShrink.Shrinker = bytes => ImageHelper.ShrinkForUpload(bytes);

        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
            })
            .ConfigureMauiHandlers(handlers =>
                handlers.AddHandler<Controls.MosaicView, Platforms.Android.Controls.MosaicViewHandler>());

        return builder.Build();
    }
}
