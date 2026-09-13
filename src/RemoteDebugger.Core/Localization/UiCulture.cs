using System.Globalization;

namespace RemoteDebugger.Core;

public static class UiCulture
{
    public static CultureInfo Resolve(CultureInfo? systemLanguage) =>
        CultureInfo.GetCultureInfo(systemLanguage?.TwoLetterISOLanguageName switch
        {
            "fr" => "fr",
            "es" => "es",
            _ => "en"
        });

    public static CultureInfo Select(CultureInfo systemLanguage, string? languageOverride = null)
    {
        if (languageOverride == null) return Resolve(systemLanguage);
        try { return Resolve(CultureInfo.GetCultureInfo(languageOverride)); }
        catch (CultureNotFoundException) { return Resolve(null); }
    }

    public static void Initialize(string? languageOverride = null)
    {
        // At GUI startup, .NET exposes the Windows user's preferred UI language.
        // Initialize before constructing controls, including their field initializers.
        var language = Select(CultureInfo.CurrentUICulture, languageOverride);
        CultureInfo.DefaultThreadCurrentUICulture = language;
        CultureInfo.CurrentUICulture = language;
    }
}
