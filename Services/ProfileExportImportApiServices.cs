using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using EmbyIcons.Api;
using EmbyIcons.Caching;
using MediaBrowser.Model.Services;

namespace EmbyIcons.Services
{
    #region Export Profiles

    [Route(ApiRoutes.ExportProfiles, "GET", Summary = "Export icon profiles to JSON")]
    public class ExportProfilesRequest : IReturn<ExportProfilesResponse>
    {
        public string? ProfileIds { get; set; }
        public bool IncludeLibraryMappings { get; set; }
    }

    public class ExportProfilesResponse
    {
        public bool Success { get; set; }
        public string? JsonData { get; set; }
        public int ProfileCount { get; set; }
        public string? Error { get; set; }
    }

    public class ExportProfilesService : IService
    {
        public object Get(ExportProfilesRequest request)
        {
            try
            {
                var plugin = Plugin.Instance;
                if (plugin == null)
                {
                    return new ExportProfilesResponse
                    {
                        Success = false,
                        Error = "Plugin instance not available"
                    };
                }

                var service = new ProfileImportExportService(plugin.Logger, plugin.Configuration);

                List<Guid>? profileIds = null;
                if (!string.IsNullOrWhiteSpace(request.ProfileIds))
                {
                    profileIds = request.ProfileIds
                        .Split(',')
                        .Select(id => Guid.TryParse(id.Trim(), out var guid) ? guid : Guid.Empty)
                        .Where(g => g != Guid.Empty)
                        .ToList();
                }

                var result = service.ExportProfiles(profileIds, request.IncludeLibraryMappings);

                var jsonOptions = new JsonSerializerOptions
                {
                    WriteIndented = true
                };

                return new ExportProfilesResponse
                {
                    Success = result.Success,
                    JsonData = JsonSerializer.Serialize(result.ExportData, jsonOptions),
                    ProfileCount = result.ProfileCount
                };
            }
            catch (Exception ex)
            {
                return new ExportProfilesResponse
                {
                    Success = false,
                    Error = ex.Message
                };
            }
        }
    }

    #endregion

    #region Import Profiles

    [Route(ApiRoutes.ImportProfiles, "POST", Summary = "Import icon profiles from JSON")]
    public class ImportProfilesRequest : IReturn<ImportProfilesResponse>
    {
        public string JsonData { get; set; } = string.Empty;
        public bool OverwriteExisting { get; set; }
        public bool RenameOnConflict { get; set; } = true;
    }

    public class ImportProfilesResponse
    {
        public bool Success { get; set; }
        public int ImportedCount { get; set; }
        public int UpdatedCount { get; set; }
        public int SkippedCount { get; set; }
        public int FailedCount { get; set; }
        public List<string> ImportedProfiles { get; set; } = new();
        public List<string> UpdatedProfiles { get; set; } = new();
        public List<string> SkippedProfiles { get; set; } = new();
        public List<string> FailedProfiles { get; set; } = new();
        public string? Error { get; set; }
    }

    public class ImportProfilesService : IService
    {
        public object Post(ImportProfilesRequest request)
        {
            try
            {
                var plugin = Plugin.Instance;
                if (plugin == null)
                {
                    return new ImportProfilesResponse
                    {
                        Success = false,
                        Error = "Plugin instance not available"
                    };
                }

                if (string.IsNullOrWhiteSpace(request.JsonData))
                {
                    return new ImportProfilesResponse
                    {
                        Success = false,
                        Error = "JsonData is required"
                    };
                }

                var service = new ProfileImportExportService(plugin.Logger, plugin.Configuration);

                var importOptions = new ImportOptions
                {
                    OverwriteExisting = request.OverwriteExisting,
                    RenameOnConflict = request.RenameOnConflict
                };

                var result = service.ImportProfilesFromJson(request.JsonData, importOptions);

                if (result.Success && (result.ImportedCount > 0 || result.UpdatedCount > 0))
                {
                    plugin.SaveConfiguration();
                }

                return new ImportProfilesResponse
                {
                    Success = result.Success,
                    ImportedCount = result.ImportedCount,
                    UpdatedCount = result.UpdatedCount,
                    SkippedCount = result.SkippedCount,
                    FailedCount = result.FailedCount,
                    ImportedProfiles = result.ImportedProfiles,
                    UpdatedProfiles = result.UpdatedProfiles,
                    SkippedProfiles = result.SkippedProfiles,
                    FailedProfiles = result.FailedProfiles,
                    Error = result.Error
                };
            }
            catch (Exception ex)
            {
                return new ImportProfilesResponse
                {
                    Success = false,
                    Error = ex.Message
                };
            }
        }
    }

    #endregion

    #region Validate Profile Import

    [Route(ApiRoutes.ValidateProfileImport, "POST", Summary = "Validate profile import JSON without importing")]
    public class ValidateProfileImportRequest : IReturn<ValidateProfileImportResponse>
    {
        public string JsonData { get; set; } = string.Empty;
    }

