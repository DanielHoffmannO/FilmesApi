// Compartilhado por index.html, tv.html e controle.html — ES5 puro de propósito, porque
// tv.html roda numa smart TV velha (nada de const/let/arrow/template string aqui).
// Eram 3 implementações desse mesmo punhado de funções puras, espalhadas e já divergindo
// (esc() de duas telas escapava aspas, a de uma não; toggleGrupo() era cópia exata em duas).

function esc(s) {
  s = String(s == null ? '' : s);
  return s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
}

// Escapar aspas em texto normal é inofensivo (o navegador mostra "&quot;" como aspas de
// volta) — então esc() já serve pros dois casos. attr() fica só de alias, pra deixar claro
// no call site "isto vai dentro de um atributo" sem precisar de uma função diferente.
var attr = esc;

function formatarTempo(seg) {
  seg = Math.max(0, Math.round(seg || 0));
  var h = Math.floor(seg / 3600), m = Math.round((seg % 3600) / 60);
  return h ? (h + 'h ' + m + 'min') : (m + 'min');
}

// "S01E05 · O Apagador": o rótulo do episódio + o título que o servidor achou no nome do arquivo
// (campo tituloEpisodio). Sem título, ou com o rótulo já mostrando ele ("Ep 12 · título"), só o rótulo.
function rotuloComTitulo(f) {
  var rotulo = f.rotulo || '';
  var titulo = f.tituloEpisodio;
  return titulo && rotulo.indexOf(titulo) === -1 ? rotulo + ' · ' + titulo : rotulo;
}

// GET que nunca lança: erro de rede ou resposta não-2xx viram null (quem chama decide o
// fallback). Só index.html e controle.html usam — a TV velha não tem fetch e vai de XHR.
function fetchJsonOuNull(url) {
  return fetch(url).then(function (r) { return r.ok ? r.json() : null; }).catch(function () { return null; });
}

// Colapsa/expande um <div class="grupo"> de pasta de filme+extras (a série usa a sanfona abaixo). `folderSet` é o
// Set que lembra quais pastas ficam abertos entre renders — hoje só o index.html usa (foldersAbertos), mas
// ele é passado explícito aqui em vez de global pra não acoplar esta função a uma variável de uma tela.
function toggleGrupo(el, folderSet) {
  var grupo = el.parentNode;
  grupo.classList.toggle('collapsed');
  var aberto = !grupo.classList.contains('collapsed');
  if (aberto) folderSet.add(grupo.dataset.key);
  else folderSet.delete(grupo.dataset.key);
  var caret = el.querySelector('.caret');
  if (caret) caret.textContent = aberto ? '▾' : '▸';
}

// ─── Sanfona de séries: série -> temporada -> episódios (index, controle e tv) ───
// A regra mora aqui; as três telas só desenham. Estado em memória (nada de localStorage):
// a tela começa com tudo fechado e o estado sobrevive ao re-render da lista.

var sanfona = { serieAberta: null, temporadaAberta: null };

// Abrir uma série fecha a que estava aberta; clicar na aberta fecha (tudo fechado é válido).
// Trocar ou fechar a série esquece a temporada aberta.
function alternarSerieAberta(chave) {
  sanfona.serieAberta = sanfona.serieAberta === chave ? null : chave;
  sanfona.temporadaAberta = null;
}

// Dentro da série aberta, uma temporada por vez; clicar na aberta fecha.
function alternarTemporadaAberta(chave) {
  sanfona.temporadaAberta = sanfona.temporadaAberta === chave ? null : chave;
}

function serieEstaAberta(chave) { return sanfona.serieAberta === chave; }
function temporadaEstaAberta(chave) { return sanfona.temporadaAberta === chave; }

// Episódios (na ordem recebida) -> grupos [{ chave, rotulo, episodios }]: temporadas numeradas
// em ordem crescente, depois "Especiais" (temporada 0) e "Outros" (sem SxxExx). Devolve null
// quando só há um grupo — a tela então mostra os episódios direto, sem o nível de temporada.
function agruparPorTemporada(episodios) {
  var porNumero = {}, numeradas = [], especiais = null, outros = null;
  for (var i = 0; i < episodios.length; i++) {
    var ep = episodios[i], t = ep.temporada, g;
    if (t == null) {
      g = outros || (outros = { chave: 'outros', rotulo: 'Outros', episodios: [] });
    } else if (t === 0) {
      g = especiais || (especiais = { chave: 'especiais', rotulo: 'Especiais', episodios: [] });
    } else {
      g = porNumero[t];
      if (!g) {
        g = porNumero[t] = { chave: String(t), numero: t, rotulo: 'Temporada ' + t, episodios: [] };
        numeradas.push(g);
      }
    }
    g.episodios.push(ep);
  }
  numeradas.sort(function (a, b) { return a.numero - b.numero; });
  var grupos = numeradas;
  if (especiais) grupos.push(especiais);
  if (outros) grupos.push(outros);
  return grupos.length > 1 ? grupos : null;
}

// "23 episódios" / "1 episódio"
function contagemEpisodios(n) { return n + (n === 1 ? ' episódio' : ' episódios'); }

// "20/23 vistos" ('' se nenhum foi visto)
function textoVistos(episodios) {
  var vistos = 0;
  for (var i = 0; i < episodios.length; i++) if (episodios[i].assistido) vistos++;
  return vistos ? vistos + '/' + episodios.length + ' vistos' : '';
}

