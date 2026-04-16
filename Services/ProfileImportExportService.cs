using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using EmbyIcons.Configuration;
using MediaBrowser.Model.Logging;

namespace EmbyIcons.Services
{
    public class ProfileImportExportService
    {
        private readonly ILogger _logger;
        private readonly PluginOptions _configuration;

        public ProfileImportExportService(ILogger logger, PluginOptions configuration)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        }
        public async Task<ExportResult> ExportProfilesToFileAsync(
            List<Guid>? profileIds,
            string filePath,
            bool includeLibraryMappings = false)
        {
            try
            {
                string resolvedPath;
                try { resolvedPath = Path.GetFullPath(filePath); }
                catch (Exception ex) { throw new ArgumentException("Invalid file path.", nameof(filePath), ex); }

                var result = ExportProfiles(profileIds, includeLibraryMappings);
                
                var options = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
                };

                var json = JsonSerializer.Serialize(result.ExportData, options);
                await File.WriteAllTextAsync(resolvedPath, json);

                _logger.Info($"[EmbyIcons] Exported {result.ProfileCount} profile(s) to: {resolvedPath}");
                
                return result;
            }
            catch (Exception ex)
            {
                _logger.ErrorException($"[EmbyIcons] Failed to export profiles to file: {filePath}", ex);
                throw;
            }
        }
        public ExportResult ExportProfiles(List<Guid>? profileIds, bool includeLibraryMappings = false)
        {
            var profilesToExport = profileIds == null || profileIds.Count == 0
                ? _configuration.Profiles.ToList()
                : _configuration.Profiles.Where(p => profileIds.Contains(p.Id)).ToList();

            var exportData = new ProfileExportData
            {
                Version = "1.0",
                ExportDate = DateTime.UtcNow,
                Profiles = profilesToExport.Select(p => new ExportedProfile
                {
                    Name = p.Name,
                    Settings = p.Settings
                }).ToList()
            };

            if (includeLibraryMappings)
            {
                exportData.LibraryMappings = _configuration.LibraryProfileMappings
                    .Where(m => profilesToExport.Any(p => p.Id == m.ProfileId))
                    .Select(m => new ExportedLibraryMapping
                    {
                        LibraryId = m.LibraryId,
                        ProfileName = profilesToExport.FirstOrDefault(p => p.Id == m.ProfileId)?.Name ?? "Unknown"
                    })
                    .ToList();
            }

            return new ExportResult
            {
                Success = true,
                ProfileCount = profilesToExport.Count,
                ExportData = exportData
            };
        }
        public async Task<ImportResult> ImportProfilesFromFileAsync(
            string filePath,
            ImportOptions options)
        {
            try
            {
                string resolvedPath;
                try { resolvedPath = Path.GetFullPath(filePath); }
                catch (Exception ex) { throw new ArgumentException("Invalid file path.", nameof(filePath), ex); }

                if (!File.Exists(resolvedPath))
                {
                    throw new FileNotFoundException("Profile file not found", resolvedPath);
                }

                var json = await File.ReadAllTextAsync(resolvedPath);
                var result = ImportProfilesFromJson(json, options);

                _logger.Info($"[EmbyIcons] Imported {result.ImportedCount} profile(s) from: {resolvedPath}");
                
                return result;
            }
            catch (Exception ex)
            {
                _logger.ErrorException($"[EmbyIcons] Failed to import profiles from file: {filePath}", ex);
                throw;
            }
        }
        public ImportResult ImportProfilesFromJson(string json, ImportOptions options)
        {
            try
            {
                var jsonOptions = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                };

                var exportData = JsonSerializer.Deserialize<ProfileExportData>(json, jsonOptions);
                
                if (exportData == null || exportData.Profiles == null)
                {
                    return new ImportResult
                    {
                        Success = false,
                        Error = "Invalid profile data: JSON is null or missing profiles"
                    };
                }

                var result = new ImportResult { Success = true };
                var importedProfiles = new List<IconProfile>();

                foreach (var exportedProfile in exportData.Profiles)
                {
                    if (exportedProfile == null) continue;
                    try
                    {
                        if (string.IsNullOrWhiteSpace(exportedProfile.Name))
                        {
                            result.FailedProfiles.Add("(unnamed)");
                            continue;
                        }

                        if (exportedProfile.Name.Length > 256)
                        {
                            result.FailedProfiles.Add(exportedProfile.Name.Substring(0, 32) + "...");
                            continue;
                        }
                        var existingProfile = _configuration.Profiles.FirstOrDefault(p => 
                            p.Name.Equals(exportedProfile.Name, StringComparison.OrdinalIgnoreCase));

                        if (existingProfile != null)
                        {
                            if (options.OverwriteExisting)
                            {
                                existingProfile.Settings = exportedProfile.Settings ?? new ProfileSettings();
                                result.UpdatedProfiles.Add(existingProfile.Name);
                                _logger.Info($"[EmbyIcons] Updated existing profile: {existingProfile.Name}");
                            }
                            else if (options.RenameOnConflict)
                            {
                                var newProfile = CreateProfileWithUniqueName(exportedProfile);
                                _configuration.Profiles.Add(newProfile);
                                importedProfiles.Add(newProfile);
                                result.ImportedProfiles.Add(newProfile.Name);
                                _logger.Info($"[EmbyIcons] Imported profile with renamed: {newProfile.Name}");
                            }
                            else
                            {
                                result.SkippedProfiles.Add(exportedProfile.Name);
                                _logger.Info($"[EmbyIcons] Skipped profile due to name conflict: {exportedProfile.Name}");
                            }
                        }
                        else
                        {
                            var newProfile = new IconProfile
                            {
                                Id = Guid.NewGuid(),
                                Name = exportedProfile.Name,
                                Settings = exportedProfile.Settings ?? new ProfileSettings()
                            };
                            _configuration.Profiles.Add(newProfile);
                            importedProfiles.Add(newProfile);
                            result.ImportedProfiles.Add(newProfile.Name);
                            _logger.Info($"[EmbyIcons] Imported new profile: {newProfile.Name}");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.ErrorException($"[EmbyIcons] Failed to import profile: {exportedProfile.Name}", ex);
                        result.FailedProfiles.Add(exportedProfile.Name);
                    }
                }

                result.ImportedCount = result.ImportedProfiles.Count;
                result.UpdatedCount = result.UpdatedProfiles.Count;
                result.SkippedCount = result.SkippedProfiles.Count;
                result.FailedCount = result.FailedProfiles.Count;

                return result;
            }
            catch (Exception ex)
            {
                _logger.ErrorException("[EmbyIcons] Failed to parse profile JSON", ex);
                return new ImportResult
                {
                    Success = false,
                    Error = $"Failed to parse JSON: {ex.Message}"
                };
            }
        }

        private IconProfile CreateProfileWithUniqueName(ExportedProfile exportedProfile)
        {
            var baseName = exportedProfile.Name;
            var suffix = 1;
            var newName = baseName;

            while (_configuration.Profiles.Any(p => 
                p.Name.Equals(newName, StringComparison.OrdinalIgnoreCase)))
            {
                newName = $"{baseName} ({suffix})";
                suffix++;
            }

            return new IconProfile
            {
                Id = Guid.NewGuid(),
                Name = newName,
                Settings = exportedProfile.Settings ?? new ProfileSettings()
            };
        }
        public ValidationResult ValidateProfileJson(string json)
        {
            try
            {
                var jsonOptions = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                };

                var exportData = JsonSerializer.Deserialize<ProfileExportData>(json, jsonOptions);
                
                if (exportData == null)
                {
                    return new ValidationResult
                    {
                        IsValid = false,
                        Errors = new List<string> { "Invalid JSON: Could not deserialize data" }
                    };
                }

                var errors = new List<string>();
                var warnings = new List<string>();

                if (exportData.Profiles == null || exportData.Profiles.Count == 0)
                {
                    errors.Add("No profiles found in export data");
                }
                else
                {
                    foreach (var profile in exportData.Profiles)
                    {
                        if (string.IsNullOrWhiteSpace(profile.Name))
                        {
                            errors.Add("Found profile with empty name");
                        }

                        if (profile.Name != null && profile.Name.Length > 256)
                        {
                            errors.Add($"Profile name is too long (max 256 characters): '{profile.Name.Substring(0, 32)}...'");
                        }

                        if (profile.Settings == null)
                        {
                            errors.Add($"Profile '{profile.Name}' has no settings");
                        }

                        var existingProfile = _configuration.Profiles.FirstOrDefault(p => 
                            p.Name.Equals(profile.Name, StringComparison.OrdinalIgnoreCase));
                        
                        if (existingProfile != null)
                        {
                            warnings.Add($"Profile '{profile.Name}' already exists and will be skipped or overwritten depending on import options");
                        }
                    }
                }

                return new ValidationResult
                {
                    IsValid = errors.Count == 0,
                    Errors = errors,
                    Warnings = warnings,
                    ProfileCount = exportData.Profiles?.Count ?? 0,
                    Version = exportData.Version
                };
            }
            catch (Exception ex)
            {
                return new ValidationResult
                {
                    IsValid = false,
                    Errors = new List<string> { $"JSON parsing error: {ex.Message}" }
                };
            }
        }
    }

    #region Data Transfer Objects

    public class ProfileExportData
    {
        public string Version { get; set; } = "1.0";
        public DateTime ExportDate { get; set; }
        public List<ExportedProfile> Profiles { get; set; } = new();
        public List<ExportedLibraryMapping>? LibraryMappings { get; set; }
    }

    public class ExportedProfile
    {
        public string Name { get; set; } = string.Empty;
        public ProfileSettings Settings { get; set; } = new();
    }

    public class ExportedLibraryMapping
    {
        public string LibraryId { get; set; } = string.Empty;
        public string ProfileName { get; set; } = string.Empty;
    }

    public class ExportResult
    {
        public bool Success { get; set; }
        public int ProfileCount { get; set; }
        public ProfileExportData? ExportData { get; set; }
        public string? Error { get; set; }
    }

    public class ImportOptions
    {
        public bool OverwriteExisting { get; set; }
        public bool RenameOnConflict { get; set; } = true;
    }

    public class ImportResult
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

    public class ValidationResult
    {
        public bool IsValid { get; set; }
        public List<string> Errors { get; set; } = new();
        public List<string> Warnings { get; set; } = new();
        public int ProfileCount { get; set; }
        public string? Version { get; set; }
    }

    #endregion
}
