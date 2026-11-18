# Arquitetura do FilmesApi

Servidor pessoal de streaming (ASP.NET, .NET 10, SQLite, ffmpeg) rodando num Rock Pi 4B+. Três clientes web, um só estado de "TV" em memória.

```
 celular ──▶ index.html ─────────────┐        TV Samsung J5200 (Orsay, ES5)
 (grade, ficha,                      │        tv.html ── poll 1,5 s ──┐
  player local, ⚙)                   │                                │
      │ iframe ?embed=1              ▼                                ▼
      └──▶ controle-tv.html ──▶  Controllers  ───────────────▶  PlayerStateService (estado único da TV, em memória)
                                  │   Catalogo · Reproducao · Player · Cache · Progresso · Series · Sono · Saude · Webhook
                                  ▼
                              Serviços ─────────────────────────────────────────────┐
   catálogo   FilmeService · CatalogoAgrupador · ClassificacaoCache · MediaNomeParser │ regras puras (sem I/O)
   metadados  MetadataService · TmdbService · NomesPersonalizadosService             │  testáveis direto
   reprodução HlsTranscodeService (HLS + remux, partial) · CompatibilidadeVideo · FfmpegArgs
   cache      CacheFixadoService · LimpezaCacheService · LimpezaDeCache · CachesDeDisco
   análise    AnaliseDeAberturaService · CapituloDeAbertura · DetectorDeAbertura · ImpressaoDeAudio
   TV         AutomacaoDoPlayer · PlayerStateService (+Fila/+Sono) · ProximoManual · LinhaDoTempoDeEnvio
                                  ▼
            SQLite (/data/filmes.db) · cache em disco /data/{hls,remux,subs} (NVMe) · mídia /media (SSD USB, só leitura) · ffmpeg/ffprobe
```

## Camadas e regras

- **Front → controllers → serviços → dados/disco/ffmpeg.** Controllers só traduzem HTTP; a lógica fica nos serviços. Regras puras (decidir, classificar, montar argumentos) ficam em classes estáticas separadas do I/O (`PoliticaPreAquecimento`, `PoliticaCacheFixado`, `CapituloDeAbertura`, `CompatibilidadeVideo`, `FfmpegArgs`, `LimpezaDeCache`) e têm teste direto.
- **Banco**: sem migrations; `DbInitializer` cria/evolui o esquema com SQL idempotente a cada boot. Tabelas: `Filmes`, `Progressos`, `PreferenciasSerie/Temporada/Episodio`, `Aberturas`, `Creditos`, `Impressoes`, `AnalisesDeDublagem`, `CacheFixado`.
- **Estado da TV** vive só em memória (`PlayerStateService`); o celular escreve comandos, a TV lê por poll. Reiniciar o servidor zera a fila manual.
- **`tv.html` é separado de propósito**: ES5, sem MSE/HLS na J5200, sem teclas coloridas. Compartilha só `js/util.js` e `js/player-regras.js` (ES5) com os outros.

## Front (wwwroot)

| Arquivo | Papel |
|---|---|
| `index.html` + `css/index.css` + `js/index.js` | grade de pôsteres, abas, ficha, configurações, barra "Tocando na TV", enviar/continuar |
| `js/player-local.js` | player do celular (HLS/stream/remux), retomada, abertura/créditos/próximo, overlay único |
| `controle-tv.html` + `css/controle-tv.css` + `js/controle-tv.js` | controle completo da TV; embutido na folha do index (`?embed=1`) ou standalone (fallback `/controle-antigo.html`) |
| `js/player-regras.js` | regras puras de abertura/créditos/botão da direita (ES5, também usado pelo controle) |
| `js/util.js` | utilidades compartilhadas (ES5): `esc`, `fetchJsonOuNull`, `opcoesJson`, sanfona, decisões de salto/próximo |
| `tv.html` | cliente da TV (ES5) |
| `status.html`, `diagnostico.html` + `js/diag-biblioteca.js` | operação do servidor |

`/controle.html` só redireciona para `/?tv=1` (abre a folha do controle).

## Fluxos

**Tocar no celular.** `play(id)` → `/progresso` → `/stream-status` (compatível: `/stream`; senão HLS por hls.js) ou, havendo cópia no cache, `/remux`. Em paralelo carrega abertura, créditos, próximo e interruptores (`carregarContextoLocal`) e pede `pre-aquecer?paraTv=1`. Progresso salvo a cada 10 s; no fim, `/concluir` e oferta do próximo.

**Enviar para a TV.** `POST /api/player/selecionar/{id}` (+ `pre-aquecer?paraTv=1`) → a TV vê no poll → `pode-direto` → `remux-status` (poll 2 s) → baixa `/remux` (mp4). O celular mostra o preparo real (`acompanharPreparoTv`). `LinhaDoTempoDeEnvio` registra no log cada etapa e o tempo desde o clique. Sem `#t=` na URL do mp4 (a J5200 trava): a retomada entra pelo `aplicarRetomada`.

**Cache fixado ("Manter no cache").** `POST /api/cache/fixar` grava em `CacheFixado`; o `CacheFixadoService` prepara, uma de cada vez e só com o servidor ocioso, a cópia `remux/{id}.mp4` (teto `CacheFixadoMaxGB`, limite `CacheFixadoGbPorRodada` por rodada). Fixado nunca é expulso (despejo por teto, limpeza automática e "limpar").

**Aquecimento.** `PreAquecerContinuarService` aquece o #1 do "continuar"; `pre-aquecer` aquece o episódio e o próximo; com `paraTv` basta haver slot de encoder livre e placa fria (`PodeAquecerComJobs`), senão só com tudo ocioso (`Ocioso`).

**Abertura e créditos.** `AnaliseDeAberturaService` acha a abertura (capítulos do arquivo ou impressão de áudio entre episódios) e os créditos (capítulo "Credits/Ending" plausível: ≥ 2 min e ≥ 65% da duração). O servidor ignora créditos implausíveis em `ObterCreditosAsync`; o front repete o piso/fração com a duração (`creditosPlausiveis`).

**Scan e metadados.** `ScanPeriodicoService`/webhook do *arr → `FilmeService` (importa, remove, limpa títulos) → `MetadataService` casa no TMDb **por série** (uma busca por série, cache entre lotes) e grava pôster/sinopse; `nomes.json` dá apelidos, `ignorar` e override de `tmdbId`.

## Débitos conhecidos (de propósito, ver docs/relatorio-simplificacao.md)

`controle-tv.js` ainda carrega a lista/sanfona do controle antigo (só o fallback standalone usa); `ReproducaoController` e `CacheController` ainda têm lógica que cabe em serviço; `tv.html` não foi tocado (frágil).
