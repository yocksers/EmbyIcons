using System;
using System.Collections.Generic;
using System.Linq;

namespace EmbyIcons.Helpers
{
    internal static class LanguageHelper
    {
        private static readonly Dictionary<string, string> LangCodeMap = new(StringComparer.OrdinalIgnoreCase)
        {
            { "aa", "aar" }, { "aar", "aar" },
            { "ab", "abk" }, { "abk", "abk" },
            { "af", "afr" }, { "afr", "afr" },
            { "ak", "aka" }, { "aka", "aka" },
            { "sq", "alb" }, { "alb", "alb" }, { "sqi", "alb" },
            { "am", "amh" }, { "amh", "amh" },
            { "ar", "ara" }, { "ara", "ara" },
            { "hy", "arm" }, { "arm", "arm" }, { "hye", "arm" },
            { "az", "aze" }, { "aze", "aze" },
            { "ba", "bak" }, { "bak", "bak" },
            { "be", "bel" }, { "bel", "bel" },
            { "bg", "bul" }, { "bul", "bul" },
            { "bn", "ben" }, { "ben", "ben" },
            { "bo", "tib" }, { "tib", "tib" }, { "bod", "tib" },
            { "br", "bre" }, { "bre", "bre" },
            { "bs", "bos" }, { "bos", "bos" },
            { "ca", "cat" }, { "cat", "cat" },
            { "cs", "cze" }, { "cze", "cze" }, { "ces", "cze" },
            { "cy", "wel" }, { "wel", "wel" }, { "cym", "wel" },
            { "da", "dan" }, { "dan", "dan" },
            { "de", "ger" }, { "ger", "ger" }, { "deu", "ger" },
            { "dz", "dzo" }, { "dzo", "dzo" },
            { "el", "gre" }, { "gre", "gre" }, { "ell", "gre" },
            { "en", "eng" }, { "eng", "eng" },
            { "es", "spa" }, { "spa", "spa" },
            { "et", "est" }, { "est", "est" },
            { "eu", "baq" }, { "baq", "baq" }, { "eus", "baq" },
            { "fa", "per" }, { "per", "per" }, { "fas", "per" },
            { "fi", "fin" }, { "fin", "fin" },
            { "fj", "fij" }, { "fij", "fij" },
            { "fo", "fao" }, { "fao", "fao" },
            { "fr", "fre" }, { "fre", "fre" }, { "fra", "fre" },
            { "ga", "gle" }, { "gle", "gle" },
            { "gl", "glg" }, { "glg", "glg" },
            { "gu", "guj" }, { "guj", "guj" },
            { "he", "heb" }, { "heb", "heb" },
            { "hi", "hin" }, { "hin", "hin" },
            { "hr", "hrv" }, { "hrv", "hrv" },
            { "hu", "hun" }, { "hun", "hun" },
            { "id", "ind" }, { "ind", "ind" },
            { "is", "ice" }, { "ice", "ice" }, { "isl", "ice" },
            { "it", "ita" }, { "ita", "ita" },
            { "ja", "jpn" }, { "jpn", "jpn" },
            { "jv", "jav" }, { "jav", "jav" },
            { "ka", "geo" }, { "geo", "geo" }, { "kat", "geo" },
            { "kk", "kaz" }, { "kaz", "kaz" },
            { "km", "khm" }, { "khm", "khm" },
            { "kn", "kan" }, { "kan", "kan" },
            { "ko", "kor" }, { "kor", "kor" },
            { "ku", "kur" }, { "kur", "kur" },
            { "ky", "kir" }, { "kir", "kir" },
            { "la", "lat" }, { "lat", "lat" },
            { "lb", "ltz" }, { "ltz", "ltz" },
            { "lo", "lao" }, { "lao", "lao" },
            { "lt", "lit" }, { "lit", "lit" },
            { "lv", "lav" }, { "lav", "lav" },
            { "mg", "mlg" }, { "mlg", "mlg" },
            { "mi", "mao" }, { "mao", "mao" }, { "mri", "mao" },
            { "mk", "mac" }, { "mac", "mac" }, { "mkd", "mac" },
            { "ml", "mal" }, { "mal", "mal" },
            { "mn", "mon" }, { "mon", "mon" },
            { "mr", "mar" }, { "mar", "mar" },
            { "ms", "may" }, { "may", "may" }, { "msa", "may" },
            { "mt", "mlt" }, { "mlt", "mlt" },
            { "my", "bur" }, { "bur", "bur" }, { "mya", "bur" },
            { "ne", "nep" }, { "nep", "nep" },
            { "nl", "dut" }, { "dut", "dut" }, { "nld", "dut" },
            { "no", "nor" }, { "nor", "nor" },
            { "pa", "pan" }, { "pan", "pan" },
            { "pl", "pol" }, { "pol", "pol" },
            { "ps", "pus" }, { "pus", "pus" },
            { "pt", "por" }, { "por", "por" },
            { "ro", "rum" }, { "rum", "rum" }, { "ron", "rum" },
            { "ru", "rus" }, { "rus", "rus" },
            { "sa", "san" }, { "san", "san" },
            { "sd", "snd" }, { "snd", "snd" },
            { "si", "sin" }, { "sin", "sin" },
            { "sk", "slo" }, { "slo", "slo" }, { "slk", "slo" },
            { "sl", "slv" }, { "slv", "slv" },
            { "so", "som" }, { "som", "som" },
            { "sr", "srp" }, { "srp", "srp" },
            { "sv", "swe" }, { "swe", "swe" },
            { "sw", "swa" }, { "swa", "swa" },
            { "ta", "tam" }, { "tam", "tam" },
            { "te", "tel" }, { "tel", "tel" },
            { "th", "tha" }, { "tha", "tha" },
            { "ti", "tir" }, { "tir", "tir" },
            { "tr", "tur" }, { "tur", "tur" },
            { "uk", "ukr" }, { "ukr", "ukr" },
            { "ur", "urd" }, { "urd", "urd" },
            { "vi", "vie" }, { "vie", "vie" },
            { "xh", "xho" }, { "xho", "xho" },
            { "yi", "yid" }, { "yid", "yid" },
            { "zh", "chi" }, { "chi", "chi" }, { "zho", "chi" },
            { "zu", "zul" }, { "zul", "zul" }
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

            return LangCodeMap.TryGetValue(code.ToLowerInvariant(), out var mapped)
                ? mapped.ToLowerInvariant()
                : code.ToLowerInvariant();
        }
    }
}