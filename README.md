![image](https://github.com/user-attachments/assets/08e70e95-d4fd-45be-b206-1d0b0922fbe1)

---

# EmbyIcons Plugin

[![Made for Emby](https://img.shields.io/badge/made%20for-emby-00a4dc.svg)](https://emby.media/)
[![BuyMeACoffee](https://img.shields.io/badge/support-buy%20me%20a%20coffee-yellow.svg)](https://buymeacoffee.com/yockser)

EmbyIcons is a powerful plugin for Emby Server that enhances your media library by overlaying dynamic, informational icons directly onto your posters. It provides an at-a-glance summary of your media's technical specifications for movies, TV shows, and collections. With deep, profile-based customization, you can tailor the look and feel to perfectly match your library's needs.

---

## Table of Contents
- [Features](#features)
- [Installation](#installation)
- [Icon Naming Convention](#icon-naming-convention)
- [Configuration Guide](#configuration-guide)
  - [Tabs Overview](#tabs-overview)
  - [Settings Tab](#settings-tab)
  - [Icon Manager Tab](#icon-manager-tab)
  - [Troubleshooter Tab](#troubleshooter-tab)
- [Troubleshooting](#troubleshooting)
- [Acknowledgements](#acknowledgements)

---

## Features
*   **Extensive Icon Support**: Displays overlays for a wide range of media properties:
    *   Audio & Subtitle Languages
    *   Audio Channels (e.g., 5.1, 7.1, Stereo)
    *   Audio & Video Codecs (e.g., DTS, EAC3, HEVC, AV1)
    *   Video Format (e.g., HDR, Dolby Vision)
    *   Video Resolution (e.g., 4K, 1080p, SD)
    *   Aspect Ratio (e.g., 16:9, 2.39:1)
    *   Parental & Community Ratings (e.g., PG-13, IMDB)
    *   Custom Media Tags (e.g., 3D, Director's Cut)
*   **Powerful Profile System**:
    *   Create multiple, distinct configuration profiles.
    *   Assign profiles to one or more libraries to customize the appearance of different content.
    *   Easily add, rename, or delete profiles.
*   **Complete Layout Control**:
    *   Independently set the on-screen corner for each icon category.
    *   Control the display **Priority** to define the precise drawing order of icons within a corner.
    *   Choose between horizontal or vertical stacking for each icon group.
    *   Customize the Community Rating icon's background shape, color, and opacity.
*   **Smart Aggregation for Series & Collections**:
    *   Display icons on Series or Collection posters based on the properties of their children.
    *   "Lite Mode" for faster scans by checking only the first item.
    *   Option to exclude the 'Specials' season from TV Show calculations.
*   **Icon Manager Tool**:
    *   Scan your library and icon folder to find out which icons are used, unused, or missing. A perfect utility for maintaining your custom icon set.
*   **Series Troubleshooter Tool**:
    *   Diagnose why an icon may not be appearing on a TV show by finding episodes with inconsistent properties (e.g., one episode is 1080p while the rest are 4K).
*   **Live Preview & Utilities**:
    *   See layout changes in real-time with a built-in settings preview.
    *   An Aspect Ratio Calculator to help you name your custom `ar` icons correctly.
    *   One-click cache clearing to apply changes to your icon files instantly.
    *   Hybrid icon loading mode uses bundled icons as a fallback for a great default experience.

---

## Installation
1.  Ensure you are running a modern, stable version of Emby Server.
2.  Place the `EmbyIcons.dll` file into your Emby Server's `/plugins` directory.
3.  Restart Emby Server to load the plugin.
4.  Open the Emby Server dashboard, navigate to **Plugins** on the left menu, find **EmbyIcons**, and click on it to open the configuration page.

---

## Icon Naming Convention
You must supply your own icon images and place them in a single folder on your server. A size of ~100x100 pixels in `.png` format with a transparent background is recommended, but `.jpg`, `.webp`, `.gif`, and `.bmp` are also supported.

Icons are identified using a strict prefix-based naming scheme: `` `prefix.name.png` ``. The prefix tells the plugin what type of icon it is, and the name is matched against media properties. Filenames are case-insensitive. Any files that do not follow this convention will be ignored.

| Icon Type | Prefix | Filename Format | Notes & Examples |
| :--- | :--- | :--- | :--- |
| Audio Language | `lang` | `lang.{language}.png` | `{language}` is the full display name, lowercased. Ex: `lang.english.png` |
| Subtitle Language | `sub` | `sub.{language}.png` | `{language}` is the full display name, lowercased. Ex: `sub.german.png` |
| Audio Channels | `ch` | `ch.{layout}.png` | Detected layouts include `5.1`, `7.1`, `stereo`, `mono`. Ex: `ch.7.1.png` |
| Audio Codec | `ac` | `ac.{codec}.png` | Detected from stream metadata. Ex: `ac.dts.png`, `ac.eac3.png`, `ac.dts-hdma.png` |
| Video Codec | `vc` | `vc.{codec}.png` | Detected from stream metadata. Ex: `vc.hevc.png`, `vc.av1.png`, `vc.h264.png` |
| Video Format (HDR) | `hdr` | `hdr.{format}.png` | Detects `hdr`, `dv` (Dolby Vision), `hdr10plus`. Ex: `hdr.dv.png`, `hdr.hdr.png` |
| Resolution | `res` | `res.{resolution}.png` | Matches keys like `4k`, `1080p`, `720p`, `sd`. Ex: `res.4k.png`, `res.1080p.png` |
| Aspect Ratio | `ar` | `ar.{ratio}.png` | Colon is replaced with 'x'. Ex: `ar.16x9.png`, `ar.2.39x1.png` |
| Parental Rating | `pr` | `pr.{rating}.png` | Lowercased rating from Emby. Slashes are replaced with hyphens. Ex: `pr.pg-13.png`, `pr.tv-ma.png` |
| Custom Tag | `tag` | `tag.{tag-name}.png` | Matches against media tags. The tag is lowercased and spaces are replaced with hyphens. Ex: `tag.3d.png`, `tag.directors-cut.png`|
| Community Rating | `rating`| `rating.{name}.png` | A static icon shown next to the community score. Ex: `rating.imdb.png` |

---
## Configuration Guide

### Tabs Overview
The configuration page is organized into four main tabs:
*   **Settings**: Configure global settings, manage icon profiles, and adjust all visual options.
*   **Icon Manager**: A utility to help you manage your custom icon files.
*   **Troubleshooter**: Tools to diagnose issues with TV show icons and calculate aspect ratios.
*   **Readme**: An offline copy of this guide.

### Settings Tab

This tab contains all the core options for the plugin. Settings are divided into logical sections.

#### Global Settings
These settings apply to all profiles and libraries.

| Setting | Description |
| :--- | :--- |
| **Icon Loading Mode** | **Hybrid** is recommended, as it uses your custom icons but falls back to the plugin's built-in icons if a custom one isn't found. |
| **Custom Icons Folder** | The full directory path to the folder containing your named icon images. A warning icon will appear if the path is invalid or empty. |
| **Image Output Format**| **Auto** is recommended. It preserves transparency by outputting a PNG if the source poster was a PNG, otherwise uses JPEG. |
| **Image Quality (10-100)** | Sets the compression quality for JPEG output. Higher is better. |
| **Enable image smoothing** | Applies anti-aliasing for smoother icons. Recommended for improving quality, especially on scaled images. |
| **Enable detailed debug logging** | Writes verbose logs to the main Emby server log, which is useful for diagnosing issues. |

#### Icon Profiles
Profiles are the core of the configuration. You can create different sets of rules and apply them to different libraries.

*   **Editing Profile**: Use this dropdown to switch between profiles you want to edit.
*   **Add (`+`)**: Creates a new profile with default settings.
*   **Rename**: Renames the currently selected profile.
*   **Delete (``)**: Deletes the currently selected profile. You cannot delete the last one.

All settings below this point are **specific to the profile selected in the "Editing Profile" dropdown.**

#### Profile Settings
*   **Assign this profile to libraries**: Check the box next to each library you want this profile to apply to. An unassigned library will not have any icons.
*   **Image Type Settings**: Select which image types (Posters, Thumbs, Banners) should have icons overlaid for this profile.

##### TV Show & Collection Settings
These settings control how icons are aggregated from child items to the parent Series or Collection poster.

| Setting | Description |
| :--- | :--- |
| **Show overlays on episode images** | When disabled, overlays will only appear on movies, series, and seasons. |
| **Show icons on TV show/collection posters**| Icons appear only if all child items share the same property. Language icons require a perfect match of the display language. |
| **Exclude 'Specials' season...** | When enabled, episodes in the 'Specials' season won't affect the icons shown on the main series poster. |
| **Use Lite Mode...** | A faster, but less accurate scan. Only checks the first episode/item to determine icons for the entire series/collection. |

##### Alignment, Layout & Priority
This grid gives you precise control over the visual placement of icons. For each icon category, you can independently configure:
*   **Alignment**: Sets the corner for the icons (e.g., Top Left, Bottom Right). `Disabled` hides this icon type.
*   **Priority**: Sets the drawing order for all icon groups assigned to the **same corner**. A lower number (like 1) is drawn first (top-most or left-most). A higher number (like 12) is drawn after. This allows you to fine-tune the stacking order.
*   **Layout Horizontally**: Check this to stack multiple icons of the same type side-by-side. Uncheck to stack them vertically.

You can also customize the appearance of the community rating score's background shape, color, and opacity.

##### Advanced Profile Settings
*   **Icon Size (% of poster height)**: Adjust the general size of all icons for this profile.

---

### Icon Manager Tab
This tool scans your media library and your custom icons folder to help you identify which icons are:
*   **Missing**: Needed by your library but not found in your folder.
*   **Found**: Correctly named and used by your library.
*   **Unused**: Exist in your folder but are not needed by any media.

Simply click the **Scan Library & Icons** button. The scan may take several minutes on large libraries, but results are cached.

### Troubleshooter Tab
This page contains two powerful utilities for diagnosing common issues.

#### Series Troubleshooter
Many icons (like Audio Language or Resolution) will only appear on a TV Show's poster if **all** of its episodes share that same property. This tool helps you find the inconsistencies.
1.  **Select Checks**: Uncheck any properties you don't care about to speed up the scan.
2.  **Scan a Show**: Search for a specific TV show and click "Scan Selected Show".
3.  **Scan All Shows**: Click "Scan All TV Shows for Inconsistencies" to run a full library report.
4.  The report will list any shows that have mismatches and show you exactly which episodes are different.

#### Aspect Ratio Calculator
If you are creating a custom aspect ratio icon and the "Snap to nearest" setting isn't working for your video's unique resolution, this tool can help.
1.  Enter your video's width and height in pixels.
2.  Click **Calculate**.
3.  The tool will provide the exact icon name to use for precise matching (e.g., `` `ar.32x9.png` ``).

---

## Troubleshooting
*   **Icons Not Appearing:**
    1.  **Profile & Alignment**: Ensure the library is assigned to a profile and the icon type you want to see is **not** set to `Disabled` in that profile's alignment settings.
    2.  **Folder & Naming**: Double-check the **Custom Icons Folder** path and verify your icon files are named *exactly* according to the convention. Use the **Icon Manager** to see the names the plugin expects.
    3.  **Metadata**: For language, codec, or resolution icons to show, the media file must have that information correctly identified by Emby. Verify the item's metadata in the web UI.
*   **TV Show Icons Are Missing:**
    *   This is the most common issue. By default, an icon (e.g., `` `res.4k.png` ``) will **only** appear on a series poster if **every single episode** is 4K. If even one episode is 1080p, the icon will not show.
    *   Use the **Series Troubleshooter** tab to find these inconsistencies.
    *   Alternatively, enable **Use Lite Mode for TV show scans**, which will base the icons on only the first episode, though this is less accurate.
*   **Icons Not Updating After Changes:**
    *   If you've changed settings or updated your icon files, you may need to clear Emby's caches.
    *   First, go to the plugin's **Settings** tab and click the **Clear Icon Cache** button at the bottom. Browse to a poster to see if it updates.
    *   If that doesn't work, perform a full image refresh. In the Emby Dashboard, go to **Scheduled Tasks -> Refresh People/Library Metadata**. Run the task for the specific library and ensure "Replace existing images" is checked.

---
## Acknowledgements
-   Many thanks to **Neminem**, **keitaro26**, and **Bakes82** from the Emby Community for their help and testing.
-   A shoutout to **Craggles** for the awesome default icons!
-   Special thanks to **Alexander Hürter** for supporting the project.

If you enjoy this plugin and wish to show your appreciation, you can...

<a href="https://buymeacoffee.com/yockser" target="_blank"><img src="https://cdn.buymeacoffee.com/buttons/v2/default-yellow.png" alt="Buy Me A Coffee" style="height: 60px !important;width: 217px !important;" ></a>
