# ETK MediaInfo Bridge

An Emby 4.9.x plugin that imports formatted media information from
[Emby ToolKit](https://github.com/hbq0405/emby-toolkit) without running Emby's
remote probe or generating `*-mediainfo.json` sidecar files.

## Features

- Persists ETK-formatted media streams and chapters into an exact Emby Item.
- Preserves external subtitle streams already detected by Emby.
- Restores ETK media information automatically after an Emby item refresh.
- Resolves both regular 115 play URLs and ETK virtual-play SHA1 URLs.
- Debounces repeated item events and limits restore requests to four at a time.

## Install

1. Download `ETKMediaInfoBridge.dll` from the
   [latest release](https://github.com/hbq0405/etk-mediainfo-bridge/releases/latest).
2. Place the DLL in Emby's plugin directory.
3. Restart Emby.

Emby must be able to reach the ETK URL stored in each STRM file. The matching
ETK backend endpoints are included in the ETK `dev` branch.

## API

The authenticated endpoint accepts the normalized object stored in
`p115_mediainfo_cache.mediainfo_json`:

```http
POST /Items/{Id}/ETKMediaInfo
Content-Type: application/json
X-Emby-Token: <admin-api-key>

{
  "MediaSourceInfo": { "MediaStreams": [] },
  "Chapters": []
}
```

Each request replaces embedded streams for that exact Emby Item ID while
preserving external streams. Repeating the same request is idempotent.

## Build

Install the .NET 8 SDK, then run:

```bash
dotnet build -c Release
```

The output is `bin/Release/netstandard2.0/ETKMediaInfoBridge.dll`.
