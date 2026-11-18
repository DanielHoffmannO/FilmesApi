# Inventário de comportamento (gerado por tools/gerar-inventario.py)

## Rotas HTTP (80)
```
DELETE /api/filmes/{id:int}/progresso   (ProgressoController.cs)
DELETE /api/player/fila   (PlayerController.cs)
DELETE /api/player/fila/{filmeId:int}   (PlayerController.cs)
GET    "/widgetlist.xml"   (Program.cs, minimal API)
GET    /api/cache   (CacheController.cs)
GET    /api/catalogo/dublados   (DubladosController.cs)
GET    /api/catalogo/novidades   (NovidadesController.cs)
GET    /api/diagnostico/biblioteca   (SaudeBibliotecaController.cs)
GET    /api/filmes/diagnostico   (CatalogoController.cs)
GET    /api/filmes/fila   (CatalogoController.cs)
GET    /api/filmes/tela   (CatalogoController.cs)
GET    /api/filmes/{id:int}   (CatalogoController.cs)
GET    /api/filmes/{id:int}/abertura   (ReproducaoController.cs)
GET    /api/filmes/{id:int}/audios   (ReproducaoController.cs)
GET    /api/filmes/{id:int}/creditos   (ReproducaoController.cs)
GET    /api/filmes/{id:int}/download   (ReproducaoController.cs)
GET    /api/filmes/{id:int}/hls/playlist.m3u8   (ReproducaoController.cs)
GET    /api/filmes/{id:int}/hls/{arquivo}   (ReproducaoController.cs)
GET    /api/filmes/{id:int}/legenda/{idx:int}   (ReproducaoController.cs)
GET    /api/filmes/{id:int}/legendas   (ReproducaoController.cs)
GET    /api/filmes/{id:int}/original   (ReproducaoController.cs)
GET    /api/filmes/{id:int}/pode-direto   (ReproducaoController.cs)
GET    /api/filmes/{id:int}/progresso   (ProgressoController.cs)
GET    /api/filmes/{id:int}/proximo   (CatalogoController.cs)
GET    /api/filmes/{id:int}/remux   (ReproducaoController.cs)
GET    /api/filmes/{id:int}/remux-status   (ReproducaoController.cs)
GET    /api/filmes/{id:int}/stream   (ReproducaoController.cs)
GET    /api/filmes/{id:int}/stream-status   (ReproducaoController.cs)
GET    /api/player/state   (PlayerController.cs)
GET    /api/series/audio   (SeriesController.cs)
GET    /api/series/padroes   (SeriesController.cs)
GET    /api/series/temporadas   (SeriesController.cs)
GET    /api/series/{chave}/audio   (SeriesController.cs)
GET    /api/status   (ReproducaoController.cs)
GET    /health   (health check)
GET    /health/vivo   (health check)
GET    OrsayWidget.CaminhoZip   (Program.cs, minimal API)
POST   "/api/admin/backup"   (Program.cs, minimal API)
POST   "/api/admin/nomes"   (Program.cs, minimal API)
POST   "/api/admin/recarregar-nomes"   (Program.cs, minimal API)
POST   "/api/diag/log"   (Program.cs, minimal API)
POST   /api/cache/desafixar   (CacheController.cs)
POST   /api/cache/fixar   (CacheController.cs)
POST   /api/cache/limpar   (CacheController.cs)
POST   /api/diagnostico/biblioteca/verificar   (SaudeBibliotecaController.cs)
POST   /api/filmes/scan   (CatalogoController.cs)
POST   /api/filmes/{id:int}/assistindo   (ReproducaoController.cs)
POST   /api/filmes/{id:int}/audio   (ReproducaoController.cs)
POST   /api/filmes/{id:int}/concluir   (ProgressoController.cs)
POST   /api/filmes/{id:int}/pre-aquecer   (ReproducaoController.cs)
POST   /api/player/abertura-pulada   (PlayerController.cs)
POST   /api/player/audio   (PlayerController.cs)
POST   /api/player/fila/{filmeId:int}   (PlayerController.cs)
POST   /api/player/interacao   (SonoController.cs)
POST   /api/player/legenda   (PlayerController.cs)
POST   /api/player/legenda-estilo   (PlayerController.cs)
POST   /api/player/legenda-tv   (PlayerController.cs)
POST   /api/player/parar   (PlayerController.cs)
POST   /api/player/play-pause   (PlayerController.cs)
POST   /api/player/posicao   (PlayerController.cs)
POST   /api/player/presenca-continuar   (SonoController.cs)
POST   /api/player/presenca-pergunta   (SonoController.cs)
POST   /api/player/proximo-aceita   (PlayerController.cs)
POST   /api/player/proximo-auto   (PlayerController.cs)
POST   /api/player/proximo-cancelar   (PlayerController.cs)
POST   /api/player/proximo-manual   (PlayerController.cs)
POST   /api/player/proximo-oferece   (PlayerController.cs)
POST   /api/player/pular-abertura   (PlayerController.cs)
POST   /api/player/seek   (PlayerController.cs)
POST   /api/player/seek-abs   (PlayerController.cs)
POST   /api/player/selecionar/{filmeId:int}   (PlayerController.cs)
POST   /api/player/sono   (SonoController.cs)
POST   /api/player/sono-fim   (SonoController.cs)
POST   /api/player/volume   (PlayerController.cs)
POST   /api/webhook/arr   (WebhookArrController.cs)
PUT    /api/filmes/{id:int}/assistido   (CatalogoController.cs)
PUT    /api/filmes/{id:int}/progresso   (ProgressoController.cs)
PUT    /api/series/{chave}/audio   (SeriesController.cs)
PUT    /api/series/{chave}/legenda   (SeriesController.cs)
PUT    /api/series/{chave}/temporadas/{n:int}   (SeriesController.cs)
```

