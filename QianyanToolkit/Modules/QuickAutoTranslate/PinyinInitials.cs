using System;
using System.Text;

namespace QToolKit.Modules.QuickAutoTranslate;

internal static class PinyinInitials
{
    private static readonly Encoding Gb2312;

    private static readonly (int Start, char Initial)[] InitialRanges =
    [
        (-20319, 'a'), (-20284, 'b'), (-19776, 'c'), (-19219, 'd'),
        (-18711, 'e'), (-18527, 'f'), (-18240, 'g'), (-17923, 'h'),
        (-17418, 'j'), (-16475, 'k'), (-16213, 'l'), (-15641, 'm'),
        (-15166, 'n'), (-14923, 'o'), (-14915, 'p'), (-14631, 'q'),
        (-14150, 'r'), (-14091, 's'), (-13319, 't'), (-12839, 'w'),
        (-12557, 'x'), (-11848, 'y'), (-11056, 'z'),
    ];

    static PinyinInitials()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Gb2312 = Encoding.GetEncoding(936, EncoderFallback.ReplacementFallback, DecoderFallback.ReplacementFallback);
    }

    public static string Get(string text)
    {
        var result = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            if (character is >= 'A' and <= 'Z')
            {
                result.Append(char.ToLowerInvariant(character));
                continue;
            }

            if (character is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                result.Append(character);
                continue;
            }

            if (!IsCjk(character))
                continue;

            var bytes = Gb2312.GetBytes(character.ToString());
            if (bytes.Length < 2 || bytes[0] == (byte)'?')
                continue;

            var code = (bytes[0] << 8) + bytes[1] - 65536;
            for (var i = InitialRanges.Length - 1; i >= 0; i--)
            {
                if (code < InitialRanges[i].Start)
                    continue;

                result.Append(InitialRanges[i].Initial);
                break;
            }
        }

        return result.ToString();
    }

    public static string Normalize(string text)
    {
        var result = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            if (char.IsLetterOrDigit(character) || IsCjk(character))
                result.Append(char.ToLowerInvariant(character));
        }

        return result.ToString();
    }

    private static bool IsCjk(char value)
        => value is >= '\u3400' and <= '\u9fff' or >= '\uf900' and <= '\ufaff';
}
