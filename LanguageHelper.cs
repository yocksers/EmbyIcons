using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace EmbyIcons.Helpers
{
    internal static class LanguageHelper
    {
        public static string NormalizeLangCode(string code)
        {
            if (string.IsNullOrWhiteSpace(code))
                return code;

            return code.ToLowerInvariant();
        }
    }
}