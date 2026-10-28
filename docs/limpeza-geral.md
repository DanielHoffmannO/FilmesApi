# Limpeza geral (branch `limpeza-geral`)

Faxina sem mudar comportamento. Base: `master` em `9392c85`. Todos os commits da branch passaram em `dotnet test`,
`node tools/verificar-es5.js` e `dotnet format --verify-no-changes --severity warn`.

## Medidas (antes → depois)

| | Antes | Depois |
|---|---|---|
| Linhas de C# (src + tests) | 12 332 | 12 577 (+245: testes novos e molduras dos arquivos parciais) |
| Maior arquivo de produção | `HlsTranscodeService.cs` 995 | `HlsTranscodeService.Hls.cs` 261 |
| `MediaNomeParser` | 1 arquivo, 695 | 4 arquivos, 95–320 |
| Warnings do compilador | 0 | 0 |
| Testes | 627 | 653 |
| Tempo do `dotnet test` (incl. build) | 21,0 s | 21,0 s |
| Rotas (`[Http*]`, `[Route]`, `Map*`) | 55 | 55 — **diff vazio** |

Rotas: lista extraída por `grep` dos atributos `[HttpGet|Post|Put|Delete|Patch]`, `[Route]` e das chamadas `Map*` em `src/`
(sem número de linha), antes no `master` e depois nesta branch: `diff` vazio.

## O que foi movido (só movimento, linhas intactas)

- `Services/HlsTranscodeService.cs` → `HlsTranscodeService.cs` (campos, caminhos, interesse, compatibilidade) +
  `.Remux.cs`, `.Cache.cs`, `.Audio.cs`, `.Hls.cs`, `.Status.cs` (classe `partial`).
- `Services/MediaNomeParser.cs` → `MediaNomeParser.cs` (regex, caminho/pasta, episódio/série) + `.Nomes.cs`, `.Classificacao.cs`, `.Titulo.cs`.
- Prova: script que compara o multiconjunto de linhas não vazias do arquivo antigo com o da soma dos novos (menos
  `namespace`/`using`/declaração da classe e a chave final): iguais. Única mudança textual nas linhas originais: `public class` →
  `public partial class` e um `using` que sobrou no arquivo principal do parser.

## O que foi consolidado (sem mudar valor)

- `CachesDeDisco.MargemDeUsoNaExpulsao` (30 min), `FracaoAlvoAoExpulsar` (0,9), `ValidadeDoRetrato` (20 s): estavam como literais
  repetidos em `HlsTranscodeService` e `LimpezaCacheService`.
- `NomesDeCache` (`playlist.m3u8`, `seg_*.ts`, `seg_%05d.ts`, `.mp4`, `.tmp`, `.audio`, `.remux`): estavam espalhados por
  `HlsTranscodeService`, `LimpezaDeCache` e `FfmpegArgs`. Valores presos por `NomesDeCacheTests` (o formato já está em disco na produção).

## O que foi corrigido (`fix:`, com teste que falhava antes)

1. `EscolhaAudio.Escolher` comparava o idioma com a lista de português sem ignorar maiúsculas, enquanto `EhPortugues` ignorava: faixa
   marcada `POR` não era a preferida. Agora há uma regra só (teste com `"POR"` falhava).
2. "Diários de Um Vampiro" aparecia como "T V D"/"DDUV" quando a pasta crua era a mais frequente. O agrupamento já estava certo
   (apelidos padrão); faltava o nome de exibição fixo em `NomesPadrao` (`nomes.json` continua sobrescrevendo). Achado conhecido nº 7 resolvido.

## Testes novos

- `NomesDeCacheTests` (formato do cache), `PrefixosRemoviveisTests` (7 casos; mutação `>=`→`>` confere), `NomesDoAcervoRealTests`
  (Mentalista `Dublado-WWW.BLUDV.COM`, Rick and Morty DUAL 5.1, Attack on Titan S03 CR WEB-DL, Presidente Curtis, DDUVT08EP13, com e
  sem pasta), caso de maiúsculas em `EscolherStreamAudioTests`.

## O que foi removido

Nada. Procurei código morto assim: lista de todo membro declarado em `src/` (exceto controllers, cujas ações são chamadas pelas rotas)
com 0 ou 1 ocorrência do nome em `src/` + `wwwroot/` + `tests/` + `tools/`. Só apareceram a implementação de interface
`CheckHealthAsync` (usada pelo framework) e overrides do EF. `IDE0005` (using sobrando) limpo. JS/CSS: nenhuma função de
`tv/controle/index/status/diagnostico.html` ou `util.js`, nem classe de `base.css`, sem referência.

## O que deixei de propósito

- **Duas listas de extensões de vídeo**: `FilmeService.VideoExtensions` (sem `.m4v`) e `LegendaExternaLocalizador.ExtVideo` (com `.m4v`).
  Têm propósitos diferentes e unificar mudaria o que o scan importa. Anotado, não alterado.
- **Duas tabelas de idioma** (`EscolhaAudio.Aliases`, códigos do ffprobe, e `LegendaExternaLocalizador.TokenIdioma`, palavras de nome de
  arquivo): `TokenIdioma` exclui de propósito tokens ambíguos (`it`, `de`, `es`, `fr`), então não dá para fundir sem mudar comportamento.
- **`{id}.mp4.audio`** é gravado direto (não `.tmp` + rename): são poucos bytes, escritos depois do `.mp4` já renomeado atomicamente;
  perder a anotação só faz o cache ser tratado como "antigo" (mantido). Trocar criaria um novo tipo de sobra (`.audio.tmp`) para a limpeza.
- **Decisão de assistido**: `Filme.DefinirAssistido` cobre os caminhos que mexem na entidade; o `ExecuteUpdate` de `ConcluirAsync` precisa
  setar `Assistido`/`AssistidoEm` por conta própria (não passa pela entidade).
- **Seção "Continuar assistindo" de `/tela`** coexiste com `/api/filmes/fila` (a TV e o `index.html` ainda leem `continuarAssistindo`).
- **Robustez já presente** (auditada, sem mudança): nenhum `async void`; `.Result` só em tarefa já concluída (`ProcessRunner`);
  todo ffmpeg/ffprobe passa por `ProcessRunner` (timeout + kill da árvore) ou `MediaProbeService` (timeout + kill); jobs de fundo
  com `try/catch` que registra; remux e legendas gravam `.tmp` e renomeiam.
- **Widget Orsay**: intocado, como pedido.
- **Testes lentos/instáveis**: a suíte inteira roda em ~18 s; não achei teste instável nas execuções desta sessão (6 rodadas completas
  idênticas). Não mexi em relógio/IO.
