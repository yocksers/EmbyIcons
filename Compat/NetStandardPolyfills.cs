namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit
    {
    }
}

namespace System.Collections.Generic
{
    internal static class CollectionExtensions
    {
        public static TValue GetValueOrDefault<TKey, TValue>(this IReadOnlyDictionary<TKey, TValue> dictionary, TKey key, TValue defaultValue)
        {
            return dictionary.TryGetValue(key, out var value) ? value : defaultValue;
        }

        public static TValue? GetValueOrDefault<TKey, TValue>(this IReadOnlyDictionary<TKey, TValue> dictionary, TKey key)
        {
            return dictionary.TryGetValue(key, out var value) ? value : default;
        }
    }
}

namespace System.Linq
{
    using System.Collections.Generic;

    internal static class EnumerableExtensions
    {
        public static IEnumerable<TSource> DistinctBy<TSource, TKey>(this IEnumerable<TSource> source, Func<TSource, TKey> keySelector)
        {
            var seenKeys = new HashSet<TKey>();
            foreach (var element in source)
            {
                if (seenKeys.Add(keySelector(element)))
                {
                    yield return element;
                }
            }
        }
    }
}

namespace System
{
    internal static class StringCompatExtensions
    {
        public static bool Contains(this string source, string value, StringComparison comparisonType)
        {
            return source.IndexOf(value, comparisonType) >= 0;
        }
    }
}

namespace EmbyIcons.Compat
{
    internal static class MathCompat
    {
        public static int Clamp(int value, int min, int max) => value < min ? min : (value > max ? max : value);
        public static long Clamp(long value, long min, long max) => value < min ? min : (value > max ? max : value);
        public static float Clamp(float value, float min, float max) => value < min ? min : (value > max ? max : value);
        public static double Clamp(double value, double min, double max) => value < min ? min : (value > max ? max : value);
    }

    internal static class RandomCompat
    {
        [System.ThreadStatic]
        private static System.Random? _random;

        public static System.Random Shared => _random ??= new System.Random(System.Guid.NewGuid().GetHashCode());
    }

    internal static class FileCompat
    {
        public static async System.Threading.Tasks.Task WriteAllTextAsync(string path, string contents)
        {
            using var writer = new System.IO.StreamWriter(path, false, System.Text.Encoding.UTF8);
            await writer.WriteAsync(contents).ConfigureAwait(false);
        }

        public static async System.Threading.Tasks.Task<string> ReadAllTextAsync(string path)
        {
            using var reader = new System.IO.StreamReader(path, System.Text.Encoding.UTF8);
            return await reader.ReadToEndAsync().ConfigureAwait(false);
        }
    }
}
