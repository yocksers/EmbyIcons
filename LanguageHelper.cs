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