## Páginas (7)
```
controle-antigo.html
controle-tv.html
controle.html
diagnostico.html
index.html
status.html
tv.html
```

## Tabelas/DbSets (11)
```
Aberturas
AnalisesDeDublagem
CacheFixado
CacheFixados
Creditos
Filmes
Impressoes
PreferenciasEpisodio
PreferenciasSerie
PreferenciasTemporada
Progressos
```

## Chaves de configuração (73)
```
AberturaAtrasoInicialSegundos  <- AnaliseDeAberturaService.cs
AberturaIntervaloMinutos  <- AnaliseDeAberturaService.cs
AberturaMinimoPuloSegundos  <- AutomacaoDoPlayer.cs, appsettings.json
AberturaMinimoSegundos  <- AnaliseDeAberturaService.cs, appsettings.json
AberturaOrcamentoMinutos  <- AnaliseDeAberturaService.cs
AberturaUsarCapitulos  <- AnaliseDeAberturaService.cs, appsettings.json
AdminToken  <- Program.cs
AindaAssistindoAposAvancos  <- PlayerStateService.Sono.cs
AllowAnyOrigin  <- Program.cs
AllowedHosts  <- appsettings.json
Backup  <- appsettings.json
Backup:Hora  <- BackupService.cs
Backup:Quantidade  <- BackupService.cs
CacheFixadoAtrasoInicialSegundos  <- CacheFixadoService.cs
CacheFixadoGbPorRodada  <- CacheFixadoService.cs
CacheFixadoIntervaloSegundos  <- CacheFixadoService.cs
CacheFixadoMaxGB  <- CacheFixadoService.cs
CachePath  <- CachesDeDisco.cs, EspacoLivre.cs
ConnectionStrings  <- appsettings.json
Default  <- appsettings.json
DownloadsSimultaneos  <- LimitadorDeDownloads.cs
DubladoIaAtrasoInicialSegundos  <- DubladoIaService.cs
DubladoIaIntervaloMinutos  <- DubladoIaService.cs
DubladoIaLoteMax  <- DubladoIaService.cs
DubladoIaOrcamentoSegundos  <- DubladoIaService.cs
DubladoIaPausaEntreLotesSegundos  <- DubladoIaService.cs
EspacoLivreMinimoGB  <- EspacoLivre.cs, appsettings.json
FfmpegPath  <- FfmpegOptions.cs, Program.cs
FfprobePath  <- FfmpegOptions.cs
ForceSoftwareEncoder  <- RkmppCapabilityService.cs
HlsCacheMaxGB  <- HlsTranscodeService.cs, appsettings.json
HlsFalhaCooldownMinutes  <- HlsTranscodeService.cs
HlsMaxAlturaReencode  <- HlsTranscodeService.cs, appsettings.json
HlsOrphanTimeoutSeconds  <- HlsTranscodeService.cs, appsettings.json
HlsRkmppDecodeHw  <- HlsTranscodeService.cs, appsettings.json
HlsStallTimeoutMinutes  <- HlsTranscodeService.cs
HlsValidadeDias  <- LimpezaCacheService.cs
Hora  <- appsettings.json
LimpezaCacheAtrasoInicialSegundos  <- LimpezaCacheService.cs
LimpezaCacheCarenciaMinutos  <- LimpezaCacheService.cs
LimpezaCacheMinutos  <- LimpezaCacheService.cs
LogLevel  <- appsettings.json
Logging  <- appsettings.json
MaxConcurrentTranscodeJobs  <- HlsTranscodeService.cs, appsettings.json
MediaPath  <- DubladoIaService.cs, EspacoLivre.cs, FilmeService.cs, Program.cs, SaudeBibliotecaService.cs, appsettings.json
NomesPath  <- NomesPersonalizadosService.cs, SaudeBibliotecaService.cs
NovidadesDias  <- NovidadesController.cs
Pasta  <- appsettings.json
PreAquecerAtrasoInicialSegundos  <- PreAquecerContinuarService.cs
PreAquecerContinuarMinutos  <- PreAquecerContinuarService.cs
ProximoAutomaticoContagemSegundos  <- AutomacaoDoPlayer.cs
ProximoRestanteSegundos  <- AutomacaoDoPlayer.cs
Quantidade  <- appsettings.json
RemuxCacheMaxGB  <- HlsTranscodeService.cs
SaudeBibliotecaAtrasoInicialSegundos  <- SaudeBibliotecaService.cs
SaudeBibliotecaIntervaloHoras  <- SaudeBibliotecaService.cs
SaudeBibliotecaJanelaFimSegundos  <- SaudeBibliotecaService.cs
SaudeBibliotecaOrcamentoMinutos  <- SaudeBibliotecaService.cs
SaudeBibliotecaPath  <- SaudeBibliotecaService.cs
SaudeBibliotecaPausaSegundos  <- SaudeBibliotecaService.cs
SaudeBibliotecaTimeoutSegundos  <- SaudeBibliotecaService.cs
ScanEstabilidadeSegundos  <- FilmeService.cs
ScanIntervaloMinutos  <- ScanPeriodicoService.cs
ThermalMaxWaitMinutes  <- ThermalService.cs
ThermalPauseCelsius  <- SaudeBibliotecaService.cs, ThermalService.cs, appsettings.json
ThermalResumeCelsius  <- ThermalService.cs
ThermalRoot  <- ThermalService.cs
TmdbApiKey  <- TmdbService.cs, appsettings.json
TmdbImageBase  <- TmdbService.cs
TmdbLanguage  <- TmdbService.cs
TranscodeJobTimeoutHours  <- HlsTranscodeService.cs
Webhook:DebounceSegundos  <- DebounceDeScan.cs
Webhook:EsperaMaximaSegundos  <- DebounceDeScan.cs
```

