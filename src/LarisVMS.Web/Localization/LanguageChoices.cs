using System.Globalization;
using LarisVMS.Core.Interfaces;
using LarisVMS.Core.Localization;

namespace LarisVMS.Web.Localization;

/// <summary>What the language picker offers: every installed language, plus the pseudo-locale only
/// when an admin turned it on. The picker hides itself when this is just English.</summary>
public static class LanguageChoices
{
    public static async Task<IReadOnlyList<LocaleInfo>> GetAsync(LocaleCatalog catalog, ISettingsResolver? settings)
    {
        var showPseudo = false;
        if (settings is not null)
        {
            try { showPseudo = await settings.GetAsync(LocaleMiddleware.ShowPseudoSettingKey, false); }
            catch { /* pre-setup: no Settings table yet */ }
        }
        return catalog.Locales.Where(l => showPseudo || l.Source != LocaleSource.Pseudo).ToList();
    }

    /// <summary>The page's lang attribute value.</summary>
    public static string Lang => CultureInfo.CurrentUICulture.Name is { Length: > 0 } name ? name : LocaleCatalog.English;

    /// <summary>The page's dir attribute value.</summary>
    public static string Dir => CultureInfo.CurrentUICulture.TextInfo.IsRightToLeft ? "rtl" : "ltr";

    /// <summary>Bootstrap's stylesheet for the page's direction: the RTL build mirrors its
    /// layout, spacing, and start/end utilities.</summary>
    public static string BootstrapCss => CultureInfo.CurrentUICulture.TextInfo.IsRightToLeft ? "bootstrap.rtl.min.css" : "bootstrap.min.css";
}
