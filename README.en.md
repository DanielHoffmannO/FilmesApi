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
- **Control the TV from your phone** — the TV opens a "dumb" page (`tv.html`); the phone is the remote.
- **Poster & synopsis** — optional enrichment via [TMDB](https://www.themoviedb.org/).
- **Three web UIs** + a status page. No app to install.

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

| URL | For |
|---|---|
| `/` (`index.html`) | Main UI — catalog with posters, filters, collapsible series, resume, player with subtitles and next-episode, plus a floating bar to control the TV. |
| `/feia.html` | Minimal UI for **old smart TVs** (ES5, no flexbox/grid, D-pad navigation). |
| `/tv.html` | What runs **on the TV** — no catalog, driven by the phone via `/api/player/*`. |
| `/status.html` | Diagnostics: board temperature, transcode queue, HLS cache usage, VPU state. |
| `/swagger` | Interactive API docs. |

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

- **Audio track:** auto-picks Portuguese, then the default track, then the first.
- **Subtitles:** not muxed into HLS. Text tracks (SRT/ASS/mov_text) are extracted to WebVTT
  on demand and served as `<track>`. Bitmap subs (PGS/VobSub) can't be converted.
- **Protections:** stall detector (kills a stuck ffmpeg after 8 min), orphan cancellation
  (aborts a transcode nobody is watching after `HlsOrphanTimeoutSeconds`), optional thermal
  governor (holds new transcodes while the board is too hot).

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
(`false`).

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

**Streaming:** `GET /api/filmes/{id}/stream-status`, `.../pode-direto`, `.../stream`,
`.../original`, `.../hls/playlist.m3u8`, `.../hls/{seg}.ts`, `.../legendas`,
`.../legenda/{idx}`.

**TV remote (`/api/player`):** `GET /state`, `POST /selecionar/{id}`, `/play-pause`,
`/parar`, `/seek`, `/seek-abs`, `/volume`, `/legenda`, `/posicao`.

**Diagnostics:** `GET /api/status`. Full details at `/swagger`.

---

## 🏗️ Stack

.NET 9 / ASP.NET Core · EF Core 8 + SQLite (no migrations — `EnsureCreated()` + idempotent
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
