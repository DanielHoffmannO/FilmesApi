🌐 [Português](README.md) | [English](README.en.md)

# 🎬 FilmesApi

[![.NET CI](https://github.com/DanielHoffmannO/FilmesApi/actions/workflows/dotnet.yml/badge.svg)](https://github.com/DanielHoffmannO/FilmesApi/actions)
![.NET](https://img.shields.io/badge/.NET-9.0-512BD4?logo=dotnet)
![SQLite](https://img.shields.io/badge/SQLite-003B57?logo=sqlite&logoColor=white)
![Docker](https://img.shields.io/badge/Docker-Ready-2496ED?logo=docker&logoColor=white)
![License](https://img.shields.io/badge/license-MIT-green)

> Un servidor personal de streaming para tu colección de películas y series, en la red de casa.
> Apúntalo a una carpeta con tus videos, levanta el contenedor y míralo desde cualquier
> dispositivo — celular, PC, una TV moderna o esa smart TV vieja que no abre ningún sitio web.

Pensado para correr 24/7 en un mini-PC o una placa ARM (Radxa / Rock Pi / Orange Pi), con
transcodificación bajo demanda y aceleración por hardware (VPU del RK3399/RK3588) cuando existe.

---

## ✨ Qué hace

- **Catálogo automático** — recorre la carpeta de medios, distingue película de serie por el
  nombre del archivo, agrupa los episodios de cada serie, ignora trailers/samples/extras.
- **Reproduce en cualquier navegador** — si el códec ya es compatible, sirve el archivo
  directo; si no, transcodifica a **HLS bajo demanda** (empieza en el primer segmento).
- **Aceleración por hardware** — usa `h264_rkmpp` (VPU) cuando el device está disponible, con
  fallback automático a `libx264`. El contenido 4K se reduce a 1080p antes de recodificar.
- **Continuar donde quedaste** — recuerda la posición de cada película y retoma sin salto.
- **Próximo episodio** — al terminar un episodio, ofrece el siguiente con cuenta regresiva.
- **Subtítulos embebidos** — extrae las pistas de texto a WebVTT y las sirve como `<track>`.
- **Controla la TV desde el celular** — la TV abre una página "tonta" (`tv.html`); el celular es el control.
- **Póster y sinopsis** — enriquecimiento opcional vía [TMDB](https://www.themoviedb.org/).
- **Tres interfaces web** + una página de estado. Sin app que instalar.

---

## 🚀 Cómo ejecutar

### Docker (recomendado)

```bash
git clone https://github.com/DanielHoffmannO/FilmesApi.git
cd FilmesApi
mkdir -p media data
# pon tus videos en ./media (se aceptan subcarpetas)
docker compose up -d --build
```

Abre `http://IP-DEL-SERVIDOR:8080` y haz clic en **📂 Scan Mídia**.

### Local (desarrollo)

```bash
dotnet run --project src/FilmesApi
# usa ./media y ./data en el directorio actual; necesita ffmpeg/ffprobe en el PATH
```

Requiere el **.NET 9 SDK** y `ffmpeg`/`ffprobe`.

---

## 📁 Organizando tus medios

La clasificación es **100% por el nombre del archivo** (contar archivos en la carpeta falla:
una película con trailer parecería una "serie"):

| Tipo | Se detecta por |
|---|---|
| **Serie** | Un marcador de episodio en el nombre: `S01E01`, `1x01`, `Episode 5`, `Capítulo 03`. El nombre de la serie es la carpeta contenedora (o el prefijo del nombre antes del marcador). |
| **Película** | Cualquier cosa sin marcador de episodio. Una carpeta con 2+ archivos y sin marcadores se trata como "película + extras". |

Mantén todos los episodios de una serie **en una sola carpeta** — una carpeta por temporada
hace que cada temporada sea una "serie" aparte y el próximo-episodio no cruza la temporada.

Extensiones reconocidas: `.mp4 .mkv .avi .mov .wmv .flv .webm`

---

## 🖥️ Las pantallas

| URL | Para |
|---|---|
| `/` (`index.html`) | Interfaz principal — catálogo con pósters, filtros, series colapsables, continuar, reproductor con subtítulos y próximo-episodio, y una barra flotante para controlar la TV. |
| `/feia.html` | Interfaz mínima para **smart TVs viejas** (ES5, sin flexbox/grid, navegación por flechas). |
| `/tv.html` | Lo que corre **en la TV** — sin catálogo, dirigido por el celular vía `/api/player/*`. |
| `/status.html` | Diagnóstico: temperatura de la placa, cola de transcode, uso del caché HLS, estado de la VPU. |
| `/swagger` | Documentación interactiva de la API. |

---

## 🎞️ Cómo funciona el streaming

Al pedir una película, el servidor elige el camino:

1. **Compatible** (`h264`/`vp9`/`av1` + `aac`/`mp3`/`opus` en `.mp4`/`.webm`/`.mov`/`.m4v`)
   → sirve el archivo **directo** con soporte `Range`. Sin transcodificación.
2. **Solo el contenedor está mal** → **remux** (stream-copy) a HLS.
3. **El códec de video debe cambiar** → **recodificación incremental** a HLS: intenta
   `h264_rkmpp`, cae a `libx264`; la entrada sobre 1080p se reduce primero; segmentos de 6s,
   la reproducción empieza en el primero.

La salida se cachea **permanentemente por película** con desalojo LRU sobre `HlsCacheMaxGB`.
Una transcodificación a la vez por defecto.

- **Pista de audio:** elige automáticamente portugués, luego la pista por defecto, luego la primera.
- **Subtítulos:** no se muxean en el HLS. Las pistas de texto (SRT/ASS/mov_text) se extraen a
  WebVTT bajo demanda y se sirven como `<track>`. Los subtítulos bitmap (PGS/VobSub) no se pueden convertir.
- **Protecciones:** detector de bloqueo (mata un ffmpeg colgado tras 8 min), cancelación de
  huérfanos (aborta un transcode que nadie mira tras `HlsOrphanTimeoutSeconds`), gobernador
  térmico opcional (retiene nuevos transcodes mientras la placa está muy caliente).

---

## ⚡ Aceleración por hardware (VPU)

El `Dockerfile` incluye **[jellyfin-ffmpeg](https://github.com/jellyfin/jellyfin-ffmpeg)**
(`--enable-rkmpp`), corre como usuario no-root en el grupo `video`, y apunta `FfmpegPath`/
`FfprobePath` a `/usr/lib/jellyfin-ffmpeg/`.

Para usar la VPU del RK3399/RK3588, pasa los devices en `docker-compose.yml`:

```yaml
    devices:
      - /dev/mpp_service   # VPU — encode/decode por hardware
      - /dev/rga           # escalador 2D
      - /dev/dri           # render nodes
```

El servicio hace un probe real al arrancar; si `h264_rkmpp` no funciona en este kernel/placa,
lo registra y usa `libx264` el resto de la ejecución.

> **RK3399:** el **encode H.264** por hardware suele funcionar; el **decode HEVC** por
> hardware necesita que el kernel exponga el clock `clk_hevc_cabac` para `rkvdec` — muchos
> kernels no lo hacen, así que el decode de 4K HEVC sigue en software. Es infraestructura del
> host, no de la app. `HlsRkmppDecodeHw` (por defecto `false`) intenta el decode por hardware
> en el camino 4K — actívalo solo tras validar `scale_rkrga` + `h264_rkmpp` por línea de comando.

Sin los devices (o fuera de ARM), todo corre en `libx264`.

---

## ⚙️ Configuración

Todo vía variables de entorno en `docker-compose.yml` (o `appsettings.json`). Las claves
anidadas usan `__`.

**Básico:** `MediaPath` (`/media`), `ConnectionStrings__Default`
(`Data Source=/data/filmes.db`), `HlsCachePath` (`/data/hls`), `SubtitleCachePath`
(`/data/subs`), `FfmpegPath` / `FfprobePath`.

**Transcodificación:** `MaxConcurrentTranscodeJobs` (`1`), `HlsMaxAlturaReencode` (`1080`,
`0` desactiva el downscale), `HlsCacheMaxGB` (`20`), `HlsOrphanTimeoutSeconds` (`90`, `0`
nunca aborta), `HlsStallTimeoutMinutes` (`8`), `ForceSoftwareEncoder` (`false`),
`HlsRkmppDecodeHw` (`false`).

**Gobernador térmico (opt-in):** `ThermalPauseCelsius` (`0` = off), `ThermalResumeCelsius`
(`pausa − 8`), `ThermalMaxWaitMinutes` (`5`).

**Pre-transcode nocturno (opt-in):** `PreTranscodeEnabled` (`false`), `PreTranscodeHoraUtc`
(`6`), `PreTranscodeMaxItens` (`5`).

**Metadatos TMDB (opt-in):** `TmdbApiKey` (vacío = off), `TmdbLanguage` (`pt-BR`),
`TmdbImageBase`.

Ver el [README en portugués](README.md) para la tabla completa con descripciones.

---

## 🔌 Endpoints

**Catálogo:** `GET /api/filmes`, `GET|POST /api/filmes[/{id}]`,
`PUT /api/filmes/{id}/assistido`, `DELETE /api/filmes/{id}`, `POST /api/filmes/scan`,
`GET /api/filmes/{id}/proximo`.

**Reproducción / progreso:** `GET /api/filmes/continuar`,
`GET|PUT|DELETE /api/filmes/{id}/progresso`, `POST /api/filmes/{id}/concluir`,
`POST /api/filmes/{id}/assistindo`.

**Streaming:** `GET /api/filmes/{id}/stream-status`, `.../pode-direto`, `.../stream`,
`.../original`, `.../hls/playlist.m3u8`, `.../hls/{seg}.ts`, `.../legendas`,
`.../legenda/{idx}`.

**Control remoto de la TV (`/api/player`):** `GET /state`, `POST /selecionar/{id}`,
`/play-pause`, `/parar`, `/seek`, `/seek-abs`, `/volume`, `/legenda`, `/posicao`.

**Diagnóstico:** `GET /api/status`. Detalles completos en `/swagger`.

---

## 🏗️ Stack

.NET 9 / ASP.NET Core · EF Core 8 + SQLite (sin migraciones — `EnsureCreated()` + guardas SQL
idempotentes al arrancar) · Swashbuckle · [hls.js](https://github.com/video-dev/hls.js)
(incluido) · Docker multi-stage (build en Alpine, runtime en Debian bookworm-slim + jellyfin-ffmpeg).

---

## 🔒 Seguridad

**Sin autenticación.** CORS está totalmente abierto. Hecho para vivir **solo en la LAN**,
detrás del router de casa. No expongas el puerto 8080 a internet — usa una VPN (Tailscale,
WireGuard) o un proxy inverso con autenticación para acceso remoto.

---

## 📄 Licencia

[MIT](LICENSE).
