using System.Globalization;

namespace ChestCounter;

/// <summary>
/// User interface language (French, English, Spanish or Russian), chosen on first launch and saved in config.json.
/// Texts are written inline as <c>L.T("français", "English", "español", "русский")</c>. Names stored in data.json (chest kinds,
/// treasure items) are kept in French for compatibility and translated when displayed (<see cref="Name"/>).
/// </summary>
public static class L
{
    /// <summary>Languages offered, in menu order.</summary>
    public static readonly string[] Codes = { "fr", "en", "es", "ru" };

    /// <summary>"fr", "en", "es" or "ru".</summary>
    public static string Code { get; private set; } = "fr";

    public static bool En => Code == "en";
    public static bool Es => Code == "es";
    public static bool Ru => Code == "ru";

    public static CultureInfo Culture { get; private set; } = CultureInfo.GetCultureInfo("fr-FR");

    public static void Set(string? code)
    {
        Code = Normalize(code) ?? "fr";
        Culture = CultureInfo.GetCultureInfo(T("fr-FR", "en-GB", "es-ES", "ru-RU"));
    }

    /// <summary>"fr", "en", "es", "ru" or null when the answer is not recognized.</summary>
    public static string? Normalize(string? code) => code?.Trim().ToLowerInvariant() switch
    {
        "fr" or "fra" or "fre" or "french" or "français" or "francais" or "francés" or "frances" => "fr",
        "en" or "eng" or "english" or "anglais" or "inglés" or "ingles" => "en",
        "es" or "spa" or "spanish" or "español" or "espanol" or "espagnol" => "es",
        "ru" or "rus" or "russian" or "russe" or "ruso" or "русский" or "russkiy" => "ru",
        _ => null,
    };

    /// <summary>Name of a language in that language (menus).</summary>
    public static string NativeName(string code) => code switch { "en" => "English", "es" => "Español", "ru" => "Русский", _ => "Français" };

    /// <summary>Language of Windows when it is one of ours, English otherwise.</summary>
    public static string SystemDefault => Normalize(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName) ?? "en";

    public static string T(string fr, string en, string es, string ru) => Code switch { "en" => en, "es" => es, "ru" => ru, _ => fr };

    public static string N(int n) => n.ToString("N0", Culture);

    /// <summary>"3 coffres" / "3 chests" / "3 cofres" / "3 сундука" (regular plurals: + "s"; Russian: "one|few|many").</summary>
    public static string Count(int n, string fr, string en, string es, string? ru = null) => Count(n, fr, fr + "s", en, en + "s", es, es + "s", ru);

    /// <summary>
    /// French: singular for 0 and 1. English and Spanish: singular for 1 only. Russian: three forms written
    /// "1|2-4|5+" in one string ("сундук|сундука|сундуков"), English forms while it is not written.
    /// </summary>
    public static string Count(int n, string frOne, string frMany, string enOne, string enMany, string esOne, string esMany, string? ru = null) => Code switch
    {
        "en" => $"{N(n)} {(n == 1 ? enOne : enMany)}",
        "es" => $"{N(n)} {(n == 1 ? esOne : esMany)}",
        "ru" => ru == null ? $"{N(n)} {(n == 1 ? enOne : enMany)}" : $"{N(n)} {RussianPlural(n, ru)}",
        _ => $"{N(n)} {(n > 1 ? frMany : frOne)}",
    };

    /// <summary>One of the three Russian forms "one|few|many": 1, 21... / 2-4, 22-24... / 0, 5-20, 25-30... (11-14 are "many").</summary>
    public static string RussianPlural(int n, string forms)
    {
        var f = forms.Split('|');
        if (f.Length < 3)
        {
            return forms;
        }
        var abs = Math.Abs(n);
        var last = abs % 10;
        var last2 = abs % 100;
        return last == 1 && last2 != 11 ? f[0] : last is >= 2 and <= 4 && last2 is < 12 or > 14 ? f[1] : f[2];
    }

