# Jellyfin AniSkip Plugin

A Jellyfin **media segment provider** that pulls crowd-sourced **anime opening,
ending and recap** timestamps from the [AniSkip](https://www.aniskip.com/) database
and publishes them through Jellyfin's Media Segments API — so **any** client with
skip-segment support (web, mobile, TV, third-party apps) can skip them.

It fills the gap that audio fingerprinting (Intro Skipper) structurally cannot:

- **Recaps** ("previously on…") differ every episode and share no repeating audio,
  so fingerprinting can never match them. A crowd-sourced database can.
- Anime **openings move around** (cold opens push the OP to 1:00, 2:20, …).
  Human-submitted timestamps handle this cleanly where fingerprinting drifts.

## How it works

```
Episode ──▶ AniDB / AniList / MAL id ──▶ arm.haglund.dev ──▶ MyAnimeList id
                                                                   │
                                    episode number + runtime ──────┤
                                                                   ▼
                                              api.aniskip.com/v2/skip-times
                                                                   │
        op → Intro   ed → Outro   recap → Recap                    ▼
        mixed-op → Intro   mixed-ed → Outro   ────▶  Jellyfin MediaSegments
```

AniSkip is keyed on **MyAnimeList** ids. Jellyfin anime libraries usually carry
**AniDB** (and sometimes **AniList**) ids instead, so those are mapped to MAL via the
[arm](https://arm.haglund.dev) service. Results (including the MAL lookup) are cached
in-process, so a full-library scan hits each external service at most once per id.

## Category mapping

| AniSkip type | Jellyfin segment |
|--------------|------------------|
| `op`         | Intro            |
| `ed`         | Outro            |
| `recap`      | Recap            |
| `mixed-op`   | Intro            |
| `mixed-ed`   | Outro            |

`mixed-op` / `mixed-ed` cover openings/endings fused with story content — the case
where a recap or cold-open is blended into the opening.

## Requirements

- Jellyfin **10.11+** (uses the Media Segments API, targetAbi `10.11.0.0`).
- Your anime must have an **AniDB, AniList or MyAnimeList** id (from the AniDB /
  AniList metadata plugins). TVDB/TMDb-only items are ignored.

## Install

Manual (no plugin catalog yet):

1. Download `Jellyfin.Plugin.AniSkip.dll` from a release.
2. Drop it into a new folder in your Jellyfin `plugins` directory, e.g.
   `plugins/AniSkip_1.0.0.0/`.
3. Restart Jellyfin.
4. Run **Dashboard → Scheduled Tasks → "Detect and Analyze Media Segments"** to
   populate segments (also runs automatically on library scans).

## Configuration

Dashboard → Plugins → AniSkip:

- Toggle **Intro / Outro / Recap / Mixed** individually.
- **Minimum episode length** guard (default 60 s) for shorts/specials.
- **Apply a series-level MAL id to seasons > 1** — off by default. Multi-cour anime
  map each season to a *distinct* MAL entry; a series-level id only describes the
  first cour, so it is not applied to later seasons unless you opt in.

## Known limitations

- **Multi-cour numbering.** AniSkip numbers episodes per MAL entry (each cour restarts
  at 1). When only a *series-level* id is present, later seasons are skipped rather
  than risk placing skips at the wrong timestamps. Season-level AniDB/AniList/MAL ids
  are used when available and resolve each cour correctly.
- **Absolute numbering.** Libraries that number a whole show absolutely (1..N across
  cours) will not line up with per-cour AniSkip numbering.
- **Coverage is crowd-sourced.** Popular shows are well covered; brand-new episodes
  may lag a few days.

## Build

```sh
dotnet build Jellyfin.Plugin.AniSkip/Jellyfin.Plugin.AniSkip.csproj -c Release
```

The plugin DLL lands in `Jellyfin.Plugin.AniSkip/bin/Release/net9.0/`.

## Credits

- [AniSkip](https://github.com/aniskip) — the crowd-sourced timestamp database & API.
- [arm](https://github.com/BeeeQueue/arm-server) — anime id relations mapper.

Not affiliated with either project.

## License

MIT
