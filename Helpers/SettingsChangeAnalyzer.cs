using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EmbyIcons.Configuration;

namespace EmbyIcons.Helpers
{
    internal static class SettingsChangeAnalyzer
    {
        private static readonly HashSet<string> LayoutOnlySettings = new HashSet<string>(StringComparer.Ordinal)
        {
            nameof(ProfileSettings.EnableForPosters),
            nameof(ProfileSettings.EnableForThumbs),
            nameof(ProfileSettings.EnableForBanners),
            nameof(ProfileSettings.ShowOverlaysForEpisodes),
            nameof(ProfileSettings.ShowOverlaysForSeasons),
            nameof(ProfileSettings.NormalizePosterAspectRatio),
            nameof(ProfileSettings.NormalizeThumbAspectRatio),
            nameof(ProfileSettings.NormalizeBannerAspectRatio),
            nameof(ProfileSettings.NormalizeMusicPosterAspectRatio),
            nameof(ProfileSettings.IconSize),
            nameof(ProfileSettings.TopLeftIconSize),
            nameof(ProfileSettings.TopRightIconSize),
            nameof(ProfileSettings.BottomLeftIconSize),
            nameof(ProfileSettings.BottomRightIconSize),
            nameof(ProfileSettings.IconSpacing),
            nameof(ProfileSettings.EdgePaddingHorizontal),
            nameof(ProfileSettings.EdgePaddingVertical),
            nameof(ProfileSettings.TopLeftHorizontalPadding),
            nameof(ProfileSettings.TopLeftVerticalPadding),
            nameof(ProfileSettings.TopRightHorizontalPadding),
            nameof(ProfileSettings.TopRightVerticalPadding),
            nameof(ProfileSettings.BottomLeftHorizontalPadding),
            nameof(ProfileSettings.BottomLeftVerticalPadding),
            nameof(ProfileSettings.BottomRightHorizontalPadding),
            nameof(ProfileSettings.BottomRightVerticalPadding),
            nameof(ProfileSettings.RatingFontSizeMultiplier),
            nameof(ProfileSettings.RatingPercentageSuffix),
            nameof(ProfileSettings.RatingTextVerticalOffset),
            nameof(ProfileSettings.EnableTopIconBar),
            nameof(ProfileSettings.TopIconBarHeight),
            nameof(ProfileSettings.TopIconBarColor),
            nameof(ProfileSettings.TopIconBarOpacity),
            nameof(ProfileSettings.TopIconBarOverlay),
            nameof(ProfileSettings.EnableBottomIconBar),
            nameof(ProfileSettings.BottomIconBarHeight),
            nameof(ProfileSettings.BottomIconBarColor),
            nameof(ProfileSettings.BottomIconBarOpacity),
            nameof(ProfileSettings.BottomIconBarOverlay),
            nameof(ProfileSettings.OnlyDrawBarsWhenIconsPresent),
            nameof(ProfileSettings.TagBasedIcons),
            nameof(ProfileSettings.SafeZones)
        };

        public static bool ProfileLookupChanged(PluginOptions oldOptions, PluginOptions newOptions)
        {
            if (oldOptions.EnableCollectionProfileLookup != newOptions.EnableCollectionProfileLookup) return true;

            var oldIds = (oldOptions.Profiles ?? new List<IconProfile>()).Select(p => p.Id).OrderBy(id => id);
            var newIds = (newOptions.Profiles ?? new List<IconProfile>()).Select(p => p.Id).OrderBy(id => id);
            if (!oldIds.SequenceEqual(newIds)) return true;

            return !MappingSignature(oldOptions).SequenceEqual(MappingSignature(newOptions));
        }

        public static bool ItemDataChanged(PluginOptions oldOptions, PluginOptions newOptions)
        {
            if (!string.Equals(oldOptions.IconsFolder ?? string.Empty, newOptions.IconsFolder ?? string.Empty, StringComparison.Ordinal)) return true;
            if (oldOptions.IconLoadingMode != newOptions.IconLoadingMode) return true;

            var oldProfiles = (oldOptions.Profiles ?? new List<IconProfile>())
                .GroupBy(p => p.Id)
                .ToDictionary(g => g.Key, g => g.First());

            foreach (var profile in newOptions.Profiles ?? new List<IconProfile>())
            {
                if (!oldProfiles.TryGetValue(profile.Id, out var oldProfile)) return true;
                if (!string.Equals(DataSignature(oldProfile.Settings), DataSignature(profile.Settings), StringComparison.Ordinal)) return true;
            }

            return false;
        }

        private static IEnumerable<string> MappingSignature(PluginOptions options)
        {
            return (options.LibraryProfileMappings ?? new List<LibraryMapping>())
                .Select(m => (m.LibraryId ?? string.Empty) + "=" + m.ProfileId.ToString("N"))
                .OrderBy(s => s, StringComparer.Ordinal);
        }

        internal static string DataSignature(ProfileSettings? settings)
        {
            if (settings == null) return string.Empty;

            var node = JsonSerializer.SerializeToNode(settings) as JsonObject ?? new JsonObject();
            var signature = new StringBuilder();

            foreach (var property in node.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                if (LayoutOnlySettings.Contains(property.Key)) continue;
                if (property.Key.EndsWith("OverlayHorizontal", StringComparison.Ordinal)) continue;
                if (property.Key.EndsWith("IconPriority", StringComparison.Ordinal)) continue;
                if (property.Key.EndsWith("BackgroundShape", StringComparison.Ordinal)) continue;
                if (property.Key.EndsWith("BackgroundColor", StringComparison.Ordinal)) continue;
                if (property.Key.EndsWith("BackgroundOpacity", StringComparison.Ordinal)) continue;

                signature.Append(property.Key).Append('=');
                if (property.Key.EndsWith("IconAlignment", StringComparison.Ordinal) && property.Value is JsonValue alignmentValue && alignmentValue.TryGetValue(out int alignment))
                {
                    signature.Append(alignment != (int)IconAlignment.Disabled ? "on" : "off");
                }
                else
                {
                    signature.Append(property.Value?.ToJsonString() ?? "null");
                }
                signature.Append(';');
            }

            return signature.ToString();
        }
    }
}
