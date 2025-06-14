![image](https://github.com/user-attachments/assets/08e70e95-d4fd-45be-b206-1d0b0922fbe1)

# EmbyIcons Plugin

---

## Overview

**EmbyIcons** is a powerful plugin for [Emby Server](https://emby.media/) that enhances your media library by overlaying dynamic, informational icons directly onto your posters. Go beyond simple language flags with icons for audio channels, video resolution, HDR, and more. This provides a rich, at-a-glance summary of your media's technical specifications, making your library more informative and visually appealing.

---

## Features

- **Multiple Icon Types**: Displays overlays for:
    - Audio Languages
    - Subtitle Languages
    - Audio Channels (e.g., Mono, Stereo, 5.1, 7.1)
    - Video Format (HDR and Dolby Vision)
    - Video Resolution (e.g., 480p, 720p, 1080p, 4K)
- **Deep Customization**:
    - Independently set the on-screen corner alignment for each icon type (`TopLeft`, `TopRight`, etc.).
    - Choose between horizontal or vertical stacking for multiple icons in the same corner.
    - Adjust icon size as a percentage of the poster's shorter dimension.
    - Control the final JPEG image quality to balance performance and visuals.
- **Smart TV Show Handling**:
    - By default, icons only appear on a TV Show's main poster if **all** episodes in the series share the same property (e.g., all are 4K, all have a French audio track).
    - Includes a **Lite Mode** for faster processing on large libraries by only checking the first episode of a series.
    - Overlays can also be enabled or disabled for individual episode posters.
- **Library Control**: Restrict icon overlays to a specific list of your libraries, or apply them to all.

---

## Installation

1.  Ensure you are running a modern, stable version of Emby Server.
2.  Place the `EmbyIcons.dll` file into your Emby Server's `/plugins` directory.
3.  Restart Emby Server to load the plugin.
4.  Open the Emby Server dashboard, navigate to **Plugins** on the side menu, and click on **EmbyIcons**.
5.  Configure the settings as desired.

---

## Configuration and Icon Naming

You must supply your own icon images and place them in a folder on your server. Point the plugin to this folder and name your files according to the convention below. A size of 100x100 pixels in PNG format is recommended.

### Icon Naming Convention

| Icon Type | Filename Format | Examples |
| :--- | :--- | :--- |
| **Audio** | `<lang_code>.png` | `eng.png`, `fre.png`, `jpn.png` |
| **Subtitle** | `srt.<lang_code>.png` | `srt.eng.png`, `srt.ger.png` |
| **Audio Channel** | `<type>.png` | `5.1.png`, `7.1.png`, `stereo.png` |
| **Video Format** | `<format>.png` | `hdr.png`, `dv.png` |
| **Resolution** | `<resolution>.png`| `4k.png`, `1080p.png`, `720p.png` |

### Configuration Options

| Setting | Description |
| :--- | :--- |
| **Icons Folder Path** | The full directory path to the folder containing your named icon images. |
| **Refresh Icon Folder** | Check this box and save to force the plugin to reload all icons from the folder. |
| **Allowed Libraries** | Comma-separated list of library names to apply icons to. Leave blank for all libraries. |
| **Show/Hide Icon Types**| Enable or disable each of the five overlay types (Audio, Subtitle, Channel, etc.) individually. |
| **Icon Alignment & Layout** | For each icon type, set the corner alignment and choose between horizontal or vertical stacking. |
| **Icon Size (% of shorter side)** | Size of icons relative to the poster's shorter side. Default is 10. |
| **JPEG Quality**| Set the output JPEG quality for the final image (10-100). Default is 75. |
| **Enable Image Smoothing**| Enable anti-aliasing for smoother icons, which can help on low-resolution posters. |
| **Use Lite Mode for TV Shows** | If enabled, only checks the first episode of a series for its properties. Much faster but less accurate. |

---

## Troubleshooting

-   **Icons Not Appearing**:
    -   Double-check that the **Icons Folder Path** is correct and that your icon files are named exactly according to the convention.
    -   Ensure the media file actually contains the metadata. For example, for a language icon to show, the audio or subtitle track must be correctly tagged with that language.
-   **Icons Not Updating**: If you change an icon file but keep the same name, Emby's cache may still show the old one. You can try using the **Refresh Icon Folder** option in the plugin settings, or trigger a full metadata refresh for your library in Emby.
-   **TV Show Icons**: Remember that in the default (non-Lite) mode, an icon will only show on a series poster if **every single episode** has that property. If a series has 10 episodes in 4K and one in 1080p, the 4K icon will not be shown on the series poster.

---

-   Many thanks to Neminem, keitaro26, and Bakes82 in the Emby Forums for the help with this plugin!!
-   A shoutout to Craggles for the awesome icons!!

---

If you like the plugin and want to [Buy me a coffee](https://buymeacoffee.com/yockser)
