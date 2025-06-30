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
            { "en", "eng" }, // English
            { "de", "ger" }, // German
            { "fr", "fre" }, // French
            { "ja", "jpn" }, // Japanese
            { "jp", "jpn" }, // Japanese (alternate)
            { "ko", "kor" }, // Korean
            { "zh", "chi" }, // Chinese
            { "es", "spa" }, // Spanish
            { "it", "ita" }, // Italian
            { "sv", "swe" }, // Swedish
            { "ru", "rus" }, // Russian
            { "nl", "dut" }, // Dutch
            { "pt", "por" }, // Portuguese
            { "no", "nor" }, // Norwegian
            { "da", "dan" }, // Danish
            { "fi", "fin" }, // Finnish
            { "pl", "pol" }, // Polish

            { "eng", "eng" },
            { "deu", "ger" }, // German (alternate)
            { "ger", "ger" },
            { "fra", "fre" }, // French (alternate)
            { "fre", "fre" },
            { "jpn", "jpn" },
            { "kor", "kor" },
            { "zho", "chi" }, // Chinese (alternate)
            { "chi", "chi" },
            { "spa", "spa" },
            { "ita", "ita" },
            { "swe", "swe" },
            { "rus", "rus" },
            { "nld", "dut" }, // Dutch (alternate)
            { "dut", "dut" },
            { "por", "por" },
            { "nor", "nor" },
            { "dan", "dan" },
            { "fin", "fin" },
            { "pol", "pol" }
        };

        public static string NormalizeLangCode(string code)
        {
            if (string.IsNullOrWhiteSpace(code))
                return code;

            code = code.ToLowerInvariant();

            if (CustomLangCodeMap.TryGetValue(code, out var mappedFromCustom))
            {
                return mappedFromCustom;
            }

            try
            {
                CultureInfo ci = new CultureInfo(code);

                if (!string.IsNullOrWhiteSpace(ci.ThreeLetterISOLanguageName))
                {
                    if (CustomLangCodeMap.TryGetValue(ci.ThreeLetterISOLanguageName, out var mappedThreeLetter))
                    {
                        return mappedThreeLetter;
                    }
                    return ci.ThreeLetterISOLanguageName;
                }
                else if (!string.IsNullOrWhiteSpace(ci.TwoLetterISOLanguageName))
                {
                    if (CustomLangCodeMap.TryGetValue(ci.TwoLetterISOLanguageName, out var mappedTwoLetter))
                    {
                        return mappedTwoLetter;
                    }
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