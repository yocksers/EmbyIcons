![image](https://github.com/user-attachments/assets/08e70e95-d4fd-45be-b206-1d0b0922fbe1)

---

EmbyIcons is a powerful plugin for Emby Server that enhances your media library by overlaying dynamic, informational icons directly onto your posters. It provides an at-a-glance summary of your media's technical specifications for movies, TV shows, seasons, and even individual episodes. With deep customization options, you can tailor the look and feel to perfectly match your library.

---

## Table of Contents
- [Features](#features)
- [Installation](#installation)
- [Configuration and Icon Naming](#configuration-and-icon-naming)
  - [Icon Naming Convention](#icon-naming-convention)
- [Configuration Options](#configuration-options)
  - [General Settings](#general-settings)
  - [Overlay Toggles](#overlay-toggles)
  - [TV Show Settings](#tv-show-settings)
  - [Alignment & Layout](#alignment--layout)
  - [Advanced Settings](#advanced-settings)
- [Troubleshooting](#troubleshooting)

---

## Features
*   **Extensive Icon Support**: Displays overlays for a wide range of media properties:
    *   Audio & Subtitle Languages
    *   Audio Channels (e.g., 5.1, 7.1, Stereo)
    *   Audio Codecs (e.g., DTS, EAC3, DTS-HD MA)
    *   Video Codecs (e.g., H265, HEVC, AV1)
    *   Video Format (e.g., HDR, Dolby Vision)
    *   Video Resolution (e.g., 4K, 1080p)
    *   Aspect Ratio (e.g., 16:9, 4:3)
    *   Custom Media Tags (e.g., 3D, Director's Cut)
    *   Community Rating (e.g., IMDB)
*   **Smart TV Show Handling**:
    *   Optionally show overlays on episode images.
    *   Display icons on Series and Season posters only if all child episodes share the same property (e.g., all have DTS audio).
    *   Includes a "Lite Mode" for significantly faster series scans by only checking the first episode.
*   **Deep Customization**:
    *   Independently set the on-screen corner alignment (Top Left, Top Right, Bottom Left, Bottom Right) for each icon category.
    *   Choose between horizontal or vertical stacking for icons within the same corner.
    *   Adjust icon size as a percentage of the poster's height.
    *   Control the final JPEG image quality.
    *   Customize the Community Rating icon's background shape, color, and opacity.
*   **Live Preview**: See your layout and alignment changes in real-time with a built-in preview image.
*   **Library Control**: Restrict icon overlays to specific libraries or apply them to all.
*   **Cache Management**: A simple one-click button to clear the icon cache after you've added or changed your icon files.

---

## Installation
1.  Ensure you are running a modern, stable version of Emby Server.
2.  Place the `EmbyIcons.dll` file into your Emby Server's `/plugins` directory.
3.  Restart Emby Server to load the plugin.
4.  Open the Emby Server dashboard, navigate to **Advanced -> Plugins** on the side menu, and click on **EmbyIcons**.
5.  Configure the settings as desired.

---

## Configuration and Icon Naming

You must supply your own icon images and place them in a single folder on your server. In the plugin settings, point the **Icons Folder** path to this folder.

A size of 100x100 pixels in `.png` format with a transparent background is recommended, but other formats like `.jpg` or `.webp` will also work.

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
## Configuration Options

### General Settings
| Setting | Description |
| :--- | :--- |
| **Icons Folder** | The full directory path to the folder containing your named icon images. |
| **Clear Icon Cache** | If you have added, removed, or changed any icon files, click this to apply the changes. |
| **Apply to these libraries** | Select the libraries you want icons to be applied to. Leave all unchecked to apply to all libraries. |

### Overlay Toggles
Enable or disable each icon type individually.

- [ ] Show Audio Language Icons
- [ ] Show Subtitle Language Icons
- [ ] Show Audio Channel Icons (e.g., 7.1)
- [ ] Show Audio Codec Icons (e.g., DTS)
- [ ] Show Video Format Icons (e.g., HDR)
- [ ] Show Video Codec Icons (e.g., H265)
- [ ] Show Custom Tag Icons
- [ ] Show Resolution Icons (e.g., 4K)
- [ ] Show Community Rating Icon
- [ ] Show Aspect Ratio Icons (e.g., 16:9)

### TV Show Settings
| Setting | Description |
| :--- | :--- |
| **Show overlays on episode images** | When disabled, overlays will only appear on movies, series, and seasons. |
| **Show icons on series/seasons** | Icons will only appear on a series/season poster if all child episodes share the same property. |
| **Use Lite Mode for series scans** | Faster, but less accurate. Only checks the first episode to determine icons for the entire series. |

### Alignment & Layout
For each icon category, you can independently configure:
*   **Icon Alignment**: Set the corner for the icons to appear (Top Left, Top Right, Bottom Left, Bottom Right).
*   **Layout Horizontally**: Check this to stack multiple icons of the same type side-by-side. Uncheck to stack them vertically.

You can also customize the appearance of the community rating score:
*   **Rating Background Shape**: Choose between None, a Circle, or a Square behind the score text.
*   **Background Color**: Select the color of the background shape.
*   **Background Opacity**: Adjust the transparency of the background shape.

### Advanced Settings
| Setting | Description |
| :--- | :--- |
| **Icon Size (%)** | Size of icons relative to the poster's height (1-100). |
| **Image Quality (10-100)** | Set the output JPEG quality for the final image. |
| **Enable image smoothing** | Enable anti-aliasing for smoother icons. Can improve quality, especially on scaled images. |
| **Enable detailed debug logging**| Enable this to log detailed information to the Emby server log. Useful for troubleshooting. |

---

## Troubleshooting
*   **Icons Not Appearing:**
    1.  Double-check that the **Icons Folder** path in settings is correct and that the Emby Server process has permission to read it.
    2.  Ensure your icon files are named *exactly* according to the `prefix.name.png` convention. The name match is case-insensitive.
    3.  For a **Custom Tag** icon to appear, a media item must have that tag applied to it in Emby.
    4.  Verify the media file has the required metadata. For a language icon to show, the audio or subtitle track must be correctly identified by Emby.
    5.  Make sure you have enabled the specific icon type in the **Overlay Toggles** section.
*   **Icons Not Updating:** If you change an icon file, modify plugin settings, or update a media item's tags, Emby's image cache may still show the old poster.
    1.  Go to the plugin settings page and click the **Clear Icon Cache** button. Your posters will update as you browse your library.
    2.  If that doesn't work, you can force a full refresh. In the Emby Dashboard, go to **Scheduled Tasks -> Refresh Metadata**. Run the task for the specific library and ensure "Replace existing images" is checked.
*   **TV Show Icons:** Remember that with the "Show icons on series/seasons" setting enabled (and Lite Mode disabled), an icon will only appear on a series poster if **every single episode** has that property. If a series has 10 episodes in 4K and one in 1080p, the `res.4k.png` icon will not be shown on the series poster.
---

-   Many thanks to Neminem, keitaro26, and Bakes82 in the Emby Forums for the help with this plugin!
-   A shoutout to Craggles for the awesome icons!

---

If you like the plugin and want to [Buy me a coffee](https://buymeacoffee.com/yockser)
