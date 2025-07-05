![image](https://github.com/user-attachments/assets/08e70e95-d4fd-45be-b206-1d0b0922fbe1)

---

## Overview

**EmbyIcons** is a powerful plugin for [Emby Server](https://emby.media/) that enhances your media library by overlaying dynamic, informational icons directly onto your posters. It works for both movie and TV show posters, going beyond simple language flags to include icons for audio codecs, video resolution, custom tags, and more. This provides an at-a-glance summary of your media's technical specifications, making your library more informative and visually appealing.

---

## Features

- **Smart TV Show Handling**:
    - Show overlays on TV show posters!
    - By default, icons only appear on a TV Show's main poster if **all** episodes in the series share the same property (e.g., all are 4K, all have a French audio track).
    - Includes a **Lite Mode** for faster processing on large libraries by only checking the first episode of a series.
- **Extensive Icon Support**: Displays overlays for a wide range of media properties:
    - Audio Languages
    - Subtitle Languages
    - Audio Channels (e.g., Mono, Stereo, 5.1, 7.1)
    - Audio Codecs (e.g., DTS, TrueHD, AC3)
    - Video Codecs (e.g., H265, AV1, H264)
    - Video Format (HDR, HDR10+, Dolby Vision)
    - Video Resolution (e.g., 4K, 1080p, 720p)
    - **Custom Tags** (e.g., IMAX, 3D, VR)
    - Community Rating (IMDb)
- **Deep Customization**:
    - Independently set the on-screen corner alignment for each icon type (`TopLeft`, `TopRight`, etc.).
    - Choose between horizontal or vertical stacking for multiple icons in the same corner.
    - Adjust icon size as a percentage of the poster's height.
    - Control the final JPEG image quality to balance performance and visuals.
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

You must supply your own icon images and place them in a folder on your server. In the plugin settings, point the **Icons Folder** path to this folder. Name your image files according to the conventions below. A size of 100x100 pixels in `.png` format is recommended, but other formats like `.jpg` or `.webp` will also work.

### Icon Naming Convention

| Icon Type | Filename Format | Examples |
| :--- | :--- | :--- |
| **Audio Language** | `<lang_code>.png` | `eng.png`, `fre.png`, `jpn.png` |
| **Subtitle Language**| `srt.<lang_code>.png` | `srt.eng.png`, `srt.ger.png` |
| **Custom Tag** | `<tag_name>.png` | `IMAX.png`, `3D.png`, `VR.png` |
| **Audio Channel** | `<type>.png` | `5.1.png`, `7.1.png`, `stereo.png`, `mono.png` |
| **Audio Codec** | `<codec>.png` | `dts.png`, `truehd.png`, `ac3.png`, `eac3.png` |
| **Video Codec** | `<codec>.png` | `h265.png`, `av1.png`, `h264.png`, `vc1.png` |
| **Video Format** | `<format>.png` | `hdr.png`, `hdr10plus.png`, `dv.png` |
| **Resolution** | `<resolution>.png`| `4k.png`, `1080p.png`, `720p.png` |
| **Community Rating** | `imdb.png` |  `imdb.png` |

**Note on Custom Tags:** To use Custom Tag icons, you must first list the desired tags in the **Custom Tag Icons** field in the plugin's settings (e.g., `IMAX, 3D, VR`). Then, create an icon file in your icons folder that **exactly matches** the tag name. The filename match is case-insensitive.

### Configuration Options

| Setting | Description |
| :--- | :--- |
| **Icons Folder** | The full directory path to the folder containing your named icon images. |
| **Custom Tag Icons** | A comma-separated list of tags you want to display icons for (e.g., `IMAX, 3D`). |
| **Apply to these libraries** | Comma-separated list of library names to apply icons to. Leave blank for all libraries. |
| **Refresh icon cache...** | Check this box and save to force the plugin to reload all icons from the folder. Use this after adding or changing icons. |
| **Overlay Toggles** | Enable or disable each icon type (Audio, Subtitle, Codecs, etc.) individually. |
| **Alignment & Layout** | For each icon type, set the corner alignment and choose between horizontal or vertical stacking. |
| **Icon Size (%)** | Size of icons relative to the poster's height. Default is 10. |
| **Image Quality**| Set the output JPEG quality for the final image (10-100). Default is 75. |
| **Enable image smoothing**| Enable anti-aliasing for smoother icons, which can help on low-resolution posters. |
| **Use Lite Mode...** | If enabled, only checks the first episode of a series for its properties. Much faster but less accurate. |

---

## Troubleshooting

-   **Icons Not Appearing**:
    -   Double-check that the **Icons Folder** path in settings is correct and accessible by the Emby Server process.
    -   Ensure your icon files are named exactly according to the convention.
    -   For **Custom Tag** icons, make sure the tag is listed in the settings *and* the filename matches the tag on the media item.
    -   Ensure the media file actually contains the required metadata. For example, for a language icon to show, the audio or subtitle track must be correctly tagged with that language within Emby.
-   **Icons Not Updating**: If you change an icon file, add a tag to a movie, or change plugin settings, Emby's cache may still show the old image.
    1.  First, try using the **"Refresh icon cache on next save"** option in the plugin settings and click Save.
    2.  If that doesn't work, trigger a "Refresh Metadata" task on the specific library in Emby's Scheduled Tasks. Make sure to check "Replace existing images".
-   **TV Show Icons**: Remember that in the default (non-Lite) mode, an icon will only show on a series poster if **every single episode** has that property. If a series has 10 episodes in 4K and one in 1080p, the 4K icon will *not* be shown on the series poster.

---

-   Many thanks to Neminem, keitaro26, and Bakes82 in the Emby Forums for the help with this plugin!
-   A shoutout to Craggles for the awesome icons!

---

If you like the plugin and want to [Buy me a coffee](https://buymeacoffee.com/yockser)
