using System;
using System.Text;

namespace AnoMech.Core.Recording;

/// <summary>JSONL primitives shared by every recording field.</summary>
internal static class RecordingJson
{
    private static readonly char[] Hex = "0123456789abcdef".ToCharArray();

    /// <summary>
    /// Escapes one JSON string value without escaping non-ASCII text. All C0 controls,
    /// quotes, and reverse solidus characters are encoded according to the JSON grammar.
    /// Malformed UTF-16 surrogates become U+FFFD so consumers can read the JSON string.
    /// </summary>
    public static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value)) return value ?? string.Empty;

        StringBuilder? escaped = null;
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            var validSurrogate = (char.IsHighSurrogate(c) && i + 1 < value.Length
                                  && char.IsLowSurrogate(value[i + 1]))
                              || (char.IsLowSurrogate(c) && i > 0
                                  && char.IsHighSurrogate(value[i - 1]));
            if (c >= ' ' && c != '"' && c != '\\' && (!char.IsSurrogate(c) || validSurrogate))
            {
                escaped?.Append(c);
                continue;
            }

            escaped ??= new StringBuilder(value.Length + 8);
            if (escaped.Length == 0 && i > 0) escaped.Append(value, 0, i);
            switch (c)
            {
                case '"': escaped.Append("\\\""); break;
                case '\\': escaped.Append("\\\\"); break;
                case '\b': escaped.Append("\\b"); break;
                case '\f': escaped.Append("\\f"); break;
                case '\n': escaped.Append("\\n"); break;
                case '\r': escaped.Append("\\r"); break;
                case '\t': escaped.Append("\\t"); break;
                default:
                    if (char.IsSurrogate(c)) c = '\uFFFD';
                    escaped.Append("\\u");
                    escaped.Append(Hex[(c >> 12) & 0xF]);
                    escaped.Append(Hex[(c >> 8) & 0xF]);
                    escaped.Append(Hex[(c >> 4) & 0xF]);
                    escaped.Append(Hex[c & 0xF]);
                    break;
            }
        }

        return escaped?.ToString() ?? value;
    }
}