## Funções do front (310)

### src/FilmesApi/wwwroot/js/diag-biblioteca.js (10)
```
agendar
carregar
celulaArquivo
linhaSimples
render
secao
tabelaDuplicatas
tabelaSimples
tempoAtras
verificar
```

### src/FilmesApi/wwwroot/js/index.js (52)
```
abrirConfig
abrirControleTv
abrirFicha
acompanharPreparoTv
barraProgresso
botaoCacheHtml
buscarDigitando
cacheDaSerie
cacheDoFilme
cartaoDaFila
cartaoDeFilme
cartoesDaTela
codigoEpisodio
consultarAgora
continuarNaTv
continuarNoCelular
desafixarCache
enviarParaTv
fecharControleTv
fecharFicha
fileira
filmeTile
fixarCache
gb
headers
irParaAba
limparCache
load
loadbar
montarFicha
mostrarPreparoTv
mostrarVazio
msgScanErro
nomeDoEpisodio
pad2
pctProgresso
postarJson
postarScan
posterHtml
pw
removerDeContinuar
render
rowHtml
scanMedia
sem
sinopseHtml
tile
tituloEpisodio
tocarASeguir
toggleAssistido
tooltipFilme
trocarTokenDoScan
```

### src/FilmesApi/wwwroot/js/player-local.js (39)
```
acaoDoBotaoLocal
alternarPausa
aplicarRetomada
arbitrarAcaoLocal
atualizarAposReproducao
atualizarControlesLocais
avisoLocal
cancelarProximo
cancelarProximoPeloUsuario
carregarContextoLocal
carregarLegendas
checarStatus
closePlayer
configurarProgresso
desligarInterruptorLocal
destruirHls
duracaoLocal
iniciarHls
iniciarWatchdogLiveEdge
irProximo
marcarAtividadeLocal
novoContextoLocal
ofereceProximo
pararWatchdogLiveEdge
pinta
pintarBotaoLocal
pintarTrilhoLocal
play
posLocal
posicionarSobreVideo
preAquecerProximo
preparandoHtml
proximoEpisodioLocal
pularAberturaLocal
reiniciarContextoLocal
removerLegendas
salvarInterruptorLocal
salvarProgresso
tick
```

