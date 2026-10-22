using System.Text;
using System.Text.RegularExpressions;

namespace HeyNorm.Speech;

/// <summary>
/// Strips what the system prompt forbids but models write anyway: emoji and markdown. Piper
/// doesn't skip them, it reads them out — measured: "Have a great day! 😊" ran 3.3 s against
/// 1.0 s without the emoji, and "**two layers**" added two seconds of asterisks.
///
/// Emoji are removed by code-point range rather than Unicode category: the category emoji
/// share (Other Symbol) also holds °, which Piper reads as "degrees" and has to stay.
/// </summary>
public static class Speakable
{
    // **bold**, __bold__, *italic*, _italic_, `code`: keep the words, drop the marks. Single
    // asterisks only count around a word, so "2*4" stays.
    private static readonly Regex Emphasis = new(@"(\*\*|__)(.+?)\1|(?<![\w*])\*(?!\s)([^*]+?)(?<!\s)\*(?![\w*])|`+([^`]*)`+");
    private static readonly Regex LeadingMarks = new(@"^\s*(#{1,6}\s+|[-*•]\s+|>\s*)");
    private static readonly Regex Spaces = new(@"\s{2,}");
    private static readonly Regex SpaceBeforePunctuation = new(@"\s+([,.!?;:])");

    public static string Clean(string text)
    {
        var withoutEmoji = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            // Most emoji sit outside the 16-bit range, as a surrogate pair of two chars.
            var width = char.IsSurrogatePair(text, i) ? 2 : 1;
            var codePoint = width == 2 ? char.ConvertToUtf32(text, i) : text[i];
            if (!IsEmoji(codePoint)) withoutEmoji.Append(text, i, width);
            i += width - 1;
        }

        var cleaned = LeadingMarks.Replace(withoutEmoji.ToString(), "");
        cleaned = Emphasis.Replace(cleaned, m =>
            m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Success ? m.Groups[3].Value : m.Groups[4].Value);
        cleaned = Spaces.Replace(cleaned, " ");
        return SpaceBeforePunctuation.Replace(cleaned, "$1").Trim();
    }

    private static bool IsEmoji(int c) =>
        c is >= 0x1F000 and <= 0x1FAFF   // emoji, pictographs, flags, skin tones
            or >= 0x2600 and <= 0x27BF   // misc symbols and dingbats: ☀ ✔ ✨
            or >= 0x2B00 and <= 0x2BFF   // ⭐ and arrows drawn as pictures
            or >= 0x2300 and <= 0x23FF   // ⌚ ⏰ ⏳
            or >= 0x1F1E6 and <= 0x1F1FF // regional indicators (flags)
            or 0x200D                    // zero-width joiner, which glues emoji sequences
            or 0xFE0F or 0xFE0E          // emoji/text presentation selectors
            or 0x20E3                    // keycap
            or 0x3030 or 0x303D;
}
