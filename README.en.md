🌐 [Português](README.md) | [Español](README.es.md)

# 🎬 FilmesApi

[![.NET CI](https://github.com/DanielHoffmannO/FilmesApi/actions/workflows/dotnet.yml/badge.svg)](https://github.com/DanielHoffmannO/FilmesApi/actions)
![.NET](https://img.shields.io/badge/.NET-9.0-512BD4?logo=dotnet)
![SQLite](https://img.shields.io/badge/SQLite-003B57?logo=sqlite&logoColor=white)
![Docker](https://img.shields.io/badge/Docker-Ready-2496ED?logo=docker&logoColor=white)
![License](https://img.shields.io/badge/license-MIT-green)

> A personal streaming server for your movie and TV collection, running on your home network.
> Point it at a folder of videos, bring up the container, and watch from any device —
> phone, PC, a modern TV, or that old smart TV that can't open a normal website.

Built to run 24/7 on a mini-PC or an ARM board (Radxa / Rock Pi / Orange Pi), with
on-demand transcoding and hardware acceleration (RK3399/RK3588 VPU) when available.

---

## ✨ What it does

- **Automatic catalog** — scans the media folder, tells movies from series by filename,
  groups each series's episodes, ignores trailers/samples/extras.
- **Plays in any browser** — if the codec is already compatible, serves the file directly;
  otherwise transcodes to **on-demand HLS** (playback starts on the first segment).
- **Hardware acceleration** — uses `h264_rkmpp` (VPU) when the device is available, with
  automatic fallback to `libx264`. 4K input is downscaled to 1080p before re-encoding so the
  board doesn't overheat.
- **Resume playback** — remembers each movie's position and resumes without a jump.
- **Next episode** — at the end of an episode, offers the next one with a countdown.
- **Embedded subtitles** — extracts text tracks to WebVTT and serves them as `<track>` (native CC menu).
- **Old smart TV screen** (`tv.html`) — standalone catalog you navigate with the TV's own remote,
  pure ES5. Plays HLS when the TV supports it (MediaSource or native `<video>` HLS); on a TV
  with neither, still fixes incompatible audio via `/remux` before falling back to the raw file.
- **Phone remote control** (`controle.html`) — send a movie to the TV, control play/pause,
  seek, volume and subtitles from your phone, with a live progress bar.
- **Poster & synopsis** — optional enrichment via [TMDB](https://www.themoviedb.org/).
- **Three web UIs** (watch / TV / remote) + a status page. No app to install.

---

## 🚀 Running it

### Docker (recommended)

```bash
git clone https://github.com/DanielHoffmannO/FilmesApi.git
cd FilmesApi
mkdir -p media data
# drop your videos into ./media (subfolders are fine)
docker compose up -d --build
```

Open `http://SERVER-IP:8080` and click **📂 Scan Mídia**.

### Local (development)

```bash
dotnet run --project src/FilmesApi
# uses ./media and ./data in the current directory; needs ffmpeg/ffprobe on PATH
```

Requires the **.NET 9 SDK** and `ffmpeg`/`ffprobe`.

---

## 📁 Organizing your media

Classification is **entirely by filename** (counting files in a folder gets it wrong: a movie
with a trailer would look like a "series"):

| Kind | Detected by |
|---|---|
| **Series** | An episode marker in the name: `S01E01`, `1x01`, `Episode 5`, `Capítulo 03`. The series name is the containing folder (or the filename prefix before the marker). |
| **Movie** | Anything without an episode marker. A folder with 2+ files and no markers is treated as "movie + extras". |

Keep all episodes of a series **in one folder** — a folder per season makes each season a
separate "series" and next-episode won't cross the season boundary.

Recognized extensions: `.mp4 .mkv .avi .mov .wmv .flv .webm`

---

## 🖥️ The screens

| URL | Where | For |
|---|---|---|
| `/` (`index.html`) | phone / PC | Compact-list catalog, search, filters, resume, player with HLS, subtitles and next-episode. Old browsers fall back to `/tv.html`. |
| `/tv.html` | old smart TV | **Standalone** catalog navigable with the TV's own remote (ES5, arrows + OK). Plays HLS (MediaSource or native) when the TV supports it; otherwise tries `/remux` (fixed audio); the raw file is the last resort. Also listens for commands from `/controle.html` in the background. |
| `/controle.html` | phone | Remote control: movie list with a "send to TV" button, plus a fixed bar with play/pause, seek, volume, subtitles and progress for whatever's playing on the TV. |
| `/status.html` | — | Diagnostics: board temperature, transcode queue, HLS cache usage, VPU state. |
| `/swagger` | — | Interactive API docs. |

---

## 🎞️ How streaming works

On request, the server picks a path:

1. **Compatible** (`h264`/`vp9`/`av1` + `aac`/`mp3`/`opus` in `.mp4`/`.webm`/`.mov`/`.m4v`)
   → serves the file **directly** with `Range` support. No transcoding.
2. **Only the container is wrong** → **remux** (stream-copy) to HLS.
3. **The video codec must change** → **incremental re-encode** to HLS: tries `h264_rkmpp`,
   falls back to `libx264`; input above 1080p is downscaled first; 6s segments, playback
   starts on the first one.

Output is cached **permanently per movie** with LRU eviction above `HlsCacheMaxGB`. One
transcode at a time by default.

**`/remux` — not to be confused with the stream-copy remux from step 2 above** (that one's
internal to the HLS pipeline). **For a TV with no HLS at all** (no MediaSource, no native
`<video>` HLS): when the video is already
compatible but only the **audio** isn't (EAC3/DTS from WEB-DL/HMAX rips), `/remux` copies the
video without re-encoding and transcodes just the audio to stereo AAC into a cached `.mp4`
(`RemuxCacheMaxGB` cap), served with normal `Range` support — works on any player, unlike a live
pipe with no `Content-Length`. Falls back to the raw original only if the video itself is also
incompatible.

- **Audio track:** auto-picks Portuguese, then the default track, then the first.
- **Subtitles:** not muxed into HLS. Text tracks (SRT/ASS/mov_text) are extracted to WebVTT
  on demand and served as `<track>`. Bitmap subs (PGS/VobSub) can't be converted.
- **Protections:** stall detector (kills a stuck ffmpeg after 8 min), orphan cancellation
  (aborts a transcode nobody is watching after `HlsOrphanTimeoutSeconds`), optional thermal
  governor (holds new transcodes while the board is too hot).

---

## 📱 Remote control

`/controle.html` sends commands to the TV through `PlayerStateService` (a single in-memory
state, built for a one-TV household): select movie, play/pause, relative/absolute seek, volume,
subtitles. `tv.html` polls that state in the background and applies commands by calling the
**same** functions its own remote uses — no playback logic changes depending on who issued the
command. The TV also reports state when the movie is changed locally, so the phone reflects
what's playing regardless of where the command came from.

No brightness/zoom on purpose (an older version had them) — those would change what's on the
TV screen, out of scope for a remote control.

---

## ⚡ Hardware acceleration (VPU)

The `Dockerfile` bundles **[jellyfin-ffmpeg](https://github.com/jellyfin/jellyfin-ffmpeg)**
(`--enable-rkmpp`), runs as a non-root user in the `video` group, and points `FfmpegPath`/
`FfprobePath` at `/usr/lib/jellyfin-ffmpeg/`.

To use the RK3399/RK3588 VPU, pass the devices in `docker-compose.yml`:

```yaml
    devices:
      - /dev/mpp_service   # VPU — hardware encode/decode
      - /dev/rga           # 2D scaler
      - /dev/dri           # render nodes
```

The service runs a real probe at boot; if `h264_rkmpp` doesn't work on this kernel/board it
logs and uses `libx264` for the rest of the run.

> **RK3399:** hardware H.264 **encode** usually works; hardware **HEVC decode** needs the
> kernel to expose the `clk_hevc_cabac` clock for `rkvdec` — many kernels don't, so 4K HEVC
> decode stays in software. That's host infrastructure, not the app. `HlsRkmppDecodeHw`
> (default `false`) attempts hardware decode on the 4K path — only enable it after validating
> `scale_rkrga` + `h264_rkmpp` from the command line.

Without the devices (or off ARM), everything runs on `libx264`.

---

## ⚙️ Configuration

All via environment variables in `docker-compose.yml` (or `appsettings.json`). Nested keys
use `__`.

**Basics:** `MediaPath` (`/media`), `ConnectionStrings__Default`
(`Data Source=/data/filmes.db`), `HlsCachePath` (`/data/hls`), `SubtitleCachePath`
(`/data/subs`), `FfmpegPath` / `FfprobePath`.

**Transcoding:** `MaxConcurrentTranscodeJobs` (`1`), `HlsMaxAlturaReencode` (`1080`, `0`
disables downscale), `HlsCacheMaxGB` (`20`), `HlsOrphanTimeoutSeconds` (`90`, `0` never
aborts), `HlsStallTimeoutMinutes` (`8`), `ForceSoftwareEncoder` (`false`), `HlsRkmppDecodeHw`
(`false`), `RemuxCachePath` (`/data/remux`), `RemuxCacheMaxGB` (`15`).

**Thermal governor (opt-in):** `ThermalPauseCelsius` (`0` = off), `ThermalResumeCelsius`
(`pause − 8`), `ThermalMaxWaitMinutes` (`5`).

**Nightly pre-transcode (opt-in):** `PreTranscodeEnabled` (`false`), `PreTranscodeHoraUtc`
(`6`), `PreTranscodeMaxItens` (`5`).

**TMDB metadata (opt-in):** `TmdbApiKey` (empty = off), `TmdbLanguage` (`pt-BR`),
`TmdbImageBase`.

See the [Portuguese README](README.md) for the full config table with descriptions.

---

## 🔌 Endpoints

**Catalog:** `GET /api/filmes`, `GET|POST /api/filmes[/{id}]`, `PUT /api/filmes/{id}/assistido`,
`DELETE /api/filmes/{id}`, `POST /api/filmes/scan`, `GET /api/filmes/{id}/proximo`.

**Playback / progress:** `GET /api/filmes/continuar`,
`GET|PUT|DELETE /api/filmes/{id}/progresso`, `POST /api/filmes/{id}/concluir`,
`POST /api/filmes/{id}/assistindo`.

**Streaming:** `GET /api/filmes/{id}/stream-status`, `.../pode-direto` (now `{compativel,
remuxavel}`), `.../stream`, `.../remux-status`, `.../remux` (video-copy + AAC audio, cached,
`Range`-served), `.../original`, `.../hls/playlist.m3u8`, `.../hls/{seg}.ts`, `.../legendas`,
`.../legenda/{idx}`.

**Remote control** (single in-memory state, `PlayerStateService`): `GET /api/player/state`,
`POST .../selecionar/{filmeId}`, `.../play-pause`, `.../seek`, `.../seek-abs`, `.../volume`,
`.../legenda`, `.../posicao` (TV reports its own position), `.../parar`.

**Diagnostics:** `GET /api/status`, `POST /api/diag/log` (`tv.html` reports JS/hls.js/`<video>`
errors here — the TV has no accessible console). Full details at `/swagger`.

---

## 🏗️ Stack

.NET 9 / ASP.NET Core · EF Core 9 + SQLite (no migrations — `EnsureCreated()` + idempotent
raw-SQL guards at boot) · Swashbuckle · [hls.js](https://github.com/video-dev/hls.js)
(bundled) · Docker multi-stage (Alpine build, Debian bookworm-slim + jellyfin-ffmpeg runtime).

---

## 🔒 Security

**No authentication.** CORS is wide open. Built to live **on the LAN only**, behind your home
router. Don't expose port 8080 to the internet — use a VPN (Tailscale, WireGuard) or an
authenticating reverse proxy for remote access.

---

## 📄 License

[MIT](LICENSE).