### src/FilmesApi/wwwroot/js/player-regras.js (8)
```
aberturaContem
aberturaParaSalto
creditosAlcancados
creditosPlausiveis
estadoLocalDoPlayer
faixaEmPercentual
modoDoBotaoDireita
proximoManualEnsina
```

### src/FilmesApi/wwwroot/js/util.js (30)
```
agruparPorTemporada
alternarSerieAberta
alternarTemporadaAberta
aplicarSanfona
carregarAudiosDasSeries
cliqueSerie
cliqueTemporada
contagemDoProximo
contagemEpisodios
contagemNoFim
corpoDaSerieHtml
decidirProximoAutomatico
decidirSaltoAbertura
definirAudioSerie
esc
fetchJsonOuNull
formatarTempo
grupoHtml
nomeDoIdioma
ordenarPorIdioma
recuarRetomada
rotuloComTitulo
seletorAudioSerieHtml
serieEstaAberta
serieHtml
servidorReiniciou
temporadaEstaAberta
textoVistos
toggleGrupo
videoEhDoFilme
```

### src/FilmesApi/wwwroot/controle-antigo.html (0)
```

```

### src/FilmesApi/wwwroot/controle-tv.html (85)
```
abrirFila
abrirFolhaAgora
abrirFolhaSerie
abrirSono
atualizarBotaoDireita
atualizarBotaoProximoEp
atualizarCapa
atualizarLinhaIa
atualizarTags
carregarPadroes
carregarTemporadasDaSerie
cartaoFila
ckHtml
codigoEp
definirCta
definirInterruptor
definirSono
dentroDaAbertura
descreverDaFila
desenharFila
desenharFolha
desenharSono
ehPortugues
enfileirar
enviarParaTv
faixaIa
faixaOriginal
fecharEmbed
fecharFila
fecharFolha
fecharSono
fimDoQue
fioDoIniciado
folhaAberta
folhaFilaAberta
folhaSonoAberta
gravarEscolhaComoPadraoDaSerie
ico
interruptoresTemporadaHtml
limparFila
lin
linhaEp
linhaFilme
linhaOpcao
mmss
mostrarFolha
nosCreditos
pil
pilulasHtml
pintarFaixa
pintarInterruptores
pollRemote
qbHtml
recarregarInterruptores
remoteAceitarProximo
remoteAlternarIa
remoteCarregarAbertura
remoteCarregarAudios
remoteCarregarCreditos
remoteCarregarLegendas
remoteContinuar
remoteDireita
remoteParar
remotePlayPause
remoteProximoEp
remotePularAbertura
remoteScrub
remoteSeek
remoteTrocarAudio
remoteVolume
render
rotuloIdioma
rotulosDasFaixas
salvarPadrao
segOpcao
serieHtmlControle
tagPadraoHtml
textoDoSono
tirarDaFila
toast
tocandoAgora
toggleAssistido
tratarAviso
tratarFila
tratarSono
```

