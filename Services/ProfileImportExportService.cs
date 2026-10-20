using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
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
        public const int MaxProfileNameLength = 256;

        public ExportResult ExportProfiles(List<Guid>? profileIds, bool includeLibraryMappings = false)
        {
            var profilesToExport = profileIds == null
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
                var profilesByExportedName = new Dictionary<string, IconProfile>(StringComparer.OrdinalIgnoreCase);

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

                        if (exportedProfile.Name.Length > MaxProfileNameLength)
                        {
                            result.FailedProfiles.Add(exportedProfile.Name.Substring(0, 32) + "...");
                            continue;
                        }

                        exportedProfile.Settings = SanitizeSettings(exportedProfile.Settings);

                        var existingProfile = _configuration.Profiles.FirstOrDefault(p =>
                            p.Name.Equals(exportedProfile.Name, StringComparison.OrdinalIgnoreCase));

                        if (existingProfile != null)
                        {
                            if (options.OverwriteExisting)
                            {
                                existingProfile.Settings = exportedProfile.Settings;
                                profilesByExportedName[exportedProfile.Name] = existingProfile;
                                result.UpdatedProfiles.Add(existingProfile.Name);
                                _logger.Info($"[EmbyIcons] Updated existing profile: {existingProfile.Name}");
                            }
                            else if (options.RenameOnConflict)
                            {
                                var newProfile = CreateProfileWithUniqueName(exportedProfile);
                                if (newProfile == null)
                                {
                                    result.FailedProfiles.Add(exportedProfile.Name);
                                    _logger.Warn($"[EmbyIcons] Could not find a unique name for imported profile: {exportedProfile.Name}");
                                    continue;
                                }
                                _configuration.Profiles.Add(newProfile);
                                profilesByExportedName[exportedProfile.Name] = newProfile;
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
                                Settings = exportedProfile.Settings
                            };
                            _configuration.Profiles.Add(newProfile);
                            profilesByExportedName[exportedProfile.Name] = newProfile;
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

                if (options.ImportLibraryMappings && exportData.LibraryMappings != null)
                {
                    ImportLibraryMappings(exportData.LibraryMappings, profilesByExportedName, options.KnownLibraryIds, result);
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

        private void ImportLibraryMappings(
            List<ExportedLibraryMapping> mappings,
            Dictionary<string, IconProfile> profilesByExportedName,
            ISet<string>? knownLibraryIds,
            ImportResult result)
        {
            foreach (var mapping in mappings)
            {
                if (mapping == null || string.IsNullOrWhiteSpace(mapping.LibraryId)) continue;

                if (string.IsNullOrWhiteSpace(mapping.ProfileName) ||
                    !profilesByExportedName.TryGetValue(mapping.ProfileName, out var profile))
                {
                    result.SkippedLibraryMappings.Add($"{mapping.LibraryId} (profile '{mapping.ProfileName}' was not imported)");
                    continue;
                }

                if (knownLibraryIds != null && !knownLibraryIds.Contains(mapping.LibraryId))
                {
                    result.SkippedLibraryMappings.Add($"{mapping.LibraryId} (library not found on this server)");
                    continue;
                }

                _configuration.LibraryProfileMappings.RemoveAll(m => m.LibraryId == mapping.LibraryId);
                _configuration.LibraryProfileMappings.Add(new LibraryMapping
                {
                    LibraryId = mapping.LibraryId,
                    ProfileId = profile.Id
                });
                result.ImportedLibraryMappingCount++;
                _logger.Info($"[EmbyIcons] Imported library mapping: {mapping.LibraryId} -> {profile.Name}");
            }
        }

        private static ProfileSettings SanitizeSettings(ProfileSettings? settings)
        {
            if (settings == null) return new ProfileSettings();
            ReplaceNullsWithDefaults(settings);
            return settings;
        }

        // Explicit JSON nulls overwrite property initializers; restore the defaults so
        // rendering code can rely on non-null strings and collections.
        private static void ReplaceNullsWithDefaults(object target)
        {
            var type = target.GetType();
            object? defaults = null;

            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!prop.CanRead || !prop.CanWrite || prop.PropertyType.IsValueType || prop.GetIndexParameters().Length > 0)
                    continue;

                var value = prop.GetValue(target);
                if (value == null)
                {
                    defaults ??= Activator.CreateInstance(type);
                    prop.SetValue(target, prop.GetValue(defaults));
                }
                else if (value is IList list)
                {
                    for (int i = list.Count - 1; i >= 0; i--)
                    {
                        var element = list[i];
                        if (element == null) list.RemoveAt(i);
                        else if (!(element is string) && !element.GetType().IsValueType) ReplaceNullsWithDefaults(element);
                    }
                }
            }
        }

        private const int MaxProfileNameSuffix = 1000;

        private IconProfile? CreateProfileWithUniqueName(ExportedProfile exportedProfile)
        {
            var baseName = exportedProfile.Name;

            for (int suffix = 1; suffix <= MaxProfileNameSuffix; suffix++)
            {
                var suffixText = $" ({suffix})";
                var stem = baseName.Length + suffixText.Length > MaxProfileNameLength
                    ? baseName.Substring(0, MaxProfileNameLength - suffixText.Length)
                    : baseName;
                var candidate = stem + suffixText;

                if (!_configuration.Profiles.Any(p => p.Name.Equals(candidate, StringComparison.OrdinalIgnoreCase)))
                {
                    return new IconProfile
                    {
                        Id = Guid.NewGuid(),
                        Name = candidate,
                        Settings = exportedProfile.Settings
                    };
                }
            }

            return null;
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
                        if (profile == null)
                        {
                            errors.Add("Found an empty profile entry");
                            continue;
                        }

                        if (string.IsNullOrWhiteSpace(profile.Name))
                        {
                            errors.Add("Found profile with empty name");
                        }

                        if (profile.Name != null && profile.Name.Length > MaxProfileNameLength)
                        {
                            errors.Add($"Profile name is too long (max {MaxProfileNameLength} characters): '{profile.Name.Substring(0, 32)}...'");
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

                if (exportData.LibraryMappings != null && exportData.LibraryMappings.Count > 0)
                {
                    warnings.Add($"The file contains {exportData.LibraryMappings.Count} library mapping(s). They will replace the current profile assignment for those libraries; mappings for libraries not on this server will be skipped");
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
        public bool ImportLibraryMappings { get; set; } = true;
        public ISet<string>? KnownLibraryIds { get; set; }
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
        public int ImportedLibraryMappingCount { get; set; }
        public List<string> SkippedLibraryMappings { get; set; } = new();
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
