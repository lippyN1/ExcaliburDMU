using System.Globalization;
using System.Resources;

namespace Excalibur.Core;

/// <summary>
/// Simple localization helper. Loads strings from embedded .resx (Strings.resx / Strings.ru.resx).
/// Usage: L.T("Help_Usage") or L.T("Error_DiskNotFound", diskNumber)
/// </summary>
public static class L
{
    private static readonly ResourceManager Rm = new(
        "Excalibur.Core.Resx.Strings",
        typeof(L).Assembly);

    public static string T(string key)
    {
        var s = Rm.GetString(key, CultureInfo.CurrentUICulture);
        return s ?? key;
    }

    public static string T(string key, params object[] args)
    {
        var s = Rm.GetString(key, CultureInfo.CurrentUICulture) ?? key;
        try { return string.Format(CultureInfo.CurrentCulture, s, args); }
        catch { return s; }
    }

    public static void SetLanguage(string? twoLetterCode)
    {
        if (string.IsNullOrWhiteSpace(twoLetterCode))
        {
            // Auto: use current UI culture
            return;
        }
        try
        {
            var culture = new CultureInfo(twoLetterCode);
            CultureInfo.CurrentUICulture = culture;
            CultureInfo.CurrentCulture = culture;
        }
        catch { /* ignore — invalid language code */ }
    }

    public static string CurrentLanguage()
    {
        return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
    }
}