### src/FilmesApi/wwwroot/controle.html (0)
```

```

### src/FilmesApi/wwwroot/diagnostico.html (10)
```
aplicarNome
arquivo
campoTmdb
carregar
copiar
enviar
headers
render
secao
tamanho
```

### src/FilmesApi/wwwroot/index.html (0)
```

```

### src/FilmesApi/wwwroot/status.html (3)
```
mb
tick
txt
```

### src/FilmesApi/wwwroot/tv.html (73)
```
ajustarVideo
alternarTelaCheiaPlayer
anexarDublados
anexarNovidades
aplicarAudioRemoto
aplicarEstadoRemoto
aplicarEstiloLegenda
aplicarModoLegenda
aplicarRetomada
armarWatchdog
ativar
ativarLegenda
avancoSegurado
avisarLegendaTv
avisoNoPlayer
buildNav
buscarProximo
cancelarProximo
carregarLegendas
coletarNav
conferirMetadadosDaRetomada
configurarProgresso
contagemEps
destruirHls
diagLegendaTela
diagRecarga
diagTrilhasNativas
emTelaCheiaNativa
epsHtml
esconderPergunta
fecharPlayer
focar
get
grpHtml
grupoAberto
guardarTocando
iniciarHls
integrarTizen
irProximo
itemHtml
ligarLegendaRecomendada
load
logDiag
mostrarPergunta
ofereceProximo
pararPoll
pararWatchdog
parseVtt
perguntando
perguntarSeAindaAssiste
pollRemoto
pollRemuxEtocar
pollStatus
pollStatusEtocar
posicionarCuesNativos
preAquecerProximo
removerLegendas
render
renderLegenda
salvarProgresso
seguraNoFim
send
sonoNaTv
suportaHlsNativo
suportaMseHls
teclaNaTv
tempoVtt
tick
tocar
tocarSemHls
tocarUrl
toggleGrp
trilhaNativa
```

