using System;
using System.Collections.Generic;
using System.Linq;

namespace EmbyIcons.Helpers
{
    internal static class LanguageHelper
    {
        private static readonly Dictionary<string, string> LangCodeMap = new(StringComparer.OrdinalIgnoreCase)
        {
            { "da", "dan" },
            { "en", "eng" },
            { "fr", "fre" },
            { "de", "ger" },
            { "es", "spa" },
            { "pl", "pol" },
            { "jp", "jpn" },
        };

        public static HashSet<string> ParseLanguageList(string? csv)
        {
            if (string.IsNullOrWhiteSpace(csv))
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            return csv.Split(',', StringSplitOptions.RemoveEmptyEntries)
                      .Select(s => s.Trim())
                      .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        public static string NormalizeLangCode(string code)
        {
            if (string.IsNullOrWhiteSpace(code))
                return code!;

            return LangCodeMap.TryGetValue(code.ToLowerInvariant(), out var mapped) ? mapped.ToLowerInvariant() : code.ToLowerInvariant();
        }
    }
}