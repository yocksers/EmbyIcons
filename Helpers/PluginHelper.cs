
namespace EmbyIcons.Helpers
{
    internal static class PluginHelper
    {
        public static bool IsDebugLoggingEnabled => Plugin.Instance?.Configuration.EnableDebugLogging ?? false;
    }
}
