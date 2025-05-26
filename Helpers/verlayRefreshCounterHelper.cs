using System;
using System.IO;

namespace YourPluginNamespace.Helpers
{
    internal static class OverlayRefreshCounterHelper
    {
        private static readonly string CounterFile =
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "embyicons_overlayrefreshcounter.txt");

        public static int GetCounter()
        {
            try
            {
                if (File.Exists(CounterFile))
                    return int.Parse(File.ReadAllText(CounterFile));
            }
            catch { }
            return 0;
        }

        public static void IncrementCounter()
        {
            int v = GetCounter() + 1;
            File.WriteAllText(CounterFile, v.ToString());
        }
    }
}
