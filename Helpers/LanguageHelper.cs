using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace EmbyIcons.Helpers
{
    internal static class LanguageHelper
    {
        private static readonly Dictionary<string, string> CustomLangCodeMap = new(StringComparer.OrdinalIgnoreCase)
        {
            // ISO 639-1 to preferred ISO 639-2/B or specific 3-letter icon name
            { "en", "eng" },
            { "de", "ger" },
            { "fr", "fre" },
            { "ja", "jpn" },
            { "ko", "kor" },
            { "zh", "chi" },
            { "es", "spa" },
            { "it", "ita" },
            { "zho", "chi" },
            { "deu", "ger" },
            { "fra", "fre" },
            { "jp", "jpn" }, 
            { "eng", "eng" },
            { "ger", "ger" },
            { "fre", "fre" },
            { "jpn", "jpn" },
            { "kor", "kor" },
            { "chi", "chi" },
            { "spa", "spa" },
            { "ita", "ita" }
            // ... (only add mappings where CultureInfo might not give your desired icon name)
        };

        public static string NormalizeLangCode(string code)
        {
            if (string.IsNullOrWhiteSpace(code))
                return code;

            code = code.ToLowerInvariant();

            // 1. Check custom map first for explicit overrides/aliases
            if (CustomLangCodeMap.TryGetValue(code, out var mappedFromCustom))
            {
                return mappedFromCustom;
            }

            // 2. Try to use CultureInfo to get a standardized ISO 639-2/T (3-letter) code
            try
            {
                // Try to create CultureInfo from the code
                CultureInfo ci = new CultureInfo(code);

                // Prioritize ISO 639-2 three-letter code
                if (!string.IsNullOrWhiteSpace(ci.ThreeLetterISOLanguageName))
                {
                    // Check if the three-letter code from CultureInfo is in our custom map
                    // to resolve to a preferred icon name (e.g., 'deu' -> 'ger')
                    if (CustomLangCodeMap.TryGetValue(ci.ThreeLetterISOLanguageName, out var mappedThreeLetter))
                    {
                        return mappedThreeLetter;
                    }
                    return ci.ThreeLetterISOLanguageName;
                }
                // Fallback to two-letter code if three-letter is not available or useful
                else if (!string.IsNullOrWhiteSpace(ci.TwoLetterISOLanguageName))
                {
                    // Check if the two-letter code from CultureInfo is in our custom map
                    // to resolve to a preferred icon name (e.g., 'de' -> 'ger')
                    if (CustomLangCodeMap.TryGetValue(ci.TwoLetterISOLanguageName, out var mappedTwoLetter))
                    {
                        return mappedTwoLetter;
                    }
                    // If no specific mapping, return the two-letter code as it might match an icon
                    return ci.TwoLetterISOLanguageName;
                }
            }
            catch (CultureNotFoundException)
            {

            }

            return code;
        }
    }
}