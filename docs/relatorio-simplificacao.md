# Relatório de simplificação (rodada de 18–19/11/2026)

Escopo: o que mudou nesta semana (grade, player local, controle embutido, cache fixado, créditos, aquecimento da TV) + organização geral. **Só refatoração, mesmo comportamento.** Guarda-corpo: `docs/inventario-antes.md` (gerado por `tools/gerar-inventario.py` antes de mexer) e o mesmo inventário depois.

## Diff de inventário (antes × depois)

- **Rotas HTTP: diff vazio** (80 → 80). **Páginas, tabelas, chaves de configuração, tipos e métodos do back-end: diff vazio.**
- **Funções do front: 8 removidas, todas sem nenhum chamador (grep + inventário)**, 2 novas (helpers):
  `toggleGrupo`, `carregarAudiosDasSeries`, `seletorAudioSerieHtml`, `definirAudioSerie` (util.js; o controle e o index não as chamam desde que áudio/legenda saíram da ficha), `cartaoDeFilme` (index.js), `atualizarAposReproducao` (wrapper vazio de `render()`), `rotuloIdioma` (alias nunca usado), `salvarInterruptorLocal` (virou `salvarInterruptorDaTemporada` em util.js). Novas: `opcoesJson`, `salvarInterruptorDaTemporada`. `pad2` e `pw` só mudaram de arquivo (index.js → util.js).

## Medidas

| | antes | depois |
|---|---|---|
| `controle-tv.html` | 1221 linhas (CSS+JS inline) | 109 (+ `css/controle-tv.css` 193, `js/controle-tv.js` 899) |
| `css/index.css` | 243 | 206 (18 regras mortas e 19 declarações sobrescritas) |
| `js/util.js` | 290 | 274 (−17 mortas, +helpers) |
| `js/index.js` / `player-local.js` | 645 / 599 | 634 / 578 |
| cópias de `fetch` com corpo JSON | 15 | 1 (`opcoesJson`) |
| PlayerController: construtores | 2 (+ atributo) | 1 |
| arquivos em `wwwroot` | 21 | 23 (a separação do controle) |

## O que foi feito (um commit por assunto)

1. `docs: inventario` — guarda-corpo.
2. `front: remove codigo morto` — funções sem chamador e 18 regras de CSS sem uso (grep em html/js).
3. `css: funde declaracoes sobrescritas` — fica só o valor final de cada propriedade (verificado por print).
4. `front: helpers compartilhados no util.js` — `opcoesJson`, `salvarInterruptorDaTemporada`, `pad2`, `pw` no lugar de 15 cópias do fetch JSON, 2 do PUT do interruptor, 2 de `padStart` e 1 troca de tamanho do TMDb. `util.js` segue ES5 (o `tv.html` o carrega).
5. `controllers: PlayerController com um construtor so` (parâmetros opcionais), `IdsInvalidos`/`TetoParaJson` no CacheController, e `GET /api/cache` não lê o catálogo quando não há fixados.
6. `cache: estado atomico dos fixados` (lista e conjunto num valor só), uma conta única de espaço livre (`CabeSemExpulsar` também no pré-aquecimento), assistidos lidos sob demanda no despejo, `BytesDoRemux` com um stat.
7. `controle-tv: separa CSS e JS`.
8. `front: a barra Tocando na TV nao consulta atras da folha` (o iframe já consulta a 1 s).
9. `metadados: confere overrides so quando o nomes.json mudou` (antes recarregava o catálogo enriquecido a cada lote).

Cada passo: 1287 testes .NET e os 6 `tools/verificar-*.js` passando; telas conferidas por print (390/360 px).

## O que ficou de propósito (débito, com motivo)

- **`tv.html`**: intocado (frágil, Orsay J5200).
- **Lista/sanfona em `controle-tv.js`**: o fallback `/controle-antigo.html` ainda usa; apagar junto com ele após a semana de fallback (`serieHtml` e opções do util.js idem, só o `verificar-temporadas.js` as exercita).
- **Lógica em controllers** (`ReproducaoController.PreAquecer`, retrato do `CacheController`) e a flag `paraTv` do pré-aquecimento (virar política): exigem teste de carga da proteção térmica; ficam para uma rodada própria.
- **`ProximoManual` 180 s × `PROXIMO_ENSINA_RESTANTE_S` 90 s** (player local): o comentário diz "igual ao servidor", o valor difere. Decisão: qual é a intenção (muda o comportamento).
- **Controle da TV chama `pre-aquecer` sem `paraTv`** (o index passa): parece involuntário, mas alinhar muda o aquecimento.
- **CSS paisagem do `#acaoLocal`** nunca valeu (vinha antes da regra base); removi a linha sem mudar o visual — reposicionar de verdade é mudança visual.
- **Delays de 300 ms duplicados no `MetadataService`, `ObterStatusRemux` fazendo I/O sob lock, `MedirCacheRemux` sem cache**: ganhos pequenos frente ao risco; anotados.
- **ES modules no front**: arriscado (handlers `onclick` inline, ordem dos scripts, `tv.html`).

## Não testado em aparelho real

Tudo acima foi conferido por testes, verificadores node e prints headless. Não foi testado em celular/TV: a folha do controle em iPhone, o player com vídeo real, o pré-aquecimento sob carga e o `tv.html` (não mudou nesta rodada).
