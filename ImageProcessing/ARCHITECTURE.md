# EmbyIcons Modular Architecture Diagram

## System Flow

```
┌─────────────────────────────────────────────────────────────┐
│                      Emby Server Startup                    │
└────────────────────────┬────────────────────────────────────┘
                         │
                         ▼
┌─────────────────────────────────────────────────────────────┐
│               EmbyIconsEnhancer Constructor                 │
│  - Initializes components                                   │
│  - Checks ImageProcessingCapabilities                       │
└────────────────────────┬────────────────────────────────────┘
                         │
                         ▼
┌─────────────────────────────────────────────────────────────┐
│         ImageProcessingCapabilities.IsSkiaSharpAvailable()  │
│  - Attempts to create SKBitmap(1,1)                         │
│  - Caches result                                            │
└─────────┬─────────────────────────────────┬─────────────────┘
          │                                 │
      ✅ Success                        ❌ Failure
          │                                 │
          ▼                                 ▼
┌──────────────────────┐        ┌──────────────────────────┐
│  SkiaSharp Available │        │ SkiaSharp Not Available  │
│  LOG: "Icon overlays │        │ LOG: "Icon overlays will │
│   will be applied"   │        │  be disabled"            │
└──────────┬───────────┘        └────────────┬─────────────┘
           │                                 │
           │                                 │
           └──────────────┬──────────────────┘
                          │
                          ▼
        ┌─────────────────────────────────────────┐
        │    Emby Calls: Supports(item, type)     │
        └──────────┬──────────────────────────────┘
                   │
                   ▼
        ┌─────────────────────────────────────────┐
        │  ImageProcessingCapabilities Check       │
        └─────┬───────────────────────┬────────────┘
              │                       │
          ✅ Available           ❌ Not Available
              │                       │
              ▼                       ▼
    ┌──────────────────┐    ┌─────────────────────┐
    │ return true      │    │ return false        │
    │ (Enable feature) │    │ (Disable feature)   │
    └────────┬─────────┘    └─────────────────────┘
             │
             ▼
    ┌──────────────────────────────────────────────┐
    │  EnhanceImageAsync() called                  │
    │  - Uses SkiaSharp directly (existing code)   │
    │  - Draws icons on posters                    │
    │  - Returns enhanced image                    │
    └──────────────────────────────────────────────┘
```

## Component Architecture

```
┌────────────────────────────────────────────────────────────┐
│                   IImageProcessor Interface                │
│  (Defines contract for image processing)                  │
└──────────────┬─────────────────────────────────────────────┘
               │
               │ Implemented by:
               │
    ┌──────────┴───────────┬─────────────────────────────┐
    │                      │                             │
    ▼                      ▼                             ▼
┌─────────────────┐  ┌──────────────────┐  ┌─────────────────────┐
│ SkiaSharp       │  │ EmbyNative       │  │ [Future: ImageSharp,│
│ Processor       │  │ Processor        │  │  System.Drawing,    │
│                 │  │                  │  │  NetVips, etc.]     │
│ • Full featured │  │ • Minimal/       │  │                     │
│ • All overlays  │  │   Passthrough    │  │                     │
│ • Preferred     │  │ • No crashes     │  │                     │
└─────────────────┘  └──────────────────┘  └─────────────────────┘
         ▲                    ▲
         │                    │
         └──────────┬─────────┘
                    │
         ┌──────────┴──────────────────────────────┐
         │   ImageProcessorFactory                 │
         │   - Tries processors in priority order  │
         │   - Returns first available             │
         │   - Caches result                       │
         └─────────────────────────────────────────┘
                          ▲
                          │
         ┌────────────────┴─────────────────────────┐
         │   ImageProcessingCapabilities            │
         │   - Checks SkiaSharp availability       │
         │   - Caches check result                 │
         │   - Used by EmbyIconsEnhancer.Supports() │
         └──────────────────────────────────────────┘
```

## Decision Tree: What Happens in Different Scenarios

### Scenario 1: SkiaSharp Installed and Working
```
Server Starts
   │
   ▼
Check: SkiaSharp available? ✅ YES
   │
   ▼
Supports() returns: TRUE
   │
   ▼
EnhanceImageAsync() called
   │
   ▼
Icons drawn on posters ✅
```

### Scenario 2: SkiaSharp Not Installed
```
Server Starts
   │
   ▼
Check: SkiaSharp available? ❌ NO
   │
   ▼
Log: "SkiaSharp not available, overlays disabled"
   │
   ▼
Supports() returns: FALSE
   │
   ▼
EnhanceImageAsync() NEVER CALLED
   │
   ▼
Original images used (no overlays)
   │
   ▼
Server continues normally ✅ (NO CRASH!)
```

### Scenario 3: SkiaSharp Crashes During Check
```
Server Starts
   │
   ▼
Try: Create test SKBitmap
   │
   ▼
Exception caught ❌
   │
   ▼
Log: "SkiaSharp not available: [error details]"
   │
   ▼
Mark as unavailable
   │
   ▼
Supports() returns: FALSE
   │
   ▼
Server continues normally ✅ (NO CRASH!)
```

## Key Safety Features

1. **Try-Catch Protection**: All SkiaSharp checks wrapped in exception handlers
2. **Early Return**: `Supports()` returns false immediately if SkiaSharp unavailable
3. **No Code Execution**: Enhancement code never runs without SkiaSharp
4. **Clear Logging**: User knows exactly what's happening
5. **Cached Results**: Availability check happens once, cached for performance
