using STranslate.Plugin;
using System.Windows;
using System.Globalization;
using System.Text;

namespace STranslate.Helpers;

public static class BidiDirectionHelper
{
    private static readonly LangEnum[] RtlLanguages =
    [
        LangEnum.Arabic,
        LangEnum.Persian,
//        TODO uncomment these when you add other languages to the app
//        LangEnum.Hebrew,
//        LangEnum.Urdu,
//        LangEnum.Pashto,
//        LangEnum.Sindhi,
//        LangEnum.Kurdish,
//        LangEnum.Yiddish
    ];

    public static FlowDirection GetFlowDirection(
        string? text,
        LangEnum? language = null,
        LangEnum? detectedLanguage = null)
    {
        if (TryGetLanguageDirection(language, out var direction))
            return direction;

        if (TryGetLanguageDirection(detectedLanguage, out direction))
            return direction;

        return GetDirectionFromText(text);
    }

    private static bool TryGetLanguageDirection(
        LangEnum? language,
        out FlowDirection direction)
    {
        direction = FlowDirection.LeftToRight;

        if (language is null or LangEnum.Auto)
            return false;

        direction = IsRightToLeftLanguage(language.Value)
            ? FlowDirection.RightToLeft
            : FlowDirection.LeftToRight;

        return true;
    }

    private static FlowDirection GetDirectionFromText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return FlowDirection.LeftToRight;

        foreach (var rune in text.EnumerateRunes())
        {
            if (IsRightToLeftRune(rune))
                return FlowDirection.RightToLeft;

            if (IsLeftToRightRune(rune))
                return FlowDirection.LeftToRight;
        }

        return FlowDirection.LeftToRight;
    }

    private static bool IsRightToLeftLanguage(LangEnum language)
        => RtlLanguages.Contains(language);

    private static bool IsRightToLeftRune(Rune rune)
    {
        var value = rune.Value;

        return
            // Hebrew
            value is >= 0x0590 and <= 0x05FF ||

            // Arabic
            value is >= 0x0600 and <= 0x06FF ||

            // Arabic Supplement
            value is >= 0x0750 and <= 0x077F ||

            // Arabic Extended-A
            value is >= 0x08A0 and <= 0x08FF ||

            // Hebrew Presentation Forms
            value is >= 0xFB1D and <= 0xFB4F ||

            // Arabic Presentation Forms-A
            value is >= 0xFB50 and <= 0xFDFF ||

            // Arabic Presentation Forms-B
            value is >= 0xFE70 and <= 0xFEFF ||

            // Arabic Mathematical Alphabetic Symbols
            value is >= 0x1EE00 and <= 0x1EEFF;
    }

    private static bool IsLeftToRightRune(Rune rune)
    {
        var category = Rune.GetUnicodeCategory(rune);

        return category is
            UnicodeCategory.UppercaseLetter or
            UnicodeCategory.LowercaseLetter or
            UnicodeCategory.TitlecaseLetter or
            UnicodeCategory.ModifierLetter or
            UnicodeCategory.OtherLetter;
    }
}