## Tipos do back-end (193)
```
class     AberturaEpisodio
class     AindaAssistindo
class     AnaliseDeAberturaService
class     AnaliseDeDublagem
class     AppDbContext
class     AprendizadoDePulo
class     ArmazemDeImpressoes
class     ArquivoDeSaude
class     AutomacaoDoPlayer
class     BackupService
class     CacheController
class     CacheFixado
class     CacheFixadoService
class     CachesDeDisco
class     CapituloDeAbertura
class     CatalogoAgrupador
class     CatalogoController
class     ClassificacaoCache
class     CompatibilidadeVideo
class     CompressaoHttp
class     CreditosEpisodio
class     DbInitializer
class     DebounceDeScan
class     DetectorDeAbertura
class     DiagnosticoCatalogo
class     DubladoIaService
class     Dublados
class     DubladosController
class     DublagemEmPortugues
class     EscolhaAudio
class     EscritaDeCache
class     EspacoHealthCheck
class     EspacoLivre
class     EstadoMontagens
class     FfmpegArgs
class     FilaContinuar
class     Filme
class     FilmeService
class     GlobCaminho
class     HlsTranscodeService
class     ImpressaoDeAudio
class     ImpressaoSalva
class     InteracaoHumana
class     InteracaoHumanaFilter
class     LegendaExternaLocalizador
class     LimitadorDeDownloads
class     LimpezaCacheService
class     LimpezaDeCache
class     LinhaDoTempoDeEnvio
class     MediaNomeParser
class     MediaProbeService
class     Medicao
class     MetadataService
class     MontagensHealthCheck
class     NomeDeDownload
class     NomesDeCache
class     NomesPersonalizadosService
class     Novidades
class     NovidadesController
class     OrigemMarca
class     OrsayWidget
class     PlayerController
class     PlayerStateService
class     PoliticaCacheFixado
class     PoliticaPreAquecimento
class     PreAquecerContinuarService
class     PreferenciaEpisodio
class     PreferenciaSerie
class     PreferenciaTemporada
class     PreferenciasAudioService
class     PreferenciasTemporadaService
class     ProcessRunner
class     ProgressoController
class     ProgressoReproducao
class     ProgressoService
class     ProximoManual
class     RegistroDeMarcacao
class     RegistroDeTarefas
class     ReproducaoController
class     RkmppCapabilityService
class     SaudeBiblioteca
class     SaudeBibliotecaController
class     SaudeBibliotecaService
class     SaudeBibliotecaServiceCollectionExtensions
class     ScanPeriodicoService
class     SeriesController
class     SeveridadeSaude
class     SonoController
class     SqlitePragmaInterceptor
class     StreamDeDownload
class     SubtitleService
class     SugestoesDeNomes
class     ThermalService
class     TimerDeSono
class     TipoSaude
class     TmdbService
class     TokenAdmin
class     WebhookArr
class     WebhookArrController
enum      AcaoDoWebhook
enum      AudioPortugues
enum      CategoriaCache
enum      CompatibilidadeDireta
enum      DecisaoSalvarProgresso
enum      DisparoDeSaude
enum      EfeitoNaPresenca
enum      EstadoEstrutura
enum      NoFimDoEpisodio
enum      RemuxStatus
enum      Resultado
enum      ResultadoAdicao
enum      ResultadoFila
enum      StreamStatus
enum      Tipo
enum      TipoCache
enum      VeredictoTruncamento
interface IMarcacao
record    AchadoSaude
record    ArquivoDiag
record    ArquivoParaDuplicata
record    AudioInfo
record    AudioRequest
record    AudioTrackRequest
record    BackupOptions
record    CapituloArquivo
record    CategoriasCache
record    ClassificacaoMidia
record    ContagemCache
record    ContagemNomes
record    ContextoDoFilme
record    ContinuarAssistindoResponse
record    DadosDeTruncamento
record    DecisaoTruncamento
record    DiagnosticoResponse
record    EntradaCache
record    FaixaAudio
record    FaixaLegenda
record    FaltandoDiag
record    FfmpegOptions
record    FilmeResponse
record    GanchosDeCache
record    GrupoDuplicado
record    HlsStatusSnapshot
record    IdsRequest
record    InventarioCache
record    ItemFila
record    ItemNovidade
record    ItemSaude
record    LegendaEstiloRequest
record    LegendaExterna
record    LegendaInfo
record    LegendaRequest
record    LegendaTvRequest
record    LeituraDoFim
record    Ligados
record    MediaInfo
record    MontagemDiag
record    NomeRequest
record    NomesArquivo
record    OpcoesLimpeza
record    PastaAgrupada
record    PosicaoRequest
record    PresencaPerguntaRequest
record    ProgressoRequest
record    ProgressoResponse
record    ProximoOfertaRequest
record    RajadaDePulo
record    RelatorioSaude
record    RepetidoDiag
record    ResultadoArquivoSaude
record    ResultadoBackup
record    ResultadoRodada
record    ResumoLimpeza
record    ScanResultado
record    SeekAbsRequest
record    SeekRequest
record    SemMetadadosDiag
record    SerieAgrupada
record    SerieAudioRequest
record    SerieAudioResponse
record    SobraCache
record    SonoRequest
record    SugestaoNome
record    SuspeitoDiag
record    Tarefa
record    TelaCatalogoResponse
record    TemporadaRequest
record    TmdbResultado
record    Trecho
record    TrechoComum
record    UmEpisodioDiag
record    VolumeEspaco
record    VolumeRequest
```