    /// <summary>"12e" / "12th" / "12.º".</summary>
    public static string Ordinal(int n)
    {
        if (Es)
        {
            return $"{n}.º";
        }
        if (Ru)
        {
            return $"{n}-е";
        }
        if (!En)
        {
            return n == 1 ? "1er" : $"{n}e";
        }
        var suffix = (n % 100) is 11 or 12 or 13 ? "th" : (n % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" };
        return $"{n}{suffix}";
    }

    // ---- Times shown to the player (Settings): local time or UTC, 12- or 24-hour clock ------------------

    /// <summary>Show times in UTC instead of the PC's time zone.</summary>
    public static bool UtcTimes { get; set; }

    /// <summary>"auto" (the Windows setting), "12" or "24".</summary>
    public static string ClockFormat { get; set; } = "auto";

    /// <summary>The Windows setting: 24-hour unless the short time pattern has an "h" (12-hour) in it.</summary>
    public static bool WindowsClock24 => !CultureInfo.CurrentCulture.DateTimeFormat.ShortTimePattern.Contains('h');

    public static bool Clock24 => ClockFormat switch { "12" => false, "24" => true, _ => WindowsClock24 };

    /// <summary>A UTC moment as it is shown: in UTC or in the PC's time zone.</summary>
    public static DateTime Shown(DateTime utc)
    {
        var u = utc.Kind == DateTimeKind.Local ? utc.ToUniversalTime() : DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        return UtcTimes ? u : u.ToLocalTime();
    }

    /// <summary>" UTC" after a time shown in UTC, nothing in local time.</summary>
    public static string Zone => UtcTimes ? " UTC" : "";

    /// <summary>The .NET pattern of a time of day ("HH:mm" / "h:mm tt"), with seconds or not.</summary>
    public static string TimePattern(bool seconds = false) => Clock24 ? (seconds ? "HH:mm:ss" : "HH:mm") : (seconds ? "h:mm:ss tt" : "h:mm tt");

    /// <summary>A time of day of a moment already converted by <see cref="Shown"/> ("22:54" / "10:54 PM", + " UTC").</summary>
    public static string Clock(DateTime shown, bool seconds = false) => shown.ToString(TimePattern(seconds), Culture) + Zone;

    /// <summary>Time stamp of the activity log, now ("[22:54:07]").</summary>
    public static string Stamp => $"[{Clock(Shown(DateTime.UtcNow), seconds: true)}]";

    /// <summary>The PC's time zone as an offset from UTC ("UTC−8", "UTC+5:30").</summary>
    public static string LocalOffset
    {
        get
        {
            var o = TimeZoneInfo.Local.GetUtcOffset(DateTime.UtcNow);
            var sign = o < TimeSpan.Zero ? "−" : "+";
            o = o.Duration();
            return o == TimeSpan.Zero ? "UTC" : $"UTC{sign}{o.Hours}{(o.Minutes != 0 ? $":{o.Minutes:00}" : "")}";
        }
    }

    /// <summary>Short date + time of a match ("05/11 22:54" / "5 Nov 10:54 PM"), of a moment already converted by <see cref="Shown"/>.</summary>
    public static string DateTimeShort(DateTime shown) => shown.ToString((En ? "d MMM " : Ru ? "dd.MM " : "dd/MM ") + TimePattern(), Culture) + Zone;

    // French names stored in data.json -> English / Spanish / Russian display names.
    private static readonly Dictionary<string, (string En, string Es, string Ru)> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Coffre"] = ("Chest", "Cofre", "Сундук"),
        ["Coffre rare"] = ("Rare chest", "Cofre raro", "Редкий сундук"),
        ["Coffre enterré"] = ("Buried chest", "Cofre enterrado", "Закопанный сундук"),
        ["Coffre d'esprit (rare)"] = ("Sprite chest (rare)", "Cofre de espíritu (raro)", "Сундук духа (редкий)"),
        ["Coffre des affiches d'esprit"] = ("Sprite poster chest", "Cofre de los carteles de espíritu", "Сундук с плакатами духа"),
        ["Autre coffre"] = ("Other chest", "Otro cofre", "Другой сундук"),
        ["Canon à butin"] = ("Loot cannon", "Cañón de botín", "Пушка с добычей"),
        ["Caisse de munitions"] = ("Ammo box", "Caja de munición", "Ящик с боеприпасами"),
        ["Caisse de nourriture"] = ("Food box", "Caja de comida", "Ящик с едой"),
        ["Autre conteneur"] = ("Other container", "Otro contenedor", "Другой контейнер"),
        ["Trésor"] = ("Treasure", "Tesoro", "Сокровище"),
        ["Trésor (type inconnu)"] = ("Treasure (unknown type)", "Tesoro (tipo desconocido)", "Сокровище (тип неизвестен)"),
        ["Feu d'artifice doré"] = ("Golden firework", "Fuego artificial dorado", "Золотой фейерверк"),
        ["Extracteur portable"] = ("Portable extractor", "Extractor portátil", "Портативный экстрактор"),
        ["Localisateur d'esprit"] = ("Sprite locator", "Localizador de espíritus", "Локатор духов"),
        ["Localisateur chanceux"] = ("Lucky locator", "Localizador de la suerte", "Счастливый локатор"),
        ["Accélérateur d'extraction"] = ("Extraction accelerator", "Acelerador de extracción", "Ускоритель извлечения"),
        ["inconnu"] = ("unknown", "desconocido", "неизвестно"),
    };

    /// <summary>Display name of a chest kind or treasure item stored in French.</summary>
    public static string Name(string? stored)
    {
        if (stored != null)
        {
            stored = ChestKinds.Canonical(stored); // older name of a kind
            stored = TreasureNames.Canonical(stored);
        }
        if (string.IsNullOrEmpty(stored) || Code == "fr" || !Names.TryGetValue(stored, out var t))
        {
            return stored ?? "";
        }
        return Code switch { "en" => t.En, "ru" => t.Ru, _ => t.Es };
    }
}
