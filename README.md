![image](https://github.com/user-attachments/assets/90ed1d7b-e0ed-46c6-94f4-4ffdd47aa9db)

EmbyIcons Plugin
Overview:
EmbyIcons is an Emby Server plugin that overlays custom audio and subtitle language icons onto media posters. It provides a quick visual summary of the languages available in your media files, directly on the poster images.

Important note:
Icons must be named eng.png for audio and srt.eng.png for subtitles, replace eng with your language code!
Sadly for now a server restart is needed for new settings to work.

Configuration Options
Icons Folder Path: Directory containing language icon PNG files named like eng.png for audio or srt.eng.png for subtitles.
Icon Size: Percentage size of icons relative to the poster's shorter side (e.g., 10%).
Audio Icon Alignment: Position where audio icons appear (TopLeft, TopRight, BottomLeft, BottomRight).
Subtitle Icon Alignment: Position where subtitle icons appear.
Audio Languages to Detect: Comma-separated ISO language codes for audio streams to overlay icons.
Subtitle Languages to Detect: Comma-separated ISO language codes for subtitles.
Show Audio Icons: Enable or disable audio language icons.
Show Subtitle Icons: Enable or disable subtitle language icons.
Restrict to Libraries: Comma-separated list of library names to apply overlays; leave empty for all libraries.
Enable Logging: Toggle plugin logging.
Log Folder Path: Location to save plugin logs.
Important:
Please restart the Emby Server after changing plugin settings for changes to take effect!

Installatio
Copy the EmbyIcons.dll Emby Server plugin directory.
Restart Emby Server.
Open Emby Server dashboard and navigate to Plugins.
Open the plugin settings page to configure options (reset server after configurating settings)

Troubleshooting
If icons do not appear, verify the icon folder path and that icons named appropriately exist.
Check plugin logs (EmbyIcons.log) for errors or status messages.
Ensure your media files have audio/subtitle streams with correct language tags.
Remember to restart Emby Server after changing plugin settings.

License
This plugin is provided as-is without warranty. Use at your own risk.
