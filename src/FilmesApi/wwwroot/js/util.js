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

// Colapsa/expande um <div class="grupo"> (série ou pasta de filme+extras). `folderSet` é o
// Set (por página) que lembra quais grupos ficam abertos entre renders — index.html e
// controle.html têm cada um o seu, com o mesmo nome (foldersAbertos), mas passado explícito
// aqui em vez de global pra não acoplar esta função a uma variável de uma tela específica.
function toggleGrupo(el, folderSet) {
  var grupo = el.parentNode;
  grupo.classList.toggle('collapsed');
  var aberto = !grupo.classList.contains('collapsed');
  if (aberto) folderSet.add(grupo.dataset.key);
  else folderSet.delete(grupo.dataset.key);
  var caret = el.querySelector('.caret');
  if (caret) caret.textContent = aberto ? '▾' : '▸';
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
