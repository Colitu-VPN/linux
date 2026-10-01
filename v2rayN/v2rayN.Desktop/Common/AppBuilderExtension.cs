namespace v2rayN.Desktop.Common;

public static class AppBuilderExtension
{
    private static readonly string DefaultFontFamilyName =
        Path.Combine(Global.AvaAssets, "Fonts#Noto Sans SC");

    public static AppBuilder WithFontByDefault(this AppBuilder appBuilder)
    {
        var fallbacks = new List<FontFallback>();

        var notoSansSc = new FontFamily(DefaultFontFamilyName);

        fallbacks.Add(new FontFallback
        {
            FontFamily = notoSansSc
        });

        AddFontFallback(fallbacks, "Noto Color Emoji");
        AddFontFallback(fallbacks, "Noto Sans Symbols");

        return appBuilder.With(new FontManagerOptions
        {
            DefaultFamilyName = DefaultFontFamilyName,
            FontFallbacks = fallbacks.ToArray()
        });
    }

    private static void AddFontFallback(List<FontFallback> fallbacks, string fontFamilyName)
    {
        fallbacks.Add(new FontFallback
        {
            FontFamily = new FontFamily(fontFamilyName)
        });
    }
}