## Métodos públicos/internal de Services e Controllers (418)
```
Abertura
AberturaPulada
AceitarProximo
AchadoSaude
AddCompressaoHttp
AddSaudeBiblioteca
Adicionar
AguardarResfriamentoAsync
Analisar
AnalisarCompatibilidadeAsync
AnaliseDeAberturaService
Apagar
AplicarRetencao
AposAvancoAutomatico
AprenderAsync
ArgsLeituraDoFim
ArquivoDiag
ArquivoParaDuplicata
Arr
Assistindo
AteProximo
AtingiuLimiteDaRodada
Atualizar
AtualizarContextoAsync
Audios
AutomacaoDoPlayer
Autorizado
Avaliar
Avisar
BackupOptions
BuscarAsync
BuscarPorIdAsync
BytesDeRemuxAssistidoDespejavel
BytesDoRemux
BytesTotaisDoRemux
CabeNoCache
CabeNoTetoFixado
CabeSemExpulsar
CacheController
CacheDeEstaticos
CacheFixadoService
CaixaDeTitulo
CaminhoPlaylist
CaminhoRemux
CancelarProximoAutomatico
CapituloArquivo
CarregarAsync
Casa
CategoriasCache
ChaveAgrupamento
ChaveBaseFranquia
ChaveConfere
ChaveDeCache
ChaveEpisodio
ChaveSerie
ChavesDeOverride
CheckHealthAsync
ClassificacaoMidia
Classificar
ClassificarAudios
CobreAConhecida
Codificar
Concluir
ConcluirAsync
ConfirmarPresenca
ContaComoFalhaDeEncoder
ContagemCache
ContagemNomes
ContarArquivo
ContarNumeradosPorPasta
ContarPasta
ContextoDoFilme
Continuar
ContinuarAssistindoAsync
Creditos
CreditosPlausivel
Da
DadosDeTruncamento
De
DecidirNoFim
DecidirSalvamento
DecidirTruncado
DecisaoTruncamento
Declarar
Decodificar
DecodificarTexto
Definir
DefinirAudio
DefinirContexto
DefinirFaixaDoEpisodio
DefinirIdiomaDaSerie
DefinirLegenda
DefinirLegendaDaSerie
DefinirNomesPersonalizados
DefinirPreferenciaAudio
DefinirSono
DefinirTemporada
Desafixar
DesafixarAsync
DescartarCaches
DescartarDeArquivoTrocadoAsync
DescartarRemuxDeOutraFaixaAsync
DesenfileirarUm
DespejarExcedenteRemux
DeveCeder
DevePerguntar
Devida
Diagnostico
DiagnosticoAsync
DiagnosticoResponse
DicaNoNome
DiretorioCache
Disparar
DispararSonoAoFim
DisponivelAsync
Dispose
DisposeAsync
Do
Download
DubladoIaService
DubladosController
DuplicatasDoCatalogo
DuplicatasPorEpisodio
DuplicatasPorNomeBase
Duracao
Efeito
EhDubladoIa
EhEpisodio
EhExtra
EhLegenda
EhNovela
EhPastaDeLegenda
EhPortugues
Encontrar
Endpoint
Enfileirar
EntradaCache
EntradaVale
EnumerarArquivosDeVideo
Escolher
EscolherCandidato
EscolherTituloLatino
EspacoHealthCheck
ExecutarAsync
ExecutarComTimeoutAsync
ExecutarRodadaAsync
ExecutarUmaVezAsync
ExisteAsync
ExtrairDaBusca
ExtrairItem
FaixaAudio
FaixaDeEpisodios
FaixaLegenda
Falhou
FaltandoDiag
FfmpegOptions
Fila
Filtro
Fixar
FixarAsync
From
GanchosDeCache
Gerar
GrupoDuplicado
Habilitado
Hls
HlsPlaylist
HlsSegmento
IdiomaDaSerie
IdiomaDoNome
IdsRequest
Ignorado
Iniciar
InicioDosCreditos
InspecionarAsync
Interacao
InteracaoHumanaFilter
InteresseExpirou
InterpretacaoDoWebhook
Interpretar
InterpretarLeituraDoFim
InterpretarResposta
Inventariar
InventarioAsync
InventarioCache
ItemFila
ItemNovidade
ItemSaude
ItensDeDuplicatas
ItensDosAchados
JaAcabou
JaCompativel
Janela
JanelaDoFim
JuntarDuplicatas
Legenda
LegendaDaSerie
LegendaDaTv
LegendaDoEpisodio
LegendaExterna
LegendaParaPreparar
LegendaPorPadraoDaSerie
Legendas
LeituraDoFim
Ler
Ligados
Ligar
LigarProximoAutomaticoAsync
LigarPuloAutomaticoAsync
LimitadorDeDownloads
Limite
Limpar
LimparCache
LimparCacheExcedente
LimparFila
LimparNaoFixados
LimpezaCacheService
ListaXml
Listar
ListarAsync
ListarAudiosAsync
Localizar
Marcar
MarcarAberturaPulada
MarcarAssistido
MarcarAssistidoAsync
MarcarAsync
MarcarRecomendada
MediaInfo
Medicao
MelhorTrecho
Mesclar
MinimoGB
MontagemDiag
MontagensHealthCheck
Montar
MontarRelatorio
NomeBaseFranquia
NomeExibicao
NomePastaExibicao
NomesArquivo
NomesPersonalizadosService
Normalizar
NormalizarBusca
NormalizarIdioma
NovidadesController
Obter
ObterAberturaAsync
ObterAsync
ObterAudio
ObterCaminhoSegmento
ObterCreditosAsync
ObterDublados
ObterEstado
ObterFilaAsync
ObterNovidades
ObterProgresso
ObterRelatorio
ObterRemuxPronto
ObterSnapshot
ObterStatusAsync
ObterStatusRemux
ObterStreamStatus
ObterTelaAsync
ObterVttAsync
OferecerProximo
Ok
OnActionExecuted
OnActionExecuting
OpcoesLimpeza
Ordem
OrdemEpisodio
Ordenar
OrdenarEpisodios
OrdenarPorRelevancia
Original
OverrideDe
Padroes
ParaRegex
Parar
ParecemASerieMesma
Parsear
PerguntarSeAindaAssiste
PlayerStateService
PodarEntradas
PodeDireto
PodeStreamDiretoAsync
PreAquecer
PreAquecerContinuarService
PreAquecerRemux
PreAquecerSeNecessarioAsync
PrecisaAnalisar
PrecisaAnalisarCreditos
PreferenciasAudioService
PreferenciasTemporadaService
PrefixoDe
PrefixosRemoviveis
PrepararEmSegundoPlano
PresencaPergunta
ProcessarLoteAsync
ProcurarModelo
ProgressoDaVersao
Protegida
ProximoAceita
ProximoAuto
ProximoCancelar
ProximoEpisodio
ProximoEpisodioAsync
ProximoManual
ProximoManualAsync
ProximoOferece
PularAbertura
RajadaDePulo
RankDeFormato
Read
ReadAsync
Recarregar
Registrar
RegistrarAvancoAutomatico
RegistrarInteracao
RegistrarInteresse
RegistrarLegendaDaTv
RegistrarResultado
RelatorioSaude
Relevancia
RemoverDaFila
RemoverProgresso
RemoverProgressoAsync
Remux
RemuxEmAndamento
RemuxStatus
RepetidoDiag
ReportarPosicao
Resolver
ResolverCaminhoAsync
RestanteSegundos
ResultadoArquivoSaude
ResultadoBackup
ResultadoEstrutura
ResultadoRodada
Resumir
Resumo
ResumoEmTexto
ResumoLimpeza
RodadaAsync
RodarAsync
SalvarAsync
SalvarProgresso
SarPrecisaCorrecao
SaudeBibliotecaController
SaudeBibliotecaService
ScanMedia
ScanMediaAsync
ScanPeriodicoService
Seek
SeekAbsoluto
SeguraOAvanco
Selecionar
SelecionarAsync
SemAcentoMinuscula
SemMetadados
SemMetadadosDiag
SemPortugues
SeriesController
SetAudio
SetEstiloLegenda
SetLegenda
SetLegendaEstilo
SetVolume
Snapshot
SoGuardadaAsync
SobraCache
Solicitar
Sono
SonoController
SonoFim
StartAsync
Status
StopAsync
Stream
SugestaoNome
SuspeitoDiag
Tamanho
TamanhoDaPasta
TamanhoDe
Tarefa
Tela
TemEscritaNaoLatina
TemperaturaC
TempoAteVencer
TemporadasPendentes
TentarColher
TentarEntrar
TituloDoEpisodio
TituloIndicaDubladoIa
TituloParaBusca
TituloParaExibir
TmdbResultado
Todas
TodasAsLegendas
TodasAsSeries
TodasTemporadas
TodosAudios
TogglePlayPause
TranscodePesado
Trecho
TrechoComum
UmEpisodioDiag
Validar
Varrer
Venceu
Vencimento
Verificar
VerificarEstrutura
VideoTocavelSemReencode
VolumeEspaco
Volumes
WebhookArrController
```
