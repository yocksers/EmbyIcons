using System.Threading;

namespace EmbyIcons.Helpers
{
    internal static class PluginHelper
    {
        private static readonly AsyncLocal<bool> _debugLoggingSuppressed = new AsyncLocal<bool>();

        public static bool IsDebugLoggingEnabled => !_debugLoggingSuppressed.Value && (Plugin.Instance?.Configuration.EnableDebugLogging ?? false);

        public static void SuppressDebugLoggingForCurrentFlow() => _debugLoggingSuppressed.Value = true;
    }
}
