using System;
using System.IO;

namespace EmbyIcons.Helpers
{
    internal static class LoggingHelper
    {
        public static void Log(bool loggingEnabledOrAlwaysWhenTrue, string message)
        {
            if (!loggingEnabledOrAlwaysWhenTrue)
                return;

            try
            {
                string logPath = Path.Combine((Plugin.Instance?.GetConfiguredOptions()?.LogFolder) ?? @"C:\temp", "EmbyIcons.log");
                File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}\n");
            }
            catch
            {
                // ignore logging errors
            }
        }

        public static void LogAlways(string message)
        {
            try
            {
                string logPath = Path.Combine((Plugin.Instance?.GetConfiguredOptions()?.LogFolder) ?? @"C:\temp", "EmbyIcons.log");
                File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}\n");
            }
            catch
            {
                // ignore logging errors
            }
        }
    }
}