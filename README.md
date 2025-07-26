![image](https://github.com/user-attachments/assets/08e70e95-d4fd-45be-b206-1d0b0922fbe1)

---

# EmbyIcons Plugin

EmbyIcons is a powerful plugin for Emby Server that enhances your media library by overlaying dynamic, informational icons directly onto your posters. It provides an at-a-glance summary of your media's technical specifications for movies, TV shows, seasons, and even individual episodes. With deep, profile-based customization, you can tailor the look and feel to perfectly match your library's needs.

---

## Table of Contents
- [Features](#features)
- [Installation](#installation)
- [Configuration and Icon Naming](#configuration-and-icon-naming)
  - [Icon Naming Convention](#icon-naming-convention)
- [Configuration Guide](#configuration-guide)
  - [Tabs Overview](#tabs-overview)
  - [General Settings](#general-settings)
  - [Icon Profiles](#icon-profiles)
  - [Profile Settings](#profile-settings)
    - [Assign to Libraries](#assign-to-libraries)
    - [TV Show Settings](#tv-show-settings)
    - [Alignment, Layout & Priority](#alignment-layout--priority)
    - [Advanced Profile Settings](#advanced-profile-settings)
  - [Icon Manager](#icon-manager)
- [Troubleshooting](#troubleshooting)
- [Acknowledgements](#acknowledgements)

---

## Features
*   **Extensive Icon Support**: Displays overlays for a wide range of media properties:
    *   Audio & Subtitle Languages
    *   Audio Channels (e.g., 5.1, 7.1)
    *   Audio & Video Codecs (e.g., DTS, HEVC, AV1)
    *   Video Format (e.g., HDR, Dolby Vision)
    *   Video Resolution (e.g., 4K, 1080p)
    *   Aspect Ratio (e.g., 16:9)
    *   Custom Media Tags (e.g., 3D)
    *   Community Rating (e.g., IMDB)
*   **Powerful Profile System**:
    *   Create multiple configuration profiles for different types of content.
    *   Assign specific profiles to one or more libraries.
    *   Clone, rename, and manage profiles with ease.
*   **Complete Layout Control**:
    *   Independently set the on-screen corner alignment for each icon category.
    *   **NEW**: Control the display **Priority** to define the drawing order of icons within a corner.
    *   Choose between horizontal or vertical stacking for icons.
    *   Customize the Community Rating icon's background shape, color, and opacity.
*   **Smart TV Show Handling**:
    *   Optionally show overlays on episode images.
    *   Display icons on Series/Seasons only if all child episodes share the same property.
    *   "Lite Mode" for faster series scans by checking only the first episode.
*   **Icon Manager Tool**:
    *   Scan your library and icon folder to find out which icons are used, unused, or missing.
    *   A perfect tool for maintaining your custom icon set.
*   **Live Preview & Cache Control**:
    *   See layout changes in real-time with a built-in preview.
    *   A one-click button to clear the icon cache after changing icon files.
    *   Hybrid icon loading mode uses bundled icons as a fallback for a great default experience.

---

## Installation
1.  Ensure you are running a modern, stable version of Emby Server.
2.  Place the `EmbyIcons.dll` file into your Emby Server's `/plugins` directory.
3.  Restart Emby Server to load the plugin.
4.  Open the Emby Server dashboard, navigate to **Plugins** on the left menu, find **EmbyIcons** in the list, and click on it to open the configuration page.

---

## Configuration and Icon Naming

You must supply your own icon images and place them in a single folder on your server. A size of 100x100 pixels in `.png` format with a transparent background is recommended, but other formats like `.jpg` or `.webp` will also work.

### Icon Naming Convention
Icons are identified using a strict prefix-based naming scheme: `prefix.name.png`. The prefix tells the plugin what type of icon it is, and the name is matched against media properties. Filenames are case-insensitive. Any files that do not follow this convention will be ignored.

| Icon Type | Prefix | Filename Format | Examples |
| :--- | :--- | :--- | :--- |
| Audio Language | `lang` | `lang.{language}.png` | `lang.english.png`, `lang.french.png` |
| Subtitle Language | `sub` | `sub.{language}.png` | `sub.english.png`, `sub.german.png` |
| Audio Channels | `ch` | `ch.{layout}.png` | `ch.5.1.png`, `ch.7.1.png`, `ch.stereo.png` |
| Audio Codec | `ac` | `ac.{codec}.png` | `ac.dts.png`, `ac.truehd.png`, `ac.eac3.png` |
| Video Codec | `vc` | `vc.{codec}.png` | `vc.hevc.png`, `vc.av1.png`, `vc.h264.png` |
| Video Format | `hdr` | `hdr.{format}.png` | `hdr.hdr.png`, `hdr.hdr10plus.png`, `hdr.dv.png` |
| Resolution | `res` | `res.{resolution}.png` | `res.4k.png`, `res.1080p.png`, `res.sd.png` |
| Aspect Ratio | `ar` | `ar.{ratio}.png` | `ar.16x9.png`, `ar.4x3.png` |
| Custom Tag | `tag` | `tag.{tag_name}.png` | `tag.3d.png`, `tag.directors cut.png` |
| Community Rating | `rating`| `rating.{name}.png` | `rating.imdb.png` |

---
## Configuration Guide

### Tabs Overview
The configuration page is organized into three main tabs:
*   **Settings**: Configure global settings, manage icon profiles, and adjust all visual options.
*   **Icon Manager**: A utility to help you manage your custom icon files.
*   **Readme**: An offline copy of the plugin's naming conventions and guide.

### General Settings
This section contains settings that apply globally, regardless of the active profile.

| Setting | Description |
| :--- | :--- |
| **Icon Loading Mode** | Choose how icons are loaded. **Hybrid** is recommended, as it uses your custom icons but falls back to the plugin's built-in icons if a custom one isn't found. |
| **Custom Icons Folder** | The full directory path to the folder containing your named icon images. |

### Icon Profiles
Profiles are the core of the configuration. You can create different sets of rules and apply them to different libraries. For example, you could have one profile for Movies and another for Kids' TV shows.

*   **Editing Profile**: Use this dropdown to switch between profiles you want to edit.
*   **Add Profile (`+`)**: Creates a new profile, inheriting the settings from the currently selected profile.
*   **Rename Profile**: Renames the currently selected profile.
*   **Delete Profile (``)**: Deletes the currently selected profile. You cannot delete the last profile.

### Profile Settings
All settings below this point are **specific to the currently selected profile.**

#### Assign to Libraries
In this section, check the box next to each library you want this profile's settings to apply to. An unassigned library will not have any icons.

#### TV Show Settings
| Setting | Description |
| :--- | :--- |
| **Show overlays on episode images** | When disabled, overlays will only appear on movies, series, and seasons. |
| **Show icons on series/seasons** | Icons will only appear on a series/season poster if all child episodes share the same property. |
| **Use Lite Mode for series scans** | Faster, but less accurate. Only checks the first episode to determine icons for the entire series. |

#### Alignment, Layout & Priority
This is where you control the visual placement of the icons. For each icon category, you can independently configure:
*   **Alignment**: Set the corner for the icons to appear (Top Left, Top Right, Bottom Left, Bottom Right). Set to **Disabled** to hide this icon type for this profile.
*   **Priority**: Set the drawing order for icons in the same corner. A lower number (like 1) will be drawn first (top-most or left-most). A higher number (like 10) will be drawn after.
*   **Layout Horizontally**: Check this to stack multiple icons of the same type side-by-side. Uncheck to stack them vertically.

You can also customize the appearance of the community rating score:
*   **Rating Background Shape**: Choose between None, a Circle, or a Square behind the score text.
*   **Background Color & Opacity**: Select the color and transparency of the background shape.

#### Advanced Profile Settings
| Setting | Description |
| :--- | :--- |
| **Icon Size (%)** | Size of icons relative to the poster's height (1-100). |
| **Image Quality (10-100)** | Set the output JPEG quality for the final image. |
| **Enable image smoothing** | Enable anti-aliasing for smoother icons. Can improve quality, especially on scaled images. |

Below these sections, you can also find options for global debug logging and a button to **Clear Icon Cache**. If you have added, removed,or changed any icon files, click this to apply the changes.

### Icon Manager
This tool scans your media library and your custom icons folder to help you identify which icons are **Missing** (needed by your library but not found in your folder), **Found** (correctly used), or **Unused** (exist in your folder but are not needed by any media).
1. Click the **Scan Library & Icons** button.
2. The scan may take several minutes on large libraries. The results are cached until the next server restart.
3. Review the collapsible sections to see which icon files you may need to add or can safely remove.

---

## Troubleshooting
*   **Icons Not Appearing:**
    1.  **Check Profile Assignment**: Ensure the library you are viewing has been assigned a profile on the configuration page.
    2.  **Check Alignment**: In the active profile for that library, make sure the icon type you want to see is **not** set to `Disabled`.
    3.  **Check Folder & Permissions**: Double-check that the **Custom Icons Folder** path is correct and that the Emby Server process has permission to read it.
    4.  **Check Naming Convention**: Ensure your icon files are named *exactly* according to the `prefix.name.png` convention. Use the **Icon Manager** to see what names the plugin is looking for.
    5.  **Check Metadata**: For a language icon to show, the audio or subtitle track must be correctly identified by Emby. Verify the metadata for the item in question.
*   **Icons Not Updating:** If you change an icon file, modify plugin settings, or update media tags, Emby's image cache may still show the old poster.
    1.  Go to the plugin settings page and click the **Clear Icon Cache** button. Your posters will update as you browse your library.
    2.  If that doesn't work, force a full refresh. In the Emby Dashboard, go to **Scheduled Tasks -> Refresh Metadata**. Run the task for the specific library and ensure "Replace existing images" is checked.
*   **TV Show Icons:** Remember that with the "Show icons on series/seasons" setting enabled (and Lite Mode disabled), an icon will only appear on a series poster if **every single episode** has that property. If a series has 10 episodes in 4K and one in 1080p, the `res.4k.png` icon will not be shown on the series poster.

---
## Acknowledgements
-   Many thanks to Neminem, keitaro26, and Bakes82 in the Emby Forums for the help with this plugin!
-   A shoutout to Craggles for the awesome icons!
-   Special thanks to Alexander Hürter for supporting the project

If you like the plugin and want to [Buy me a coffee](https://buymeacoffee.com/yockser)
