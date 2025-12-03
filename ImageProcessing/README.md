# EmbyIcons Image Processing Architecture

## Overview

EmbyIcons now uses a modular image processing architecture that supports different image processing backends. This ensures the plugin can work on Emby servers even if specific image libraries are not available.

## Supported Image Processors

### 1. SkiaSharp (Recommended)
- **Full featured** - Supports all icon overlay features
- **Cross-platform** - Works on Windows, Linux, macOS
- **Requirement**: SkiaSharp native libraries must be installed
- **Status**: Primary implementation

### 2. BasicFallback
- **Limited functionality** - Cannot apply icon overlays
- **Always available** - Uses only standard .NET libraries  
- **Purpose**: Prevents server crashes when SkiaSharp is unavailable
- **Status**: Fallback implementation

## How It Works

The plugin automatically detects which image processor is available at startup:

1. **Startup Check**: When EmbyIconsEnhancer initializes, it checks if SkiaSharp is available
2. **Graceful Degradation**: If SkiaSharp is not available:
   - The plugin loads successfully (no crash)
   - Icon overlay features are automatically disabled
   - A warning is logged explaining why overlays are disabled
3. **Full Functionality**: If SkiaSharp is available:
   - All icon overlay features work normally
   - Icons are drawn on posters as expected

## Architecture Components

### Core Interfaces
- `IImageProcessor`: Abstract interface for image processing operations
  - `DecodeImage()`: Load images from streams
  - `CreateBlankImage()`: Create new images
  - `DrawImage()`: Composite images together  
  - `DrawText()`: Render text on images
  - `EncodeImage()`: Save images to streams

### Implementations
- `SkiaSharpImageProcessor`: Full-featured implementation using SkiaSharp
- `EmbyNativeImageProcessor`: Minimal fallback implementation

### Helper Classes
- `ImageProcessingCapabilities`: Checks which processors are available
- `ImageProcessorFactory`: Creates the appropriate processor instance

## For Developers

### Adding a New Image Processor

To add support for a new image library:

1. Create a new class implementing `IImageProcessor`
2. Add it to `ImageProcessorFactory.GetImageProcessor()` in priority order
3. Implement all required methods for your library

Example:
```csharp
public class MyImageProcessor : IImageProcessor
{
    public string Name => "MyLibrary";
    public bool IsAvailable => CheckIfMyLibraryWorks();
    
    // Implement all interface methods...
}
```

### Why This Architecture?

The Emby developer noted: "Bear in mind this will likely cause servers that don't run Skia to crash. What's needed is a modular implementation that will vary depending on what image processor(s) the server has available."

This architecture solves that problem by:
- **Preventing crashes**: Plugin loads even without SkiaSharp
- **Graceful degradation**: Features disable cleanly when unavailable
- **Future extensibility**: Easy to add new image processors
- **Clear logging**: Users know exactly what's available and why

## Installation Requirements

### For Full Functionality
Ensure SkiaSharp is available on your Emby server:

**Windows**: Usually works out of the box  
**Linux**: May need `apt-get install libskiasharp` or similar  
**Docker**: Include SkiaSharp native libraries in your container

### Checking Status
Look for these log messages on startup:
- ✅ `"SkiaSharp is available. Icon overlays will be applied."`
- ⚠️ `"SkiaSharp is not available. Icon overlays will be disabled."`

## Troubleshooting

### "Icon overlays will be disabled" Warning

This means SkiaSharp is not available on your system. To fix:

1. Check that SkiaSharp NuGet package is included in the project
2. Ensure native SkiaSharp libraries are installed for your OS
3. On Linux, install required system libraries
4. Check Emby logs for specific error messages

### Server Crashes

If the server crashes on startup with EmbyIcons:
- This should no longer happen with the modular architecture
- If it does, please report it as a bug with full error logs
- The fallback processor should prevent all crashes

## Future Enhancements

Potential additional image processors:
- System.Drawing.Common (Windows-specific)
- ImageSharp (cross-platform alternative)
- NetVips (high performance option)
- Emby's built-in image processor (if API supports composition)
