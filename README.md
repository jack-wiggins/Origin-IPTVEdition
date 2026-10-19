# Origin

Origin is a 10-foot home theater PC (HTPC) application for Windows, in the spirit of Windows Media Center and Microsoft Mediaroom. It plays live TV from an **Xtream Codes** IPTV provider and lines its channels up with **Schedules Direct** guide listings.

It is written in C# and WPF on .NET Framework 4.8, so it runs on **Windows 7 SP1** through Windows 11. The animated background is drawn with **Direct3D 9Ex**. Every other animation in the interface (page transitions, the start menu, focus glows) is composited on the GPU by WPF, which itself renders through DirectX.

## What works today

| Area | Status |
| --- | --- |
| Start menu | Media Center-style category strip (tv · settings · tasks) with eased, GPU-composited animations over a Direct3D ribbon background |
| IPTV provider | Server URL + username + password (or paste a full `get.php` link). Test connection shows account status, expiry and connections |
| Playlist | Downloads the M3U Plus playlist (`get.php?type=m3u_plus`), parses it as a stream and splits live TV from movies and series |
| Schedules Direct | Sign in, list and remove the lineups on your account, search by country and postal code, add a lineup, choose the one Origin uses |
| Lineup | Copies the M3U's live channels into Origin's lineup and matches each one to a station in the Schedules Direct lineup |
| Channel mapping | Shows every channel, its matched station and how it was matched. Change, clear or hide any channel; your choices survive updates |
| Channel groups | Choose which playlist groups become channels; number channels from the playlist or from Schedules Direct |
| Guide listings | Two weeks of schedules and program details from Schedules Direct, downloaded with the lineup. Unchanged days and programs are skipped by md5, and everything is cached for offline use |
| Guide | 90-minute grid of program blocks sized by duration. Left/Right move through time, Up/Down through channels; the details panel shows title, episode, description and air time |
| Live TV | Full-screen playback through LibVLC (MPEG-TS and HLS, DXVA2 hardware decoding), channel banner with what's on now and next, ch+/ch−, number entry, auto-reconnect |

**Not built yet:** recording, reminders, movies and series libraries.

## How the M3U becomes a Schedules Direct lineup

`Tasks › update lineup` runs [`LineupService.UpdateAsync`](src/Origin.Core/Lineup/LineupService.cs):

1. **Download.** Builds `{server}/get.php?username=…&password=…&type=m3u_plus&output=ts|m3u8` from the saved account and parses it as it streams in ([`M3uParser`](src/Origin.Core/Iptv/M3uParser.cs)).
2. **Strip credentials.** Xtream stream URLs (`/live/{user}/{pass}/{id}.ts`) are stored as parts and rebuilt at play time ([`PlaylistImporter`](src/Origin.Core/Iptv/PlaylistImporter.cs), [`StreamReference`](src/Origin.Core/Iptv/StreamReference.cs)). No password is ever written to `playlist.json` or `lineup.json`.
3. **Fetch the lineup.** `GET /lineups/{id}` from Schedules Direct gives the channel map and the station list ([`SdClient`](src/Origin.Core/SchedulesDirect/SdClient.cs)). A copy is cached, so the lineup can be rebuilt when Schedules Direct is unreachable.
4. **Copy and match.** Every live channel in the selected groups is copied in playlist order, then [`ChannelMatcher`](src/Origin.Core/Lineup/ChannelMatcher.cs) tries, in order:
   - `tvg-id` equals a Schedules Direct station ID
   - `tvg-id` carries a callsign or name (`WABC.us`, `ESPN.us`)
   - a broadcast callsign in the channel name (`CBS 2 (WCBS) HD`)
   - the cleaned-up name equals a station name or callsign (`US: ESPN FHD` → `espn`)
   - a fuzzy name comparison that refuses to confuse `Fox Sports 1` with `Fox Sports 2`, or East feeds with West
