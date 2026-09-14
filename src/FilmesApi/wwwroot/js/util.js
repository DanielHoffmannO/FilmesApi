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
