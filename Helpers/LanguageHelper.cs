using System;
using System.Collections.Generic;
using System.Linq;

namespace EmbyIcons.Helpers
{
    internal static class LanguageHelper
    {
        /// <summary>
        /// Parses a comma-separated list of language codes into a HashSet, trimming and ignoring case.
        /// </summary>
        public static HashSet<string> ParseLanguageList(string? csv)
        {
            if (string.IsNullOrWhiteSpace(csv))
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            return csv.Split(',', StringSplitOptions.RemoveEmptyEntries)
                      .Select(s => s.Trim())
                      .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Normalizes a language code by trimming and lowercasing.
        /// </summary>
        public static string NormalizeLangCode(string code)
        {
            if (string.IsNullOrWhiteSpace(code))
                return code!;

            return code.Trim().ToLowerInvariant();
        }
    }
}
