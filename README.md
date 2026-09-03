🌐 [English](README.en.md) | [Español](README.es.md)

# 🎬 FilmesApi

[![.NET CI](https://github.com/DanielHoffmannO/FilmesApi/actions/workflows/dotnet.yml/badge.svg)](https://github.com/DanielHoffmannO/FilmesApi/actions)
![.NET](https://img.shields.io/badge/.NET-9.0-512BD4?logo=dotnet)
![SQLite](https://img.shields.io/badge/SQLite-003B57?logo=sqlite&logoColor=white)
![Docker](https://img.shields.io/badge/Docker-Ready-2496ED?logo=docker&logoColor=white)
![License](https://img.shields.io/badge/license-MIT-green)

> Servidor pessoal de streaming pra sua coleção de filmes e séries, rodando na rede de casa.
> Aponta pra uma pasta com seus vídeos, sobe o container, e assiste de qualquer aparelho —
> celular, PC, TV nova ou aquela smart TV velha que não abre site nenhum.

Pensado pra rodar 24/7 num mini-PC ou numa placa ARM (Radxa / Rock Pi / Orange Pi), com
transcodificação sob demanda e aceleração por hardware (VPU do RK3399/RK3588) quando disponível.

---

## ✨ O que ele faz

- **Catálogo automático** — varre a pasta de mídia, separa filme de série pelo nome do arquivo,
  agrupa os episódios de cada série e ignora trailer/sample/extra.
- **Toca em qualquer navegador** — se o codec já é compatível, serve o arquivo direto; senão
  transcodifica pra **HLS sob demanda** (começa a tocar no primeiro segmento, não espera o filme inteiro).
- **Aceleração por hardware** — usa `h264_rkmpp` (VPU) quando o device está disponível, com
  fallback automático pra `libx264`. Conteúdo 4K é reduzido pra 1080p antes de recodificar,
  pra não fritar a placa.
- **Continuar de onde parou** — guarda a posição de cada filme e retoma sem pulo.
- **Próximo episódio** — no fim de um episódio, oferece o próximo da série com contagem regressiva.
- **Legendas embutidas** — extrai as faixas de texto pra WebVTT e serve como `<track>` (menu CC nativo).
- **Controle pela TV via celular** — a TV abre uma página "burra" (`tv.html`) e o celular vira o controle.
- **Pôster e sinopse** — enriquecimento opcional via [TMDB](https://www.themoviedb.org/).
- **Três interfaces web** + página de status. Sem app pra instalar.

---

## 🚀 Como rodar

### Docker (recomendado)

```bash
git clone https://github.com/DanielHoffmannO/FilmesApi.git
cd FilmesApi
mkdir -p media data
# jogue seus vídeos dentro de ./media (pode ter subpastas)
docker compose up -d --build
```

Acesse `http://IP-DO-SERVIDOR:8080` e clique em **📂 Scan Mídia**.

O `docker-compose.yml` que vem no repo é o mínimo. Veja
[Aceleração por hardware](#-aceleração-por-hardware-vpu) e [Configuração](#-configuração)
pra ligar VPU, TMDB, governador térmico, etc.

### Local (desenvolvimento)

```bash
dotnet run --project src/FilmesApi
# usa ./media e ./data no diretório atual; precisa de ffmpeg/ffprobe no PATH
```

Requer o **.NET 9 SDK** e `ffmpeg`/`ffprobe` instalados.

```bash
dotnet test        # MediaNomeParser (nome de arquivo -> série/episódio) + downmix 5.1 do HLS
```

---

## 📁 Organizando sua mídia

A classificação é **100% pelo nome do arquivo** (contar arquivos na pasta erra: um filme com
trailer viraria "série"). O que importa:

### Séries — marcador de episódio no nome

| Formato | Exemplos que casam |
|---|---|
| `SxxExx` | `Breaking Bad S01E01.mkv`, `Serie.S3.E10.mkv`, `Show S01 E100.mkv` |
| `NxNN` | `Breaking Bad 1x01.mkv`, `Serie 12x05.mkv` (não confunde com `720p`) |
| `Episódio N` / `Capítulo N` | `Novela Capítulo 01.mkv`, `Anime - Episodio 11.mkv`, `Show Episode 5.mkv` |

O **nome da série** é a pasta onde o arquivo está; se estiver solto na raiz, é o pedaço do
nome antes do marcador. Os episódios são ordenados por temporada e número.

```
media/
├── Breaking Bad/
│   ├── Breaking Bad S01E01.mkv     → série "Breaking Bad", S01E01
│   └── Breaking Bad S01E02.mkv
├── Novela Grande/
│   ├── Novela Grande Capítulo 01.mkv
│   └── Novela Grande Capítulo 02.mkv
└── Futurama S02E05.mkv             → série "Futurama" (solto na raiz)
```

> **Uma pasta por temporada** (`Serie/Season 1/`, `Serie/Season 2/`) faz cada temporada
> virar uma "série" separada, e o "próximo episódio" não cruza a virada de temporada.
> Prefira todos os episódios da série na mesma pasta.

### Filmes — qualquer coisa sem marcador de episódio

- `Filme 2020 1080p BluRay.mp4` solto → filme.
- Uma pasta com 2+ arquivos e nenhum marcador de episódio → tratada como **filme + extras**
  (trailer, making-of…). Se sobrar só 1 arquivo "de verdade", ele aparece solto.

Arquivos com `trailer`, `sample`, `extras`, `featurette`, `bastidores`, `deleted` no nome são
filtrados como extras.

Extensões reconhecidas: `.mp4 .mkv .avi .mov .wmv .flv .webm`

---

## 🖥️ As telas

| URL | Pra quê |
|---|---|
| `/` (`index.html`) | Interface principal — catálogo em lista compacta, busca, filtros (tipo / assistido), pastas e séries colapsáveis, "continuar assistindo", player com legenda e próximo-episódio, e uma barra flutuante pra controlar a TV. |
| `/feia.html` | Interface mínima pra **smart TV antiga** (ES5, sem flexbox/grid, navegação por setas do controle). Toca o arquivo original direto; botão vermelho liga/desliga legenda. |
| `/tv.html` | O que fica **aberto na TV**. Não tem catálogo — é dirigido pelo celular via `/api/player/*` (selecionar, play/pause, seek, volume, legenda). |
| `/status.html` | Diagnóstico: temperatura da placa, fila de transcode, uso do cache HLS, estado da VPU. |
| `/swagger` | Documentação interativa da API. |

---

## 🎞️ Como funciona o streaming

Ao pedir um filme, o servidor decide o caminho:

1. **Compatível** (`h264`/`vp9`/`av1` + `aac`/`mp3`/`opus` em `.mp4`/`.webm`/`.mov`/`.m4v`)
   → serve o arquivo **direto**, com suporte a `Range` (seek instantâneo). Zero transcodificação.

2. **Só o container é o problema** → **remux** por stream-copy pra HLS (rápido, sem perda).

3. **O vídeo precisa mudar de codec** → **reencode incremental** pra HLS:
   - tenta `h264_rkmpp` (VPU) → se falhar, `libx264` (`-preset veryfast -crf 23`);
   - entrada acima de 1080p é **reduzida pra 1080p** antes de recodificar (`HlsMaxAlturaReencode`);
   - segmentos de 6s, playlist do tipo `event` — o play começa assim que o **primeiro segmento** existe.

O resultado fica em **cache permanente por filme** (`/data/hls/{id}/`), com despejo LRU quando
passa de `HlsCacheMaxGB`. Só **1 transcodificação por vez** por padrão (`MaxConcurrentTranscodeJobs`).

**Faixa de áudio:** escolhe automaticamente português (`por`/`pt`/`pob`), senão a marcada como
_default_, senão a primeira. (Rip dual-áudio costuma vir com a faixa errada como default.)
Sempre reencodada pra **AAC estéreo** (`-ac 2`): AAC 5.1/7.1 sem `channel_layout` reconhecido
é rejeitado _em silêncio_ pelo decoder do Chrome (MSE/hls.js) — o vídeo não toca e não há erro.

**Legendas:** não são muxadas no HLS. As faixas de **texto** (SRT/ASS/mov_text) são extraídas
sob demanda pra WebVTT (`/api/filmes/{id}/legenda/{idx}`) e servidas como `<track>`. Legenda
**bitmap** (PGS/VobSub) não dá pra converter em texto — fica listada como `convertivel: false`.

**Proteções:**
- **Detector de travamento** — mata o ffmpeg se a contagem de segmentos não muda por 8 min.
- **Cancelamento de órfão** — se ninguém pede status/segmento há `HlsOrphanTimeoutSeconds` (90s),
  aborta o transcode (o caso clássico: abriu um 4K, viu "Preparando", desistiu). Volta a ser
  retentável. Os players mandam um _keepalive_ (`POST /{id}/assistindo`) enquanto tocam.
- **Governador térmico** (opt-in) — segura o início de um novo transcode enquanto a placa
  estiver acima de `ThermalPauseCelsius`.

---

## ⚡ Aceleração por hardware (VPU)

O `Dockerfile` usa **[jellyfin-ffmpeg](https://github.com/jellyfin/jellyfin-ffmpeg)** (com
`--enable-rkmpp`), roda como usuário `filmesapi` (uid 1000) no grupo `video`, e aponta
`FfmpegPath`/`FfprobePath` pra `/usr/lib/jellyfin-ffmpeg/`.

Pra usar a VPU do RK3399/RK3588, passe os devices no `docker-compose.yml`:

```yaml
    devices:
      - /dev/mpp_service   # VPU — encode/decode de hardware
      - /dev/rga           # scaler/blitter 2D
      - /dev/dri           # render nodes
```

O serviço faz um _probe_ real no boot (um encode sintético) — se `h264_rkmpp` não funcionar
neste kernel/placa, ele registra no log e usa `libx264` pelo resto da execução. Depois de 3
falhas em runtime, desliga a VPU até o próximo restart.

> **RK3399:** o **encode H.264** por hardware costuma funcionar; o **decode HEVC** por hardware
> depende do kernel expor o clock `clk_hevc_cabac` pro `rkvdec` — vários kernels não expõem, e
> aí o decode de 4K HEVC continua em software (mais pesado). Isso é infra do host, não da
> aplicação. `HlsRkmppDecodeHw` (default `false`) tenta o decode por hardware no caminho 4K;
> só ligue depois de validar `scale_rkrga` + `h264_rkmpp` na linha de comando.

Sem os devices (ou fora de ARM), tudo roda em `libx264` normalmente.

---

## ⚙️ Configuração

Tudo via variável de ambiente no `docker-compose.yml` (ou `appsettings.json`). Chaves aninhadas
usam `__` (ex.: `ConnectionStrings__Default`).

### Básico

| Chave | Padrão | Descrição |
|---|---|---|
| `MediaPath` | `/media` | Pasta com os vídeos (varredura recursiva). |
| `ConnectionStrings__Default` | `Data Source=/data/filmes.db` | Banco SQLite. |
| `HlsCachePath` | `/data/hls` | Cache dos segmentos HLS. |
| `SubtitleCachePath` | `/data/subs` | Cache das legendas `.vtt` extraídas. |
| `FfmpegPath` / `FfprobePath` | `ffmpeg` / `ffprobe` | Binários (o Docker aponta pro jellyfin-ffmpeg). |

### Transcodificação

| Chave | Padrão | Descrição |
|---|---|---|
| `MaxConcurrentTranscodeJobs` | `1` | Transcodificações simultâneas. |
| `HlsMaxAlturaReencode` | `1080` | Reduz entradas mais altas pra esta altura antes de recodificar. `0` = sem downscale. |
| `HlsCacheMaxGB` | `20` | Teto do cache HLS; acima disso, despejo LRU. `0` = ilimitado. |
| `HlsOrphanTimeoutSeconds` | `90` | Aborta o transcode se ninguém pede status/segmento por este tempo. `0` = nunca aborta. |
| `HlsStallTimeoutMinutes` | `8` | Mata o ffmpeg se não gerar segmento novo neste intervalo. |
| `TranscodeJobTimeoutHours` | `6` | Teto absoluto por job de ffmpeg. |
| `ForceSoftwareEncoder` | `false` | Ignora a VPU e usa sempre `libx264`. |
| `HlsRkmppDecodeHw` | `false` | Tenta decode por hardware no caminho 4K (experimental — ver acima). |

### Governador térmico (opt-in)

| Chave | Padrão | Descrição |
|---|---|---|
| `ThermalPauseCelsius` | `0` | Acima desta temperatura, segura novos transcodes. `0` = desligado. Defina depois de ver os números reais da placa (`sensors`). |
| `ThermalResumeCelsius` | `pausa − 8` | Libera quando a placa cai pra este valor. |
| `ThermalMaxWaitMinutes` | `5` | Depois disso, transcodifica mesmo quente (melhor que travar o vídeo pra sempre). |
| `ThermalRoot` | `/sys/class/thermal` | Onde ficam as zonas térmicas. |

### Pré-transcode noturno (opt-in)

| Chave | Padrão | Descrição |
|---|---|---|
| `PreTranscodeEnabled` | `false` | Liga a passada noturna. |
| `PreTranscodeHoraUtc` | `6` | Hora (UTC) em que roda. |
| `PreTranscodeMaxItens` | `5` | Quantos filmes prepara por noite (retomadas + adições recentes não assistidas). |

### Metadados TMDB (opt-in)

| Chave | Padrão | Descrição |
|---|---|---|
| `TmdbApiKey` | _(vazio)_ | Chave v3 da [API do TMDB](https://www.themoviedb.org/settings/api) (grátis). Sem chave, o enriquecimento fica desligado. |
| `TmdbLanguage` | `pt-BR` | Idioma dos metadados. |
| `TmdbImageBase` | `https://image.tmdb.org/t/p/w342` | Base das URLs de pôster. |

Com a chave configurada, um serviço em background busca título oficial, sinopse e pôster pros
filmes ainda sem metadados (roda ~30s depois do boot e reprocessa a cada scan).

---

## 🔌 Endpoints

### Catálogo

| Método | Rota | Descrição |
|---|---|---|
| `GET` | `/api/filmes?assistido=` | Lista (filtro opcional). Traz pôster, sinopse e ponto de retomada de cada item. |
| `GET` | `/api/filmes/{id}` | Detalhes de um filme. |
| `POST` | `/api/filmes` | Adiciona manualmente (`{titulo, anoLancamento?, diretor?, arquivoPath?}`). |
| `PUT` | `/api/filmes/{id}/assistido` | Alterna "assistido". |
| `DELETE` | `/api/filmes/{id}` | Remove do catálogo (+ progresso + caches). |
| `POST` | `/api/filmes/scan` | Importa vídeos novos da pasta e remove órfãos. `{importados, removidos}`. |
| `GET` | `/api/filmes/{id}/proximo` | Próximo episódio da série. `204` se não há. |

### Reprodução / progresso

| Método | Rota | Descrição |
|---|---|---|
| `GET` | `/api/filmes/continuar` | "Continuar assistindo" (mais recentes primeiro). |
| `GET` `PUT` `DELETE` | `/api/filmes/{id}/progresso` | Lê / salva / esquece onde a reprodução parou. |
| `POST` | `/api/filmes/{id}/concluir` | Marca assistido e limpa a retomada (evento `ended` do player). |
| `POST` | `/api/filmes/{id}/assistindo` | Keepalive — "ainda tem alguém assistindo". |

### Streaming

| Método | Rota | Descrição |
|---|---|---|
| `GET` | `/api/filmes/{id}/stream-status` | `compativel` / `preparando` / `disponivel` / erro `500`. |
| `GET` | `/api/filmes/{id}/pode-direto` | `{compativel: bool}` sem disparar transcode (usado pela `feia.html`). |
| `GET` | `/api/filmes/{id}/stream` | Stream direto (só quando compatível), com `Range`. |
| `GET` | `/api/filmes/{id}/original` | Sempre o arquivo original, sem transcodificar (VLC etc.). |
| `GET` | `/api/filmes/{id}/hls/playlist.m3u8` | Manifesto HLS (dispara/reusa o transcode). `202` enquanto prepara. |
| `GET` | `/api/filmes/{id}/hls/{seg}.ts` | Segmentos HLS. |
| `GET` | `/api/filmes/{id}/legendas` | Faixas de legenda embutidas. |
| `GET` | `/api/filmes/{id}/legenda/{idx}` | Uma faixa de texto convertida pra WebVTT. |

### Controle remoto do player da TV — `/api/player`

| Método | Rota | Descrição |
|---|---|---|
| `GET` | `/state` | Estado atual (a `tv.html` faz poll disso). |
| `POST` | `/selecionar/{filmeId}` | TV começa a tocar este filme. |
| `POST` | `/play-pause` · `/parar` | Transporte. |
| `POST` | `/seek` `{delta}` · `/seek-abs` `{pos}` · `/volume` `{valor}` · `/legenda` `{idx}` | Comandos. |
| `POST` | `/posicao` `{pos, dur}` | A TV reporta a posição (pro celular desenhar a barra). |

### Diagnóstico

| Método | Rota | Descrição |
|---|---|---|
| `GET` | `/api/status` | Temperatura, fila de transcode, cache, estado da VPU (consumido pela `status.html`). |

---

## 🏗️ Arquitetura

```
src/FilmesApi/
├── Controllers/            (todos sob /api/filmes, exceto onde indicado)
│   ├── CatalogoController.cs     listar/criar/remover, scan da pasta, próximo episódio
│   ├── ProgressoController.cs    "continuar de onde parou", concluir
│   ├── ReproducaoController.cs   stream direto vs HLS, playlist/segments, legendas, keepalive
│   ├── PlayerController.cs       /api/player — controle remoto da TV (estado compartilhado)
│   └── StatusController.cs       /api/status
├── Services/
│   ├── FilmeService.cs           CRUD + scan da pasta de mídia
│   ├── ProgressoService.cs       "continuar de onde parou"
│   ├── HlsTranscodeService.cs    decisão compat/remux/reencode, cache, filas
│   ├── RkmppCapabilityService.cs probe da VPU + fallback pra libx264
│   ├── ThermalService.cs         leitura de /sys/class/thermal + backpressure
│   ├── SubtitleService.cs        listar/extrair legendas → WebVTT
│   ├── MediaNomeParser.cs        série/episódio/ano a partir do nome do arquivo
│   ├── TmdbService.cs            busca no TMDB
│   ├── MetadataService.cs        enriquecimento em background (BackgroundService)
│   ├── PreTranscodeService.cs    passada noturna (BackgroundService)
│   ├── PlayerStateService.cs     estado do "player da TV" (singleton)
│   └── ProcessRunner.cs          executa ffmpeg com timeout + detector de travamento
├── Models/                       entidades EF + DTOs
├── Data/AppDbContext.cs          Filmes + Progressos (SQLite)
├── wwwroot/                      index.html · feia.html · tv.html · status.html · vendor/hls.min.js
└── Program.cs                    DI, pipeline, "auto-migração" no boot

tests/FilmesApi.Tests/          corpus do MediaNomeParser + downmix 5.1→estéreo do HLS
```

**Stack:** .NET 9 / ASP.NET Core · EF Core 8 + SQLite · Swashbuckle (Swagger) ·
[hls.js](https://github.com/video-dev/hls.js) (embutido, sem CDN) · Docker multi-stage
(build em Alpine, runtime em Debian bookworm-slim + jellyfin-ffmpeg).

**Banco:** sem EF Migrations — `EnsureCreated()` no primeiro boot, e blocos `ExecuteSqlRaw`
idempotentes (`CREATE TABLE IF NOT EXISTS`, `ADD COLUMN` guardado por `pragma_table_info`)
pra evoluir bancos já existentes. Roda a cada inicialização.

---

## 🔒 Segurança

**Não tem autenticação.** CORS é aberto (`AllowAnyOrigin`). Foi feito pra viver **só na LAN**,
atrás do roteador de casa. Não exponha a porta 8080 direto na internet — se precisar de acesso
remoto, use uma VPN (Tailscale, WireGuard) ou um proxy reverso com autenticação na frente.

---

## 📄 Licença

[MIT](LICENSE).
