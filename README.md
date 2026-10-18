![image](https://github.com/user-attachments/assets/08e70e95-d4fd-45be-b206-1d0b0922fbe1)

# EmbyIcons Plugin

[![Made for Emby](https://img.shields.io/badge/made%20for-emby-00a4dc.svg)](https://emby.media/)
[![BuyMeACoffee](https://img.shields.io/badge/support-buy%20me%20a%20coffee-yellow.svg)](https://buymeacoffee.com/yockser)

EmbyIcons enhances your Emby Server by overlaying informational icons directly onto media posters. Display language, codecs, resolution, ratings, and more at a glance with fully customizable profiles for different libraries.

---

## Features
*   **Extensive Icon Support**: Audio/subtitle languages, original language, audio channels, codecs (audio & video), HDR/Dolby Vision, resolution, frame rate, aspect ratio, parental ratings, community ratings (IMDb, Rotten Tomatoes, MDBList audience scores), custom tags, and favorite counts
*   **Multi-Profile System**: Create distinct profiles and assign them to different libraries for complete customization
*   **Flexible Layout Control**: Position icons in any corner with custom priority ordering and horizontal/vertical stacking
*   **Series & Collection Aggregation**: Smart detection shows icons only when all episodes/items share the same property (with Lite Mode option for faster scans)
*   **Profile Import/Export**: Backup and share your profile configurations
*   **Icon Manager**: Identify used, unused, and missing icons in your collection
*   **Series Troubleshooter**: Find episodes with inconsistent properties causing missing icons
*   **MDBList Integration**: Display Rotten Tomatoes audience scores with a free API key
*   **Live Preview**: See layout changes in real-time before saving
*   **Advanced Settings**: Fine-tune cache sizes, performance, and memory usage

---

## Installation
1.  Go to the catalog in Emby and find EmbyIcons under general.
2.  Press install.

---

## Icon Naming Convention
Place your icon images in a folder on your server. Recommended: ~100x100px PNG with transparency. Supported formats: `.png`, `.jpg`, `.webp`, `.gif`, `.bmp`.

Icons use the format: **`prefix.name.png`** (case-insensitive)

| Prefix | Type | Example |
|--------|------|---------|
| `lang` | Audio Language | `lang.english.png` |
| `sub` | Subtitle Language | `sub.german.png` |
| `og` | Original Language | `og.japanese.png` |
| `ch` | Audio Channels | `ch.7.1.png`, `ch.stereo.png` |
| `ac` | Audio Codec | `ac.dts.png`, `ac.eac3.png` |
| `vc` | Video Codec | `vc.hevc.png`, `vc.av1.png` |
| `hdr` | Video Format | `hdr.dv.png`, `hdr.hdr.png` |
| `res` | Resolution | `res.4k.png`, `res.1080p.png` |
| `fps` | Frame Rate | `fps.24.png`, `fps.60.png` |
| `ar` | Aspect Ratio | `ar.16x9.png`, `ar.2.39x1.png` |
| `pr` | Parental Rating | `pr.pg-13.png`, `pr.tv-ma.png` |
| `tag` | Custom Tag | `tag.3d.png`, `tag.directors-cut.png` |
| `rating` | Community Rating | `rating.imdb.png` |
| (none) | Rotten Tomatoes | `t.tomato.png`, `t.splat.png` |
| (none) | Popcorn-O-Meter | `t.popcorn.png`, `t.spilledpopcorn.png`, `t.fresh.png` |
| (none) | Favorite Count | `heart.png` |

**Note**: Rotten Tomatoes Popcorn-O-Meter requires a free [MDBList API key](https://mdblist.com) configured in Advanced Settings.

---

## Configuration Overview

### Settings Tab
*   **Global Settings**: Set icon folder path, loading mode (Hybrid recommended), output format, quality, and logging
*   **Profile Management**: Create, rename, delete, export, and import profiles
*   **Per-Profile Settings**:
    *   Assign profiles to libraries
    *   Enable/disable icon types with custom alignment (corner), priority, and layout (horizontal/vertical)
    *   Configure TV show/collection aggregation (Lite Mode, exclude Specials season, etc.)
    *   Customize rating score background (shape, color, opacity)
    *   Adjust icon size

### Icon Manager Tab
Scan your library to identify **missing**, **found**, and **unused** icons. Perfect for organizing your icon collection.

### Troubleshooter Tab
*   **Series Troubleshooter**: Find episodes with inconsistent properties (resolution, codec, etc.) that prevent series-level icons
*   **Aspect Ratio Calculator**: Get the exact icon filename for custom aspect ratios
*   **Memory Usage**: View plugin memory statistics

### Advanced Tab
Fine-tune cache sizes, expiration times, concurrency, and MDBList API key. Default values work for most users.

---

## Image tags and rendering

Media-list image tags use cached profile information, precomputed settings keys, and input revisions. They do not query episodes, read media streams, scan icon directories, or fetch MDBList ratings. Actual image requests and the pre-draw task share the normal rendering path; parent-only overlays such as ratings and parental ratings skip child aggregation there as well.

Image identities and configuration bindings are saved in `EmbyIcons/image-state-v2/` under Emby's data directory. Internal Emby IDs identify items even when no TMDb or other provider ID exists. Learning a profile after startup does not change an already-issued image key. Assembly version, cache sizes, logging, profile names, and the disk-summary toggle do not invalidate enhanced images. Explicit manual refresh and actual drawing-setting changes still do.

Only identities that participate in image caching are tracked; there is no whole-library scan or 500,000-item cutoff. A background worker reads tracked metadata in batches of 500 and checks relevant relationships. It compares enabled overlay inputs before changing an image revision, so timestamp-only metadata saves need not redraw posters. Collection membership events and reverse dependency indexes target affected images. Where precise relationship events are unavailable, coalesced checks inspect known collection/music parents. List requests do not wait for this verification, so an existing overlay can remain visible briefly while changed inputs are checked. Actual drawing and stream inspection share the renderer's concurrency limit.

State is stored in checksummed, atomically replaced partitions with backup recovery. Corruption invalidates only the affected partition. Existing `ei11` keys for ordinary resolved-profile overlays are retained where their inputs can be proved compatible; earlier session-dependent collection/aggregation keys or unverifiable state can require one initial redraw. MDBList image keys retain per-work seven-day refresh intervals.

The optional **Keep TV show, collection and album summaries on disk** setting saves aggregation results separately in `plugins/embyicons/summaries-tv.json` and `summaries-music.json`. These summaries still validate child membership and metadata before reuse. Their compatibility version is independent of the DLL version; unchanged summaries written by 5.61.1.5 and 5.61.1.6 can be migrated after validation. The first rendering lookup waits for the initial disk load rather than racing it and unnecessarily aggregating again. Keeping summaries does not by itself preserve Emby's enhanced-image cache tags.

Movie filename overlays use Emby's saved alternate-version relationship and a namespaced TMDb, IMDb, Douban, JavDB, or JavBus identifier when available. Without either relationship, a movie uses only its own path; matching titles or catalogue numbers never merge unrelated items. The renderer, previews, troubleshooting tools, and pre-draw task retain their existing roles. First-time rendering still performs the required data and drawing work.

To run the offline regression suite with the .NET 8 SDK:

```sh
dotnet test Tests/EmbyIcons.Tests.csproj --configuration Release
```

## Acknowledgements
Many thanks to **Neminem**, **keitaro26**, **Bakes82**, and everyone from the Emby Community forum. Shoutout to **Craggles** for the default icons.

If you enjoy this plugin, consider supporting development:

<a href="https://buymeacoffee.com/yockser" target="_blank"><img src="https://cdn.buymeacoffee.com/buttons/v2/default-yellow.png" alt="Buy Me A Coffee" style="height: 60px !important;width: 217px !important;" ></a>