5. **Number and save.** Each channel takes its `tvg-chno` (or its Schedules Direct channel number, if you choose that), and the rest are numbered in playlist order. Hidden channels and hand-picked matches carry over from the previous lineup.
6. **Download the guide.** For every matched station, [`GuideDownloader`](src/Origin.Core/Guide/GuideDownloader.cs) asks `POST /schedules/md5` for yesterday plus the next 14 days, downloads only the days whose md5 changed (`POST /schedules`), then the details of programs that are new or changed (`POST /programs`, 5,000 at a time). [`GuideStore`](src/Origin.Core/Guide/GuideStore.cs) keeps them in `cache\guide` and answers "what's on station X between two times". If Schedules Direct is down, the update still succeeds and the guide keeps its last listings.

## Remote control and keyboard

Origin follows the Windows Media Center keyboard shortcuts that eHome/MCE remotes send, and it also handles `WM_APPCOMMAND` media keys.

| Action | Keys |
| --- | --- |
| Move / select | Arrow keys, Enter |
| Back | Backspace, Esc, remote Back, mouse back button |
| Guide | Ctrl+G (or G) |
| Live TV | Ctrl+T |
| Info / banner | Ctrl+D (or I) |
| Channel up / down | Page Up / Page Down, remote CH+/CH− |
| Play / pause / stop | Ctrl+Shift+P / Ctrl+P / Ctrl+Shift+S, media keys |
| Go to channel | 0–9 |
| Full screen | Alt+Enter or F11 |

Moving the mouse shows Back, Start, Minimize, Full screen and Close buttons, as Media Center did.

## Building

You need the .NET SDK (any recent version) on Windows, with the .NET Framework 4.8 targeting pack (included with Visual Studio's .NET desktop workload).

```bash
dotnet build Origin.sln -c Release
```

```bash
dotnet test tests/Origin.Core.Tests/Origin.Core.Tests.csproj
```

The app is `src/Origin.App/bin/Release/net48/OriginHTPC.exe`. It starts full screen. Pass `--windowed` to start in a window. LibVLC for both x86 and x64 is copied into `libvlc\` beside the executable.

Settings, the lineup and logs live in `%LOCALAPPDATA%\OriginHTPC`. Passwords are encrypted with DPAPI for the current Windows user. For Schedules Direct, only the SHA-1 hash the API requires is kept.

## Windows 7 notes

- Install the [.NET Framework 4.8 runtime](https://dotnet.microsoft.com/download/dotnet-framework/net48). It is the newest .NET that supports Windows 7 SP1.
- Origin turns on TLS 1.2 itself, which Schedules Direct requires and which Windows 7 does not offer .NET applications by default.
- The DirectX background needs a WDDM display driver (Direct3D 9Ex). Without one, or over some Remote Desktop sessions, Origin falls back to a static gradient.
- Fonts are Segoe UI, Segoe UI Light and Segoe UI Semibold, which all ship with Windows 7. Icons are vector paths, so no newer icon fonts are needed.

## Project layout

```
src/Origin.Core      Network, parsing, Schedules Direct, matching and storage (no UI)
  Iptv/              M3U parser, playlist import, credential-free stream references
  Xtream/            Xtream Codes URLs and client (player_api.php, get.php)
  SchedulesDirect/   JSON API client (20141201): sign-in, lineups, schedules, programs
  Lineup/            Channel matcher, lineup composer, store and update service
  Guide/             Schedule and program download, on-disk guide cache, grid layout
src/Origin.App       WPF application (OriginHTPC.exe)
  Rendering/         Direct3D 9Ex ribbon background hosted in a D3DImage
  Input/             Keyboard, MCE remote and WM_APPCOMMAND mapping
  Playback/          LibVLC playback service
  Views/             Start, Guide, Live TV, settings pages
tests/Origin.Core.Tests   Unit tests and end-to-end tests against a local mock server
```

## License

Copyright (C) 2026 Jack Wiggins

Origin is free and open source software, released under the [GNU Lesser General Public License v3.0](COPYING.LESSER) (or, at your option, any later version). The LGPL builds on the [GNU General Public License v3.0](COPYING), which is included as well.

- **Free, forever.** No subscriptions, no pay wall, no paid tiers.
- **Copy and redistribute it.** Share Origin, fork it, or ship your own builds.
- **Build plug-ins.** Plug-ins that use Origin can be released under any license you choose, open or closed.
- **Keep Origin open.** If you distribute a modified version of Origin itself, you must share the source of those changes under the LGPL too.

Origin is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the licenses for details.
