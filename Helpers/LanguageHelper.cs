using System;

namespace EmbyIcons.Helpers
{
    internal static class LanguageHelper
    {
        public static string NormalizeLangCode(string code)
        {
            if (string.IsNullOrWhiteSpace(code))
                return string.Empty;

            return code.ToLowerInvariant();
        }
    }
}