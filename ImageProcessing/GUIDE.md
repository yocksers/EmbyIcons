# EmbyIcons - Image Processing Guide

## For Users

### What Changed?
EmbyIcons now has built-in protection against crashes on systems where SkiaSharp (the image processing library) is not available.

### What to Expect

#### If SkiaSharp is Available (Normal Case)
- ✅ Plugin works exactly as before
- ✅ All icon overlays appear on your posters
- ✅ No changes to functionality
- Log message: `"SkiaSharp is available. Icon overlays will be applied."`

#### If SkiaSharp is Not Available
- ℹ️ Plugin loads successfully (server doesn't crash!)
- ℹ️ Icon overlays are automatically disabled
- ℹ️ Original posters are used without modifications
- ⚠️ Log message: `"SkiaSharp is not available. Icon overlays will be disabled."`

### How to Enable Full Functionality

If you see the "not available" warning, you can enable icon overlays by:

1. **Windows**: Usually works automatically - try reinstalling the plugin
2. **Linux**: Install SkiaSharp dependencies
   ```bash
   # Debian/Ubuntu
   sudo apt-get install libfontconfig1
   
   # Or for fuller support:
   sudo apt-get install libskiasharp libfontconfig1
   ```
3. **Docker**: Ensure your container includes SkiaSharp libraries
   ```dockerfile
   RUN apt-get update && apt-get install -y libfontconfig1
   ```
4. **Restart Emby** after installing dependencies

### Testing the Fallback Behavior

You can test how the plugin behaves without SkiaSharp by using the built-in toggle:

1. Go to **Plugin Settings → Advanced**
2. Scroll to **Testing & Diagnostics**
3. Check **"Force Disable SkiaSharp"**
4. Click **Save**
5. Restart Emby server

This will force the plugin to use the fallback processor even if SkiaSharp is available, allowing you to verify that:
- The server doesn't crash
- Icon overlays are properly disabled
- The plugin logs appropriate warnings

**Important:** Uncheck this option for normal operation!

### Troubleshooting

**Q: Why aren't my icon overlays showing?**  
A: Check your Emby logs for "SkiaSharp is not available" - if present, follow the installation steps above.

**Q: Will this crash my server?**  
A: No! The new modular architecture specifically prevents crashes. If SkiaSharp isn't available, overlays are simply disabled.

**Q: Do I need to change any settings?**  
A: No, the plugin automatically detects what's available and adjusts accordingly.

---

## For Developers

### Architecture Overview

The plugin now uses a modular image processing system with these components:

```
ImageProcessingCapabilities  ← Checks what's available
         │
         ├─→ SkiaSharpImageProcessor (preferred)
         │
         └─→ EmbyNativeImageProcessor (fallback)
```

### Key Classes

#### `ImageProcessingCapabilities`
Static class that checks if SkiaSharp is available:
```csharp
bool available = ImageProcessingCapabilities.IsSkiaSharpAvailable(logger);
```

#### `IImageProcessor` Interface
Abstraction for image processing operations:
```csharp
public interface IImageProcessor
{
    string Name { get; }
    bool IsAvailable { get; }
    object DecodeImage(Stream inputStream);
    void DrawImage(object target, object source, int x, int y, int w, int h, bool smooth);
    void EncodeImage(object image, Stream output, string format, int quality);
    // ... more methods
}
```

#### `SkiaSharpImageProcessor`
Full-featured implementation using SkiaSharp. Used when available.

#### `EmbyNativeImageProcessor`
Minimal fallback that prevents crashes but can't apply overlays.

### Integration in EmbyIconsEnhancer

The check happens in the `Supports()` method:

```csharp
public bool Supports(BaseItem? item, ImageType imageType)
{
    if (item == null) return false;
    
    // NEW: Check if SkiaSharp is available
    if (!ImageProcessingCapabilities.IsSkiaSharpAvailable(_logger))
    {
        return false; // Disable feature gracefully
    }
    
    // ... rest of existing logic
}
```

### Adding a New Image Processor

To add support for another image library (e.g., ImageSharp, System.Drawing):

1. **Create Implementation**:
```csharp
public class MyImageProcessor : IImageProcessor
{
    public string Name => "MyLibrary";
    
    public bool IsAvailable
    {
        get
        {
            try
            {
                // Test if your library works
                return TestMyLibrary();
            }
            catch
            {
                return false;
            }
        }
    }
    
    public object DecodeImage(Stream inputStream)
    {
        // Use your library to decode
        return MyLibrary.Load(inputStream);
    }
    
    // Implement other interface methods...
}
```

2. **Add to Factory** (in `ImageProcessorFactory.cs`):
```csharp
var processors = new List<Func<IImageProcessor>>
{
    () => new SkiaSharpImageProcessor(logger),      // Try SkiaSharp first
    () => new MyImageProcessor(logger),             // Try your processor
    () => new EmbyNativeImageProcessor(logger)      // Fallback
};
```

### Testing

#### Test 1: With SkiaSharp
```csharp
// Should log: "SkiaSharp is available"
// Should apply overlays normally
```

#### Test 2: Without SkiaSharp
```csharp
// Simulate: Remove SkiaSharp DLLs
// Should log: "SkiaSharp is not available"
// Should NOT crash
// Should disable overlays
```

#### Test 3: SkiaSharp Throws Exception
```csharp
// Should catch exception
// Should log error details
// Should fall back gracefully
// Should NOT crash
```

### Performance Notes

- Availability check is cached after first run
- Check happens once per server session
- No performance impact on normal operation
- Factory pattern allows future optimizations

### Why This Design?

Per Emby developer feedback:
> "Bear in mind this will likely cause servers that don't run Skia to crash. What's needed is a modular implementation that will vary depending on what image processor(s) the server has available."

This architecture:
- ✅ Prevents crashes (try-catch + early return)
- ✅ Is modular (interface + multiple implementations)
- ✅ Varies by availability (capability detection + factory)
- ✅ Provides clear diagnostics (comprehensive logging)
- ✅ Is extensible (easy to add new processors)

### Documentation

- `README.md` - Overview of image processing system
- `ARCHITECTURE.md` - Visual diagrams and flow charts
- `MODULAR_IMPLEMENTATION_SUMMARY.md` - Complete implementation details
- This file - Quick reference guide

### Questions?

Check the logs! The plugin provides clear diagnostic information:
- What processor is being used
- Why certain processors aren't available
- What features are enabled/disabled
