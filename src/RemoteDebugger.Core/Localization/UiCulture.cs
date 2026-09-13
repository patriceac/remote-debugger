using System.Globalization;

namespace RemoteDebugger.Core;

public static class UiCulture
{
    public static CultureInfo SystemLanguage { get; private set; } = CultureInfo.CurrentUICulture;
    // Async operations may have captured the previous thread UI culture. Resource
    // lookups use this shared choice so their later results follow live changes.
    public static CultureInfo? ApplicationLanguage { get; set; }
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
        SystemLanguage = CultureInfo.CurrentUICulture;
        var language = Select(SystemLanguage, languageOverride);
        Apply(language);
    }

    public static void Apply(CultureInfo language)
    {
        language = Resolve(language);
        ApplicationLanguage = language;
        CultureInfo.DefaultThreadCurrentUICulture = language;
        CultureInfo.CurrentUICulture = language;
    }
}
