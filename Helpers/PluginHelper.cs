using System;

namespace EmbyIcons.Helpers
{
    internal static class PluginHelper
    {
        public static bool IsDebugLoggingEnabled => Plugin.Instance?.Configuration.EnableDebugLogging ?? false;

        public static void SafeDispose(IDisposable? disposable, Action<Exception>? onError = null)
        {
            if (disposable == null) return;
            
            try
            {
                disposable.Dispose();
            }
            catch (Exception ex)
            {
                onError?.Invoke(ex);
            }
        }
    }
}
