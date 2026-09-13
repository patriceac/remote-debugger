using System.Globalization;
using System.Resources;

[assembly: NeutralResourcesLanguage("en")]

namespace RemoteDebugger.Core;

/// <summary>English is the neutral resource language; French and Spanish are satellites.</summary>
public static partial class UiText
{
    private static readonly ResourceManager Resources = new(
        "RemoteDebugger.Core.Localization.Strings", typeof(UiText).Assembly);

    public static string Get(string key, CultureInfo? culture = null) =>
        Resources.GetString(key, UiCulture.Resolve(culture ?? CultureInfo.CurrentUICulture))
        ?? throw new MissingManifestResourceException($"Missing UI resource: {key}");

    // Keep the user's regional number/date formats, independently of UI language.
    public static string Format(string template, params object?[] arguments) =>
        string.Format(CultureInfo.CurrentCulture, template, arguments);
}
