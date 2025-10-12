using EmbyIcons.Api;
using MediaBrowser.Model.Services;
using System.Collections.Concurrent;

namespace EmbyIcons.Services
{
    [Route(ApiRoutes.ScanProgress, "GET", Summary = "Gets the progress of a long-running scan")]
    public class GetScanProgress : IReturn<ScanProgress>
    {
        [ApiMember(Name = "ScanType", Description = "The type of scan to get progress for (e.g., 'IconManager', 'FullSeriesScan').", IsRequired = true, DataType = "string", ParameterType = "query")]
        // FIX: Initialize non-nullable property.
        public string ScanType { get; set; } = string.Empty;
    }

    public class ScanProgress
    {
        public int Current { get; set; }
        public int Total { get; set; }
        // FIX: Initialize non-nullable property.
        public string Message { get; set; } = string.Empty;
        public bool IsComplete { get; set; }
    }

    public class ScanProgressService : IService
    {
        private static readonly ConcurrentDictionary<string, ScanProgress> _progressCache = new(System.StringComparer.OrdinalIgnoreCase);

        public static void UpdateProgress(string scanType, int current, int total, string message)
        {
            var progress = new ScanProgress { Current = current, Total = total, Message = message, IsComplete = current >= total };
            _progressCache.AddOrUpdate(scanType, progress, (key, old) => progress);
        }

        public static void ClearProgress(string scanType)
        {
            _progressCache.TryRemove(scanType, out _);
        }

        public object Get(GetScanProgress request)
        {
            if (string.IsNullOrEmpty(request.ScanType))
            {
                return new ScanProgress { IsComplete = true, Message = "Invalid scan type." };
            }

            if (_progressCache.TryGetValue(request.ScanType, out var progress))
            {
                return progress;
            }

            return new ScanProgress { IsComplete = true, Message = "Scan has not started." };
        }
    }
}