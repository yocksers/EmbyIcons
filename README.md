![image](https://github.com/user-attachments/assets/90ed1d7b-e0ed-46c6-94f4-4ffdd47aa9db)

# EmbyIcons Plugin

---

## Overview

**EmbyIcons** is a plugin for [Emby Server](https://emby.media/) that overlays audio and subtitle language icons directly onto media posters. This provides a quick visual summary of available languages in your media, making browsing your library more informative and visually appealing.

---

## Features

- Show avilable audio and subtitle langguages on movie and tv show posters (Please read the troubleshooting on TV shows) 
- Supports detection of external subtitle files (`.srt`).
- Overlays language icons on posters with user-configurable size and alignment.
- Restricts icon overlay to selected libraries by name.
- Enables separate toggling of audio and subtitle icons.
- Configurable list of languages to detect and display icons for.
- Adjustable icon size as a percentage of the poster's shorter dimension.
- Plugin logging with configurable log folder location.
- Maps common language codes internally for consistent icon naming.

---

## Installation

1. Make sure you have the latest Emby version as this plugin might be incompatible with older and non x64/amd64 versions!
2. Place the EmbyIcons.dll into your Emby Server's plugin directory.
3. Restart Emby Server to load the plugin.
4. Open the Emby Server dashboard and navigate to **Plugins**.
5. Configure the plugin settings from the plugin page.

---

## Configuration Options

| Setting                      | Description                                                                                      |
|------------------------------|--------------------------------------------------------------------------------------------------|
| **Icons Folder Path**         | Directory containing language icon PNG files (e.g., `eng.png`, `srt.eng.png`). Note: Must supply own icons, i suggest 100x100 in size.|
| **Icon Size**                 | Size of icons as a percentage of the poster’s shorter side (e.g., 10%).                           |
| **Audio Icon Alignment**      | Corner where audio icons are displayed (`TopLeft`, `TopRight`, `BottomLeft`, `BottomRight`).      |
| **Subtitle Icon Alignment**   | Corner where subtitle icons are displayed.                                                        |
| **Audio Languages to Detect** | Comma-separated list of ISO language codes for audio streams to overlay icons for.                |
| **Subtitle Languages to Detect** | Comma-separated list of ISO language codes for subtitle streams.                               |
| **Show Audio Icons**          | Enable or disable audio language icon overlays.                                                   |
| **Show Subtitle Icons**       | Enable or disable subtitle icon overlays.                                                         |
| **Restrict to Libraries**     | Comma-separated list of library names to restrict icon overlays to; leave empty for all libraries.|
| **Enable Logging**            | Enable or disable plugin logging.                                                                 |

> **Important:**  
> _Icons must me named "eng.png" for audio tracks and "srt.eng.png" for subtitles, change eng with the language code you want like dan or jpn._

---

## Troubleshooting

- When chaging an icon for another of the same name sometimes a metadata refresh (with new images and/or server restart might be needed, not much i can do about that.
- For icons to show on TV show posters ALL episodes must contain the same language audio and/or subtitles.
- You need to supply your own icons. 
- Verify the icon folder path contains correctly named PNG icons for your languages.  
- Library names might need quotetation marks around them on some systems eg. "Movies". I'm looking into this.
- Ensure media files have proper language tags on audio and subtitle streams.  

---

- Many thanks to Neminem and keitaro26 Bakes82 in the Emby Forums for the help with this plugin!!
- A shoutout to Craggles for the awesome icons!!

---

If you like the plugin and want to [Buy me a coffee](https://www.paypal.com/donate/?hosted_button_id=KEXBXYM4KFPE8)