// Esqueleto de um .grupo da sanfona (série ou temporada): cabeçalho clicável com caret + corpo. `classe` entra
// depois de "grupo" (ex.: ' temporada') e `dados` é o atributo data-* que aplicarSanfona usa pra achar o grupo.
function grupoHtml(classe, dados, aberto, onclick, tituloHtml, corpoHtml) {
  return '<div class="grupo' + classe + (aberto ? '' : ' collapsed') + '" ' + dados + '>' +
    '<div class="grupo-hd" onclick="' + onclick + '"><span class="caret">' + (aberto ? '▾' : '▸') + '</span>' + tituloHtml + '</div>' +
    '<div class="grupo-body">' + corpoHtml + '</div></div>';
}

// Corpo de uma série (index/controle): seletor de áudio no topo, depois as temporadas
// (cada uma um .grupo recuado, fechado) ou, com uma só, os episódios direto. `linhaHtml(ep)`
// devolve a linha do episódio. `opcoes` (opcional, só o controle usa): `temporadaExtra(numero)` devolve o
// html ao fim do cabeçalho de cada temporada numerada (ou, na série de uma temporada só, numa linha acima dos
// episódios). A linha "Faltam: E01–E08" por temporada entraria aqui, no corpo de cada temporada.
function corpoDaSerieHtml(episodios, audioHtml, linhaHtml, opcoes) {
  var extra = (opcoes && opcoes.temporadaExtra) || function () { return ''; };
  var linhas = function (eps) { return eps.map(linhaHtml).join(''); };
  var grupos = agruparPorTemporada(episodios);
  if (!grupos) {
    var t = episodios.length && episodios[0].temporada;
    var unica = t > 0 ? extra(t) : '';
    return audioHtml + (unica ? '<div class="serie-opcoes">' + unica + '</div>' : '') + linhas(episodios);
  }
  return audioHtml + grupos.map(function (g) {
    var vistos = textoVistos(g.episodios);
    return grupoHtml(' temporada', 'data-temp="' + attr(g.chave) + '"', temporadaEstaAberta(g.chave), 'cliqueTemporada(this)',
      esc(g.rotulo) + ' <span class="count">(' + contagemEpisodios(g.episodios.length) + ')</span>' +
        (vistos ? ' <span class="count">' + vistos + '</span>' : '') + (g.numero > 0 ? extra(g.numero) : ''),
      linhas(g.episodios));
  }).join('');
}

// O .grupo inteiro de uma série (index/controle). `prefixoHtml` vem antes do nome (o pôster, no index).
// `opcoes.sufixoSerie` (html) vai ao fim do cabeçalho (os chips de padrão do controle); o resto de `opcoes` vai pro corpo.
function serieHtml(s, prefixoHtml, audioHtml, linhaHtml, opcoes) {
  return grupoHtml('', 'data-serie="' + attr(s.chave) + '"', serieEstaAberta(s.chave), 'cliqueSerie(this)',
    prefixoHtml + esc(s.nome) + ' <span class="count">(' + contagemEpisodios(s.episodios.length) + ')</span>' +
      ((opcoes && opcoes.sufixoSerie) || ''),
    corpoDaSerieHtml(s.episodios, audioHtml, linhaHtml, opcoes));
}

// Reflete `sanfona` no DOM (classe collapsed + caret) sem refazer a lista — o scroll não pula.
function aplicarSanfona() {
  var marca = function (seletor, atributo, estaAberto) {
    var els = document.querySelectorAll(seletor);
    for (var i = 0; i < els.length; i++) {
      var aberto = estaAberto(els[i].getAttribute(atributo));
      els[i].classList.toggle('collapsed', !aberto);
      var caret = els[i].querySelector('.caret');
      if (caret) caret.textContent = aberto ? '▾' : '▸';
    }
  };
  marca('.grupo[data-serie]', 'data-serie', serieEstaAberta);
  marca('.grupo[data-temp]', 'data-temp', temporadaEstaAberta);
}

// onclick dos cabeçalhos (index e controle). Abrir uma série pode fechar outra ACIMA e empurrar
// tudo: corrige o scroll pra o cabeçalho clicado ficar onde o dedo está.
function cliqueSerie(hd) {
  var antes = hd.getBoundingClientRect().top;
  alternarSerieAberta(hd.parentNode.getAttribute('data-serie'));
  aplicarSanfona();
  window.scrollBy(0, hd.getBoundingClientRect().top - antes);
}

function cliqueTemporada(hd) {
  alternarTemporadaAberta(hd.parentNode.getAttribute('data-temp'));
  aplicarSanfona();
}

// ─── Áudio padrão por série (index.html e controle.html; a TV só obedece o servidor) ───

// { chave: idioma } das séries com idioma definido; {} se o servidor não responder.
function carregarAudiosDasSeries() {
  return fetchJsonOuNull('/api/series/audio').then(function (mapa) { return mapa || {}; });
}

// Seletor "Áudio padrão: Automático / Português / Inglês" pro topo do grupo de uma série.
function seletorAudioSerieHtml(chave, atual) {
  var op = function (valor, rotulo) {
    return '<option value="' + valor + '"' + ((atual || '') === valor ? ' selected' : '') + '>' + rotulo + '</option>';
  };
  return '<label class="serie-audio">Áudio padrão: <select data-chave="' + attr(chave) + '" onchange="definirAudioSerie(this)">' +
    op('', 'Automático') + op('por', 'Português') + op('eng', 'Inglês') + '</select></label>';
}

// Grava a escolha. Vale pro PRÓXIMO episódio que abrir (não derruba quem está assistindo).
function definirAudioSerie(select) {
  return fetch('/api/series/' + encodeURIComponent(select.getAttribute('data-chave')) + '/audio', {
    method: 'PUT', headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ idioma: select.value || null })
  }).then(function (r) { if (!r.ok) alert('Não consegui salvar o áudio padrão da série.'); });
}