    public class ValidateProfileImportResponse
    {
        public bool IsValid { get; set; }
        public List<string> Errors { get; set; } = new();
        public List<string> Warnings { get; set; } = new();
        public int ProfileCount { get; set; }
        public string? Version { get; set; }
    }

    public class ValidateProfileImportService : IService
    {
        public object Post(ValidateProfileImportRequest request)
        {
            try
            {
                var plugin = Plugin.Instance;
                if (plugin == null)
                {
                    return new ValidateProfileImportResponse
                    {
                        IsValid = false,
                        Errors = new List<string> { "Plugin instance not available" }
                    };
                }

                if (string.IsNullOrWhiteSpace(request.JsonData))
                {
                    return new ValidateProfileImportResponse
                    {
                        IsValid = false,
                        Errors = new List<string> { "JsonData is required" }
                    };
                }

                var service = new ProfileImportExportService(plugin.Logger, plugin.Configuration);
                var result = service.ValidateProfileJson(request.JsonData);

                return new ValidateProfileImportResponse
                {
                    IsValid = result.IsValid,
                    Errors = result.Errors,
                    Warnings = result.Warnings,
                    ProfileCount = result.ProfileCount,
                    Version = result.Version
                };
            }
            catch (Exception ex)
            {
                return new ValidateProfileImportResponse
                {
                    IsValid = false,
                    Errors = new List<string> { ex.Message }
                };
            }
        }
    }

    #endregion

    #region Template Cache Stats

    [Route(ApiRoutes.TemplateCacheStats, "GET", Summary = "Get icon template cache statistics")]
    public class TemplateCacheStatsRequest : IReturn<TemplateCacheStatsResponse> { }

    public class TemplateCacheStatsResponse
    {
        public bool Success { get; set; }
        public long CacheHits { get; set; }
        public long CacheMisses { get; set; }
        public long TotalRequests { get; set; }
        public double HitRate { get; set; }
        public long TemplatesGenerated { get; set; }
        public bool TemplatesCachingEnabled { get; set; }
        public string? Error { get; set; }
    }

    public class TemplateCacheStatsService : IService
    {
        public object Get(TemplateCacheStatsRequest request)
        {
            try
            {
                var plugin = Plugin.Instance;
                if (plugin == null)
                {
                    return new TemplateCacheStatsResponse
                    {
                        Success = false,
                        Error = "Plugin instance not available"
                    };
                }

                var enhancer = plugin.Enhancer;
                var templateCache = enhancer.TemplateCache;

                if (templateCache == null)
                {
                    return new TemplateCacheStatsResponse
                    {
                        Success = true,
                        TemplatesCachingEnabled = false,
                        CacheHits = 0,
                        CacheMisses = 0,
                        TotalRequests = 0,
                        HitRate = 0,
                        TemplatesGenerated = 0
                    };
                }

                var stats = templateCache.GetStats();

                return new TemplateCacheStatsResponse
                {
                    Success = true,
                    TemplatesCachingEnabled = plugin.Configuration.EnableIconTemplateCaching,
                    CacheHits = stats.CacheHits,
                    CacheMisses = stats.CacheMisses,
                    TotalRequests = stats.TotalRequests,
                    HitRate = stats.HitRate,
                    TemplatesGenerated = stats.TemplatesGenerated
                };
            }
            catch (Exception ex)
            {
                return new TemplateCacheStatsResponse
                {
                    Success = false,
                    Error = ex.Message
                };
            }
        }
    }

    #endregion

    #region Clear Template Cache

    [Route(ApiRoutes.ClearTemplateCache, "POST", Summary = "Clear the icon template cache")]
    public class ClearTemplateCacheRequest : IReturn<ClearTemplateCacheResponse> { }

    public class ClearTemplateCacheResponse
    {
        public bool Success { get; set; }
        public string? Message { get; set; }
        public string? Error { get; set; }
    }

    public class ClearTemplateCacheService : IService
    {
        public object Post(ClearTemplateCacheRequest request)
        {
            try
            {
                var plugin = Plugin.Instance;
                if (plugin == null)
                {
                    return new ClearTemplateCacheResponse
                    {
                        Success = false,
                        Error = "Plugin instance not available"
                    };
                }

                var enhancer = plugin.Enhancer;
                var templateCache = enhancer.TemplateCache;

                if (templateCache != null)
                {
                    templateCache.Clear();
                    return new ClearTemplateCacheResponse
                    {
                        Success = true,
                        Message = "Icon template cache cleared successfully"
                    };
                }
                else
                {
                    return new ClearTemplateCacheResponse
                    {
                        Success = true,
                        Message = "Template caching is not enabled"
                    };
                }
            }
            catch (Exception ex)
            {
                return new ClearTemplateCacheResponse
                {
                    Success = false,
                    Error = ex.Message
                };
            }
        }
    }

    #endregion
}
