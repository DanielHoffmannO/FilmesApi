const API = '/api/filmes';
const PLAYER_API = '/api/player';
const EMBED = document.body.classList.contains('embed');   // dentro da folha do index.html: sem lista, só o controle

// ─── Configuração (valores antes soltos pelo código) ───────────────────────
const SEEK_STEP_S = 30;      // avanço/retrocesso dos botões «30s / 30s»
const POLL_ATIVO_MS = 1000;  // intervalo de poll de /player/state com algo tocando
const POLL_OCIOSO_MS = 3000; // intervalo de poll sem nada tocando (menos urgente)

let filtroTipo = 'all';         // all | filme | serie
let filtroDublados = false;     // chip "Dublados": só o que tem áudio em português (dublagem de verdade ou por IA)

// ─── Ícones (SVG inline, traço 1.8, currentColor): nada de emoji nem de fonte/CDN externa ───
const IC = {
  grid: '<rect x="4" y="4" width="6.5" height="6.5" rx="1.5"/><rect x="13.5" y="4" width="6.5" height="6.5" rx="1.5"/><rect x="4" y="13.5" width="6.5" height="6.5" rx="1.5"/><rect x="13.5" y="13.5" width="6.5" height="6.5" rx="1.5"/>',
  caret: '<path d="M9 6l6 6l-6 6"/>',
  dots: '<circle cx="5" cy="12" r="1"/><circle cx="12" cy="12" r="1"/><circle cx="19" cy="12" r="1"/>',
  check: '<path d="M5 12l5 5l10-10"/>',
  close: '<path d="M6 6l12 12M18 6L6 18"/>',
  circle: '<circle cx="12" cy="12" r="8.5"/>',
  tv: '<rect x="3" y="7" width="18" height="12" rx="2"/><path d="M16 3l-4 4l-4-4"/>',
  skip: '<path d="M5 5l8 7l-8 7z"/><path d="M13 5l8 7l-8 7z"/>',
  fila: '<path d="M4 7h11M4 12h11M4 17h6M18 13v8M14 17h8"/>',
  next: '<path d="M7 4v16l10-8z"/><path d="M20 5v14"/>',
  nextF: '<path d="M5.5 4.5v15L16 12z"/><rect x="18" y="5" width="2.4" height="14" rx="1.2"/>',
  back: '<path d="M3.06 13a9 9 0 1 0 .49-4.087"/><path d="M3 4v5h5"/>',
  pause: '<rect x="6" y="5" width="4" height="14" rx="1"/><rect x="14" y="5" width="4" height="14" rx="1"/>',
  play: '<path d="M8 5v14l11-7z"/>',
  sliders: '<path d="M4 6h8M16 6h4M4 12h2M10 12h10M4 18h12M20 18h0"/><circle cx="14" cy="6" r="2"/><circle cx="8" cy="12" r="2"/><circle cx="18" cy="18" r="2"/>',
  vol: '<path d="M15 8a5 5 0 0 1 0 8"/><path d="M6 15H4a1 1 0 0 1-1-1v-4a1 1 0 0 1 1-1h2l3.5-4.5a.8.8 0 0 1 1.5.5v14a.8.8 0 0 1-1.5.5L6 15"/>',
  stop: '<rect x="6" y="6" width="12" height="12" rx="2"/>',
};
const ico = (nome, cls) => `<svg viewBox="0 0 24 24" class="${cls || ''}" aria-hidden="true">${IC[nome]}</svg>`;
document.querySelectorAll('[data-ico]').forEach(el => { el.innerHTML = ico(el.dataset.ico); });

// Elementos consultados em 2+ lugares — pegos uma vez só.
const els = {
  remote: document.getElementById('remote'),
  remoteProximoEpBtn: document.getElementById('remoteProximoEpBtn'),
};

document.getElementById('filters').addEventListener('click', e => {
  const btn = e.target.closest('button[data-filter]');
  if (!btn) return;
  document.querySelectorAll('#filters button').forEach(b => { b.classList.toggle('on', b === btn); b.setAttribute('aria-selected', b === btn); });
  filtroTipo = btn.dataset.filter;
  render();
});

// esc() e a sanfona das séries vêm de /js/util.js.

// "E02" (ou "E23-24" nos arquivos duplos) a partir do rótulo do servidor; sem SxxExx, o número do episódio.
function codigoEp(f) {
  const m = /E\d+(?:-E?\d+)?/i.exec(f.rotulo || '');
  return m ? m[0].toUpperCase() : (f.episodio != null ? 'E' + pad2(f.episodio) : '');
}
const tocandoAgora = f => !!(ultimoEstadoRemoto && ultimoEstadoRemoto.filmeId === f.id);

// "tocar a seguir": entra no fim da fila manual (com nada tocando, só toca)
const qbHtml = f => `<button class="qb" aria-label="Tocar a seguir" onclick="event.stopPropagation(); enfileirar(${f.id})">${ico('fila')}</button>`;

// "marcar como assistido": área de toque de 44x44 (a linha inteira manda pra TV)
function ckHtml(f) {
  const rot = f.assistido ? 'Desmarcar como assistido' : 'Marcar como assistido';
  return `<button class="ck${f.assistido ? ' on' : ''}" aria-label="${rot}" aria-pressed="${!!f.assistido}" onclick="event.stopPropagation(); toggleAssistido(${f.id})">${ico(f.assistido ? 'check' : 'circle')}</button>`;
}

// episódio/filme que já foi aberto (tem posição salva) mas não terminado: um fio fino de progresso embaixo da linha
function fioDoIniciado(f) {
  if (f.assistido || !(f.posicaoSegundos > 0)) return '';
  const pct = f.duracaoSegundos > 0 ? Math.max(4, Math.min(96, 100 * f.posicaoSegundos / f.duracaoSegundos)) : 8;
  return `<i class="ini" style="width:${pct.toFixed(0)}%"></i>`;
}


function linhaEp(f) {
  const tc = tocandoAgora(f);
  const titulo = f.tituloEpisodio || (f.episodio != null ? 'Episódio ' + f.episodio : f.titulo);
  return `<div class="ep${tc ? ' tc' : ''}${f.assistido ? ' vi' : ''}" onclick="enviarParaTv(${f.id})">
      <span class="n">${esc(codigoEp(f))}</span><span class="t">${esc(titulo)}</span>${tc ? ico('tv', 'pl') : ''}${qbHtml(f)}${ckHtml(f)}${fioDoIniciado(f)}
    </div>`;
}

function linhaFilme(f) {
  const tc = tocandoAgora(f);
  return `<div class="ep filme${tc ? ' tc' : ''}${f.assistido ? ' vi' : ''}" onclick="enviarParaTv(${f.id})">
      <span class="t"><b>${esc(f.titulo)}</b>${f.anoLancamento ? `<small>${f.anoLancamento}</small>` : ''}</span>${tc ? ico('tv', 'pl') : ''}${qbHtml(f)}${ckHtml(f)}${fioDoIniciado(f)}
    </div>`;
}

// Cartão do "Continuar assistindo": série (ou filme), "T4 · E22" e um fio vermelho de progresso quando há posição salva
// chave da série -> nome de exibição (o MESMO da lista de séries: o mais frequente / nomes.json). O `serie` cru do episódio é o do arquivo
// e pode vir em MAIÚSCULAS ('FUTURAMA') quando só aquele arquivo está assim.
let nomesDeSerie = {};
function cartaoFila(i) {
  const f = i.filme;
  const ep = f.ehEpisodio ? (f.temporada != null ? 'T' + f.temporada + ' · ' : '') + codigoEp(f) : (f.anoLancamento || '');
  const sub = i.motivo === 'proximo' ? 'A seguir · ' + ep : ep;
  const pct = i.motivo !== 'proximo' && f.posicaoSegundos && f.duracaoSegundos ? Math.min(100, 100 * f.posicaoSegundos / f.duracaoSegundos) : 0;
  return `<button class="card" onclick="enviarParaTv(${f.id})"><b>${esc(f.ehEpisodio && f.serie ? (nomesDeSerie[f.serieChave] || f.serie) : f.titulo)}</b><small>${esc(sub)}</small>` +
         (pct ? `<div class="fio"><i style="width:${pct.toFixed(0)}%"></i></div>` : '') + '</button>';
}

// ─── Série (linha) -> temporada (cartão) -> episódios. O estado aberto/fechado é o da sanfona do util.js (uma série e uma temporada por vez);
//     os .grupo[data-serie|data-temp] + .collapsed são o que aplicarSanfona()/cliqueSerie()/cliqueTemporada() do util.js manipulam. ───
function pilulasHtml(chave, n) {
  const p = (prefsTemporadas[chave] || {})[n] || {};
  const pil = (campo, icone, rotulo) =>
    `<button class="pill" role="switch" aria-checked="${!!p[campo]}" data-sw="${campo}" data-serie="${attr(chave)}" data-temp="${n}">${ico(icone)}${rotulo}</button>`;
  return `<div class="pills">${pil('pularAbertura', 'skip', 'Pular abertura')}${pil('proximoAutomatico', 'next', 'Próximo auto')}</div>`;
}

function tagPadraoHtml(chave) {
  const p = padroes[chave];
  if (!p) return '';
  const partes = [];
  if (p.audio) partes.push(p.audio.toUpperCase());
  if (p.legenda) partes.push('CC ' + (p.legenda === 'off' ? 'OFF' : p.legenda.toUpperCase()));
  return `<span class="tag" data-tag="${attr(chave)}">${partes.join(' · ')}</span>`;
}
function atualizarTags() {
  document.querySelectorAll('.grupo[data-serie]').forEach(g => {
    const chave = g.getAttribute('data-serie'), hd = g.querySelector(':scope > .serie');
    hd.querySelector('.tag')?.remove();
    const html = tagPadraoHtml(chave);
    if (html) hd.querySelector('[data-pref-serie]').insertAdjacentHTML('beforebegin', html);
  });
}

function serieHtmlControle(s) {
  const grupos = agruparPorTemporada(s.episodios);
  const aberta = serieEstaAberta(s.chave);
  const eps = contagemEpisodios(s.episodios.length);
  const sub = grupos ? `${grupos.length} temporadas · ${eps}` : eps;
  let corpo;
  if (!grupos) {   // uma temporada só (ou só "Especiais"/"Outros"): o cartão já vem aberto, sem o nível de temporada
    const t = s.episodios[0] && s.episodios[0].temporada;
    corpo = `<div class="temp"><div class="temp-hd fixo"><b>${t > 0 ? 'Temporada ' + t : 'Episódios'}</b><small>${eps}</small></div>` +
      (t > 0 ? pilulasHtml(s.chave, t) : '') + s.episodios.map(linhaEp).join('') + '</div>';
  } else {
    corpo = grupos.map(g => {
      const vistos = textoVistos(g.episodios);
      return `<div class="grupo temp${temporadaEstaAberta(g.chave) ? '' : ' collapsed'}" data-temp="${attr(g.chave)}">
        <div class="temp-hd" onclick="cliqueTemporada(this)">${ico('caret', 'cv')}<b>${esc(g.rotulo)}</b><small>${contagemEpisodios(g.episodios.length)}${vistos ? ' · ' + vistos : ''}</small></div>
        <div class="grupo-body">${g.numero > 0 ? pilulasHtml(s.chave, g.numero) : ''}${g.episodios.map(linhaEp).join('')}</div></div>`;
    }).join('');
  }
  return `<div class="grupo${aberta ? '' : ' collapsed'}" data-serie="${attr(s.chave)}">
    <div class="serie" onclick="cliqueSerie(this)">${ico('caret', 'cv')}
      <div class="t"><b>${esc(s.nome)}</b><small>${sub}</small></div>${tagPadraoHtml(s.chave)}
      <button class="ib" data-pref-serie="${attr(s.chave)}" aria-label="Idioma e legenda padrão de ${attr(s.nome)}">${ico('dots')}</button>
    </div>
    <div class="grupo-body">${corpo}</div></div>`;
}

async function toggleAssistido(id) {
  await fetch(`${API}/${id}/assistido`, { method: 'PUT' });
  render();  // re-busca já refletindo a mudança — sem cache local pra remendar
}

// Filtro e agrupamento vêm prontos de /api/filmes/tela (mesma fonte do index.html e do
// tv.html) — esta tela só desenha. Sem filtro de "visto" nem busca aqui (essa tela é o
// controle remoto simplificado), então só manda o tipo.
async function render() {
  const grid = document.getElementById('grid');
  const msg = document.getElementById('msg');
  const params = new URLSearchParams({ tipo: filtroTipo, visto: 'all' });
  if (filtroDublados) params.set('dublagem', 'qualquer');
  const [t, fila] = await Promise.all([
    fetchJsonOuNull(`${API}/tela?${params}`),
    filtroTipo === 'all' && !filtroDublados ? fetchJsonOuNull(`${API}/fila`) : null,
  ]);

  if (!t) { msg.style.display = 'block'; msg.textContent = 'Falha ao carregar a lista.'; return; }

  if (t.series.length) {
    [padroes, prefsTemporadas] = await Promise.all([
      fetchJsonOuNull('/api/series/padroes').then(m => m || {}),
      fetchJsonOuNull('/api/series/temporadas').then(m => m || {}),
    ]);
  }

  nomesDeSerie = Object.fromEntries(t.series.map(s => [s.chave, s.nome]));
  let html = '';
  if (fila && fila.length) {
    // "proximo": o episódio seguinte de uma série vista há pouco; o cartão mostra o nome da série porque está fora do grupo dela
    html += `<div class="sec">Continuar assistindo</div><div class="cont">${fila.map(cartaoFila).join('')}</div>`;
  }

  // Essa tela não distingue "pasta de filme+extras" de filme solto — lista tudo junto,
  // ordenado por título (o index.html completo é que mostra a separação por pasta).
  const soltos = [...t.filmesSoltos, ...t.pastasFilme.flatMap(p => p.itens)]
    .sort((a, b) => a.titulo.localeCompare(b.titulo));
  if (soltos.length) html += `<div class="sec">Filmes · ${soltos.length}</div><div class="filmes">${soltos.map(linhaFilme).join('')}</div>`;

  if (t.series.length) html += `<div class="sec">Séries · ${t.series.length}</div>` + t.series.map(serieHtmlControle).join('');

  msg.style.display = html ? 'none' : 'block';
  if (!html) msg.textContent = t.catalogoVazio ? 'Nenhum filme. Rode o Scan na lista completa.' : (filtroTipo === 'novela' ? 'Nenhuma novela ainda. Coloque os capítulos em novelas/Nome da novela/.' : 'Nada nesse filtro.');
  grid.innerHTML = html;
}

// ─── Controle remoto (TV) — mesmo código que existia no index.html ────────
let ultimoEstadoRemoto = null;
let remoteTimer = null;
let filmeAtualRemoto = null;  // cache do filme tocando agora — só busca de novo quando muda

function mmss(s) {
  s = Math.max(0, Math.round(s || 0));
  const h = Math.floor(s / 3600), m = Math.floor((s % 3600) / 60), sec = s % 60;
  return (h ? h + ':' + pad2(m) : String(m)) + ':' + pad2(sec);
}

async function enviarParaTv(id) {
  // aquece o remux deste episódio e do seguinte sem esperar: a TV só pede quando carrega o vídeo
  fetch(`${API}/${id}/pre-aquecer`, { method: 'POST' }).catch(() => {});
  await fetch(`${PLAYER_API}/selecionar/${id}`, { method: 'POST' });
  pollRemote();
}

// Modo embutido: pede ao index.html (a página de fora) que feche a folha.
function fecharEmbed(parou) { try { parent.postMessage({ tipo: 'fechar-controle', parou: !!parou }, location.origin); } catch (e) { /* sem pai: nada a fechar */ } }

// Modo embutido: a capa (pôster, nome e sinopse) do que toca, ou o aviso de TV parada.
function atualizarCapa() {
  if (!EMBED) return;
  const f = filmeAtualRemoto, capa = document.getElementById('embedCapa');
  document.getElementById('embedOcioso').classList.toggle('on', !f);
  capa.classList.toggle('off', !f);
  if (!f) return;
  const img = document.getElementById('capaImg');
  img.style.display = f.posterUrl ? '' : 'none';
  if (f.posterUrl) img.src = pw(f.posterUrl, 500);
  document.getElementById('capaTit').textContent = f.ehEpisodio && f.serie ? f.serie : f.titulo;
  document.getElementById('capaSub').textContent = f.ehEpisodio
    ? [f.temporada != null ? 'T' + f.temporada : '', codigoEp(f), f.tituloEpisodio].filter(Boolean).join(' · ')
    : [f.anoLancamento, f.tituloOriginal && f.tituloOriginal !== f.titulo ? f.tituloOriginal : ''].filter(Boolean).join(' · ');
  document.getElementById('capaSin').textContent = f.sinopse || '';
}

// Padrões da série e interruptores da temporada: a lista os carrega junto do /tela; no modo embutido (sem lista) vêm daqui.
async function carregarPadroes() {
  [padroes, prefsTemporadas] = await Promise.all([
    fetchJsonOuNull('/api/series/padroes').then(m => m || {}),
    fetchJsonOuNull('/api/series/temporadas').then(m => m || {}),
  ]);
}

// ─── Fila manual "tocar a seguir" (o servidor guarda; a TV toca o primeiro ao fim do que passa) ───
const titulosDaFila = {};   // id -> { linha, sub } (buscado uma vez)
let filaAtual = [];

async function enfileirar(id) {
  if (!ultimoEstadoRemoto || ultimoEstadoRemoto.filmeId === null) { enviarParaTv(id); return; }   // nada tocando: não há "a seguir", só toca
  const r = await fetch(`${PLAYER_API}/fila/${id}`, { method: 'POST' }).catch(() => null);
  if (!r || !r.ok) { toast(r && r.status === 409 ? 'A fila está cheia.' : 'Não consegui pôr na fila.'); return; }
  const j = await r.json();
  const f = await descreverDaFila(id);
  toast(j.resultado === 'JaNaFila' ? 'Já está na fila.' : `Toca a seguir: ${f.linha}`,
        j.resultado === 'JaNaFila' ? null : { rotulo: 'Desfazer', fazer: () => tirarDaFila(id) });
  pollRemote();
}

async function descreverDaFila(id) {
  if (titulosDaFila[id]) return titulosDaFila[id];
  const f = await fetchJsonOuNull(`${API}/${id}`);
  const linha = !f ? 'Item ' + id : f.ehEpisodio ? `${nomesDeSerie[f.serieChave] || f.serie} · ${codigoEp(f)}` : f.titulo;
  return (titulosDaFila[id] = { linha });
}

async function tirarDaFila(id) {
  await fetch(`${PLAYER_API}/fila/${id}`, { method: 'DELETE' }).catch(() => {});
  await pollRemote();
  if (folhaFilaAberta()) desenharFila();
}

async function limparFila() {
  await fetch(`${PLAYER_API}/fila`, { method: 'DELETE' }).catch(() => {});
  await pollRemote();
  fecharFila();
}

const folhaFilaAberta = () => document.getElementById('filaFolha').classList.contains('on');
function abrirFila() {
  document.getElementById('filaScrim').classList.add('on');
  const f = document.getElementById('filaFolha');
  f.classList.add('on');
  desenharFila();
  f.focus({ preventScroll: true });
}
function fecharFila() {
  document.getElementById('filaScrim').classList.remove('on');
  document.getElementById('filaFolha').classList.remove('on');
}
document.addEventListener('keydown', e => { if (e.key === 'Escape' && folhaFilaAberta()) fecharFila(); });

async function desenharFila() {
  const corpo = document.getElementById('filaCorpo');
  if (!filaAtual.length) { corpo.innerHTML = '<div class="vazio">A fila está vazia.</div>'; return; }
  const itens = await Promise.all(filaAtual.map(descreverDaFila));
  corpo.innerHTML = filaAtual.map((id, i) =>
    `<div class="fila-lin"><span class="n">${i + 1}</span><span class="t">${esc(itens[i].linha)}</span>` +
    `<button aria-label="Tirar da fila" onclick="tirarDaFila(${id})">${ico('close')}</button></div>`).join('') +
    '<button class="op mais" onclick="limparFila()"><span>Limpar a fila</span></button>';
}

// Chamado a cada poll: botão da barra (com contador), lista aberta e o "próximo" valendo mesmo sem próximo episódio.
let filaTamanhoAnterior = 0;
function tratarFila(estado) {
  filaAtual = estado.fila || [];
  const btn = document.getElementById('remoteFilaBtn'), n = document.getElementById('remoteFilaN');
  btn.style.display = filaAtual.length ? '' : 'none';
  n.textContent = filaAtual.length; n.style.display = filaAtual.length ? 'grid' : 'none';
  if (folhaFilaAberta()) desenharFila();
  if (filaAtual.length) { els.remoteProximoEpBtn.style.display = ''; els.remoteProximoEpBtn.disabled = false; }
  else if (filaTamanhoAnterior && estado.filmeId !== null) atualizarBotaoProximoEp(estado.filmeId);   // esvaziou: volta a depender do próximo episódio
  filaTamanhoAnterior = filaAtual.length;
}

async function remotePlayPause() {
  await fetch(`${PLAYER_API}/play-pause`, { method: 'POST' });
  pollRemote();
}

async function remoteSeek(delta) {
  await fetch(`${PLAYER_API}/seek`, opcoesJson('POST', { delta }));
  pollRemote();
}

async function remoteScrub(e) {
  const dur = (ultimoEstadoRemoto && ultimoEstadoRemoto.duracaoSegundos) || 0;
  const bar = document.getElementById('remoteProg').getBoundingClientRect();
  if (!dur || !bar.width) return;
  const frac = Math.max(0, Math.min(1, (e.clientX - bar.left) / bar.width));
  await fetch(`${PLAYER_API}/seek-abs`, opcoesJson('POST', { pos: frac * dur }));
  pollRemote();
}

// Botão da direita da barra, contextual: "30s »" normalmente; "Pular abertura" (vermelho) dentro da abertura conhecida; "Próximo episódio"
// (vermelho) a partir do início dos créditos conhecidos, se há próximo. O servidor acha abertura e créditos em segundo plano (404 = ainda não achou).
let remoteAbertura = null;
let remoteCreditos = null;       // { inicioSegundos } ou null
let remoteTemProximo = false;
async function remoteCarregarAbertura(id) {
  remoteAbertura = await fetchJsonOuNull(`${API}/${id}/abertura`);
}
async function remoteCarregarCreditos(id) {
  remoteCreditos = await fetchJsonOuNull(`${API}/${id}/creditos`);
}
// as regras puras moram em js/player-regras.js (compartilhadas com o player local do index.html)
function dentroDaAbertura(pos) { return aberturaContem(remoteAbertura, pos); }
function nosCreditos(pos, dur) { return creditosAlcancados(remoteCreditos, remoteTemProximo, pos, dur); }
function pintarFaixa(id, ini, fim, dur) {
  const el = document.getElementById(id), f = faixaEmPercentual(ini, fim, dur);
  el.style.display = f ? 'block' : 'none';
  if (f) { el.style.left = f.left + '%'; el.style.width = f.width + '%'; }
}
// O botão contextual só é redesenhado quando muda de forma (não a cada poll): neutro "30s »", vermelho "Pular abertura" / "Próximo episódio".
let ctaModo = null;
function definirCta(modo) {
  if (ctaModo === modo) return;
  ctaModo = modo;
  const b = document.getElementById('remoteDireitaBtn');
  b.className = 'cta' + (modo === 'neutro' ? ' neutro' : '');
  b.innerHTML = modo === 'abertura' ? ico('skip', 'f') + 'Pular abertura' : modo === 'proximo' ? ico('nextF', 'f') + 'Próximo episódio' : '30s ' + ico('skip');
  b.setAttribute('aria-label', modo === 'abertura' ? 'Pular abertura' : modo === 'proximo' ? 'Próximo episódio' : 'Avançar 30 segundos');
}
function atualizarBotaoDireita(pos, dur) {
  const pular = dentroDaAbertura(pos), proximo = !pular && nosCreditos(pos, dur);
  definirCta(pular ? 'abertura' : proximo ? 'proximo' : 'neutro');
  pintarFaixa('remoteFaixaAbertura', remoteAbertura && remoteAbertura.inicioSegundos, remoteAbertura && remoteAbertura.fimSegundos, dur);
  pintarFaixa('remoteFaixaCreditos', creditosPlausiveis(remoteCreditos, dur) ? remoteCreditos.inicioSegundos : null, dur, dur);
}
function remoteDireita() {
  const pos = (ultimoEstadoRemoto && ultimoEstadoRemoto.posSegundos) || 0;
  if (dentroDaAbertura(pos)) return remotePularAbertura();
  if (nosCreditos(pos, (ultimoEstadoRemoto && ultimoEstadoRemoto.duracaoSegundos) || 0)) return remoteProximoEp();
  return remoteSeek(SEEK_STEP_S);
}
// O servidor pula (vai pro fim da abertura que ele conhece) e liga o pulo automático da temporada: o Daniel confirmou que ali é abertura.
async function remotePularAbertura() {
  const res = await fetch(`${PLAYER_API}/pular-abertura`, { method: 'POST' }).catch(() => null);
  if (!res || !res.ok) toast('Não achei a abertura deste episódio.');
  pollRemote();
}

async function remoteVolume(valor) {
  document.getElementById('remoteVolume').style.setProperty('--v', (100 * valor) + '%');
  await fetch(`${PLAYER_API}/volume`, opcoesJson('POST', { valor: Number(valor) }));
}

// ■ Parar: manda parar e, com a TV parada, FECHA o controle e volta pra biblioteca (embutido: fecha a folha no index; direto: vai pra "/").
// Se o parar falhar (TV/servidor fora), avisa e fica aqui.
async function remoteParar() {
  const res = await fetch(`${PLAYER_API}/parar`, { method: 'POST' }).catch(() => null);
  if (!res || !res.ok) { toast('Não consegui parar a TV. Tente de novo.'); return; }
  if (EMBED) fecharEmbed(true);
  else location.href = '/';
}

// A TV mesma oferece o próximo episódio (com contagem regressiva) ao terminar um — isso só
// confirma antes da hora, pelo mesmo caminho que o OK local do D-pad usaria.
async function remoteAceitarProximo() {
  await fetch(`${PLAYER_API}/proximo-aceita`, { method: 'POST' });
  pollRemote();
}

// Botão manual "próximo episódio" — diferente da oferta automática (remoteAceitarProximo):
// pula na hora, sem esperar a TV chegar perto do fim e abrir a contagem regressiva sozinha.
// O servidor toca o próximo e, se foi apertado no fim do episódio, liga o próximo automático da temporada (e avisa).
async function remoteProximoEp() {
  const res = await fetch(`${PLAYER_API}/proximo-manual`, { method: 'POST' }).catch(() => null);
  if (!res || !res.ok) { toast('Não há próximo episódio.'); return; }
  const { proximoId } = await res.json();
  fetch(`${API}/${proximoId}/pre-aquecer`, { method: 'POST' }).catch(() => {});   // adianta o remux enquanto a TV carrega
  pollRemote();
}

// Mostra o botão pra qualquer episódio, mas só habilita se REALMENTE existe um próximo --
// sem isso, no último episódio da série o botão ficava visível e clicável sem fazer nada
// (silencioso, parecia quebrado em vez de "já é o último").
async function atualizarBotaoProximoEp(filmeId) {
  const btn = els.remoteProximoEpBtn;
  if (!filmeAtualRemoto || !filmeAtualRemoto.ehEpisodio) { btn.style.display = 'none'; remoteTemProximo = false; return; }
  btn.style.display = '';
  btn.disabled = true;
  const prox = await fetch(`${API}/${filmeId}/proximo`)
    .then(r => r.status === 200 ? r.json() : null).catch(() => null);
  if (filmeId === ultimoEstadoRemoto?.filmeId) { btn.disabled = !prox; remoteTemProximo = !!prox; }
}

// ─── Faixas do que está tocando e padrões por série ─────────────────────────
// Só recarrega quando pollRemote detecta troca de filme. `idx` de áudio é o GLOBAL do ffprobe (bate com -map);
// o de legenda é relativo. Trocar a dublagem manda a TV regerar o HLS/remux (não é instantâneo).
let remoteLegendas = [];
let remoteAudios = [];
let padroes = {};            // chave da série -> { audio, legenda }
let prefsTemporadas = {};    // chave da série -> { "1": { pularAbertura, proximoAutomatico } }

async function remoteCarregarLegendas(id) {
  try { remoteLegendas = (await fetch(`${API}/${id}/legendas`).then(r => r.json())).filter(x => x.convertivel); }
  catch { remoteLegendas = []; }
  if (folhaAberta()) desenharFolha();
}
async function remoteCarregarAudios(id) {
  remoteAudios = [];   // as faixas do filme anterior não valem pro novo (o botão "Original ⇄ Dublagem IA" sairia errado por um instante)
  try { remoteAudios = await fetch(`${API}/${id}/audios`).then(r => r.json()); }
  catch { remoteAudios = []; }
  if (folhaAberta()) desenharFolha();
}

// ─── Dublagem por IA: botão "Original ⇄ Dublagem IA" da barra (usa as faixas de /audios e o mesmo POST /api/player/audio da folha) ───
// "Original" = a faixa que não é IA e não é português (o áudio de origem); sem ela, qualquer faixa que não seja IA.
const ehPortugues = a => ['por', 'pt', 'pob'].includes(String(a.idioma || '').toLowerCase());
const faixaIa = () => remoteAudios.find(a => a.dubladoIa);
const faixaOriginal = () => remoteAudios.find(a => !a.dubladoIa && !ehPortugues(a)) || remoteAudios.find(a => !a.dubladoIa);
let chaveLinhaIa = null;   // só redesenha quando muda (o poll roda a cada segundo)

function atualizarLinhaIa() {
  const ia = ultimoEstadoRemoto && ultimoEstadoRemoto.filmeId !== null ? faixaIa() : null;
  const orig = ia ? faixaOriginal() : null;
  const chave = ia ? `${ia.idx}|${ia.atual}|${orig ? orig.idx : ''}` : '';
  if (chave === chaveLinhaIa) return;
  chaveLinhaIa = chave;
  document.getElementById('remoteIa').classList.toggle('on', !!ia);
  const btn = document.getElementById('remoteIaBtn');
  btn.style.display = orig ? '' : 'none';   // só a faixa IA no arquivo: fica o selo, sem o que alternar
  if (!ia) return;
  btn.innerHTML = `<span class="${ia.atual ? '' : 'on'}">Original</span><i aria-hidden="true">⇄</i><span class="${ia.atual ? 'on ia' : ''}">Dublagem IA</span>`;
  btn.setAttribute('aria-label', ia.atual ? 'Dublagem IA ligada. Tocar para voltar ao áudio original' : 'Áudio original ligado. Tocar para ouvir a dublagem IA');
}

// Troca a faixa de áudio da TV (a TV regera o HLS/remux — não é instantâneo). Mesmo caminho da folha "Idioma e legenda".
async function remoteTrocarAudio(idx) {
  await fetch(`${PLAYER_API}/audio`, opcoesJson('POST', { idx }));
  remoteAudios = remoteAudios.map(a => ({ ...a, atual: a.idx === idx }));
  if (padroes[filmeAtualRemoto?.serieChave]) await gravarEscolhaComoPadraoDaSerie();
  atualizarLinhaIa();
  if (folhaAberta()) desenharFolha();
  pollRemote();
}

async function remoteAlternarIa() {
  const ia = faixaIa();
  const alvo = ia && (ia.atual ? faixaOriginal() : ia);
  if (!alvo) return;
  toast(alvo === ia ? 'Trocando para a dublagem IA…' : 'Voltando ao áudio original…');
  await remoteTrocarAudio(alvo.idx);
}

const NOME_IDIOMA = { por: 'Português', eng: 'Inglês', spa: 'Espanhol', fra: 'Francês', deu: 'Alemão', ita: 'Italiano', jpn: 'Japonês' };

document.getElementById('grid').addEventListener('click', async e => {
  const chip = e.target.closest('[data-pref-serie]');
  if (chip) { e.stopPropagation(); abrirFolhaSerie(chip.dataset.prefSerie); return; }
  const sw = e.target.closest('.pill[data-sw]');
  if (!sw) return;
  e.stopPropagation();   // (captura) o clique não chega ao cabeçalho da série/temporada, que abriria/fecharia
  const { serie, temp, sw: campo } = sw.dataset;
  definirInterruptor(serie, temp, campo, sw.getAttribute('aria-checked') !== 'true');
}, true);

async function definirInterruptor(serie, temp, campo, novo) {
  if (!await salvarInterruptorDaTemporada(serie, temp, campo, novo)) { toast('Não consegui salvar. Tente de novo.'); return false; }
  ((prefsTemporadas[serie] ||= {})[temp] ||= {})[campo] = novo;
  pintarInterruptores();
  return true;
}

function pintarInterruptores() {
  document.querySelectorAll('.pill[data-sw]').forEach(b => {
    b.setAttribute('aria-checked', !!(((prefsTemporadas[b.dataset.serie] || {})[b.dataset.temp] || {})[b.dataset.sw]));
  });
}

// O servidor liga sozinho (aprendeu com o pulo / botão Pular abertura): relê e repinta os interruptores.
async function recarregarInterruptores() {
  prefsTemporadas = (await fetchJsonOuNull('/api/series/temporadas')) || prefsTemporadas;
  pintarInterruptores();
}

// ─── avisos do servidor ("Abertura pulada · voltar", "Abertura aprendida… · desligar") ───
let ultimoAviso = null;   // null = ainda não vi o 1º estado (não mostra aviso velho ao abrir a página)
let instanciaVista = null;   // identifica o processo do servidor; muda a cada restart e zera os contadores dele (versão do aviso)
function tratarAviso(aviso, instancia) {
  if (instancia && instancia !== instanciaVista) {
    const reinicio = instanciaVista !== null;
    instanciaVista = instancia;
    if (reinicio) ultimoAviso = 0;   // servidor novo: os avisos recomeçam da versão 1
  }
  if (ultimoAviso === null) { ultimoAviso = aviso ? aviso.versao : 0; return; }
  if (!aviso || aviso.versao <= ultimoAviso) return;
  ultimoAviso = aviso.versao;
  const f = filmeAtualRemoto, ab = ultimoEstadoRemoto && ultimoEstadoRemoto.abertura;
  const acoes = {
    voltar: ab && { rotulo: 'Voltar', fazer: () => fetch(`${PLAYER_API}/seek-abs`, opcoesJson('POST', { pos: ab.inicio })).then(pollRemote) },
    desligar: f && f.serieChave && { rotulo: 'Desligar', fazer: () => definirInterruptor(f.serieChave, f.temporada, 'pularAbertura', false) },
    'desligar-proximo': f && f.serieChave && { rotulo: 'Desligar', fazer: () => definirInterruptor(f.serieChave, f.temporada, 'proximoAutomatico', false) },
    cancelar: { rotulo: 'Cancelar', fazer: () => fetch(`${PLAYER_API}/proximo-cancelar`, { method: 'POST' }).then(pollRemote) },
  };
  toast(aviso.texto, acoes[aviso.acao] || null);
  if (aviso.tipo === 'abertura' || aviso.tipo === 'proximo') recarregarInterruptores();
}

async function salvarPadrao(chave, tipo, valor) {   // tipo: 'audio' | 'legenda'
  const r = await fetch(`/api/series/${encodeURIComponent(chave)}/${tipo}`, opcoesJson('PUT', { idioma: valor || null })).catch(() => null);
  if (!r || !r.ok) { toast('Não consegui salvar o padrão da série.'); return false; }
  const p = padroes[chave] ||= {};
  if (valor) p[tipo] = valor; else delete p[tipo];
  if (!p.audio && !p.legenda) delete padroes[chave];
  atualizarTags();
  return true;
}

// Avisos curtos acima da barra (nada de confirm()/alert()); somem sozinhos.
let toastTimer = null;
function toast(texto, acao) {
  document.getElementById('toast')?.remove();
  const el = document.createElement('div');
  el.className = 'toast'; el.id = 'toast'; el.setAttribute('role', 'status');
  el.innerHTML = `<span>${esc(texto)}</span>` + (acao ? `<button>${esc(acao.rotulo)}</button>` : '');
  if (acao) el.querySelector('button').onclick = () => { el.remove(); acao.fazer(); };
  document.body.appendChild(el);
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => el.remove(), 8000);
}

// ─── Folha "Idioma e legenda": abre ao tocar no título (o que está tocando) ou no ⋯ de uma série (padrão da série) ───
let folha = null;   // { modo: 'agora', todas } | { modo: 'serie', chave, nome }
const folhaAberta = () => document.getElementById('folha').classList.contains('on');
const LEGENDAS_VISIVEIS = 4;

function abrirFolhaAgora() { if (!filmeAtualRemoto) return; folha = { modo: 'agora', todas: false }; mostrarFolha(); }
function abrirFolhaSerie(chave, nomeDado) {
  const nome = nomeDado || document.querySelector(`.grupo[data-serie="${CSS.escape(chave)}"] .serie b`)?.textContent?.trim() || 'série';
  folha = { modo: 'serie', chave, nome };
  mostrarFolha();
  carregarTemporadasDaSerie(chave);
}

// Temporadas de uma série (pros interruptores "Pular abertura / Próximo automático" de cada uma): vêm do /tela, uma vez por série.
const temporadasDaSerie = {};
async function carregarTemporadasDaSerie(chave) {
  if (temporadasDaSerie[chave]) return;
  const t = await fetchJsonOuNull(`${API}/tela?tipo=serie&visto=all`);
  const s = t && t.series.find(x => x.chave === chave);
  temporadasDaSerie[chave] = s ? [...new Set(s.episodios.map(e => e.temporada).filter(n => n > 0))].sort((a, b) => a - b) : [];
  if (folha && folha.modo === 'serie' && folha.chave === chave && folhaAberta()) desenharFolha();
}

// Interruptores de uma temporada (a mesma regra e rota da lista: PUT /api/series/{chave}/temporadas/{n}).
function interruptoresTemporadaHtml(chave, n) {
  const p = (prefsTemporadas[chave] || {})[n] || {};
  const lin = (campo, rotulo, sub) => `<div class="lin" style="margin-top:8px;padding-top:10px"><div id="sw-${campo}-${n}">${rotulo}<small>${sub}</small></div>
    <button class="sw" role="switch" aria-checked="${!!p[campo]}" aria-labelledby="sw-${campo}-${n}" data-fo="sw-temp" data-serie="${attr(chave)}" data-temp="${n}" data-campo="${campo}"></button></div>`;
  return lin('pularAbertura', 'Pular abertura', 'Vai direto para o fim da abertura') + lin('proximoAutomatico', 'Próximo episódio automático', 'Nos créditos, com contagem de 5 s');
}
function mostrarFolha() {
  document.getElementById('folhaScrim').classList.add('on');
  const f = document.getElementById('folha');
  f.classList.add('on');
  desenharFolha();
  f.focus({ preventScroll: true });
}
function fecharFolha() {
  document.getElementById('folhaScrim').classList.remove('on');
  document.getElementById('folha').classList.remove('on');
}
document.addEventListener('keydown', e => { if (e.key === 'Escape' && folhaAberta()) fecharFolha(); });

const segOpcao = (grupo, valor, rotulo, ligada) =>
  `<button role="radio" aria-checked="${ligada}" class="${ligada ? 'on' : ''}" data-fo="${grupo}" data-v="${attr(String(valor))}">${esc(rotulo)}</button>`;
const linhaOpcao = (grupo, valor, rotulo, ligada) =>
  `<button class="op" role="radio" aria-checked="${ligada}" data-fo="${grupo}" data-v="${attr(String(valor))}"><span>${esc(rotulo)}</span>${ligada ? ico('check') : ''}</button>`;

// Rótulos em português (nomeDoIdioma); se duas faixas dão o mesmo nome, o título da faixa desempata ("Português · SDH").
function rotulosDasFaixas(faixas) {
  const nomes = faixas.map(x => nomeDoIdioma(x.idioma, x.titulo));
  return faixas.map((x, i) => nomes.indexOf(nomes[i]) !== nomes.lastIndexOf(nomes[i]) && x.titulo && !nomes[i].includes(x.titulo) ? nomes[i] + ' · ' + x.titulo : nomes[i]);
}

function desenharFolha() {
  const corpo = document.getElementById('folhaCorpo');
  const sub = document.getElementById('folhaSub');
  if (folha.modo === 'serie') {
    const p = padroes[folha.chave] || {};
    sub.textContent = folha.nome;
    document.getElementById('folhaTitulo').textContent = 'Padrões da série';
    const temps = temporadasDaSerie[folha.chave];
    const blocoTemps = !temps ? '<div class="vazio">Carregando temporadas…</div>'
      : temps.map(n => `<div class="rot">Temporada ${n}</div>${interruptoresTemporadaHtml(folha.chave, n)}`).join('');
    corpo.innerHTML =
      '<div class="rot">Áudio</div><div class="seg2" role="radiogroup" aria-label="Áudio">' +
        [['', 'Automático'], ['por', 'Português'], ['eng', 'Inglês']].map(([v, t]) => segOpcao('s-audio', v, t, (p.audio || '') === v)).join('') +
      '</div><div class="rot">Legenda</div><div role="radiogroup" aria-label="Legenda">' +
        [['', 'Automática'], ['off', 'Desligada'], ['por', 'Português'], ['eng', 'Inglês']].map(([v, t]) => linhaOpcao('s-legenda', v, t, (p.legenda || '') === v)).join('') +
      '</div>' + blocoTemps;
    return;
  }

  document.getElementById('folhaTitulo').textContent = 'Idioma e legenda';
  const f = filmeAtualRemoto;
  sub.textContent = f ? (f.ehEpisodio ? [f.serie, f.temporada != null ? 'T' + f.temporada : '', codigoEp(f)].filter(Boolean).join(' · ') : f.titulo) : '';

  // áudio: controle segmentado (português, inglês, espanhol, resto em ordem alfabética)
  const audios = ordenarPorIdioma(remoteAudios, a => ({ idioma: a.idioma, titulo: a.titulo }));
  const rotAudios = rotulosDasFaixas(audios).map((r, i) => audios[i].dubladoIa ? nomeDoIdioma(audios[i].idioma, audios[i].titulo) + ' (IA)' : r);
  let html = '<div class="rot">Áudio</div>' + (audios.length > 1
    ? '<div class="seg2" role="radiogroup" aria-label="Áudio">' + audios.map((a, i) => segOpcao('audio', a.idx, rotAudios[i], !!a.atual)).join('') + '</div>'
    : `<div class="vazio">${audios.length ? esc(rotAudios[0]) + ' (única faixa de áudio)' : 'Sem faixas de áudio.'}</div>`);

  // legenda: lista de linhas (4 visíveis + "Mais N idiomas")
  const legAtual = ultimoEstadoRemoto ? ultimoEstadoRemoto.legendaIdx : -1;
  const legs = ordenarPorIdioma(remoteLegendas, l => ({ idioma: l.idioma, titulo: l.titulo }));
  const rotLegs = rotulosDasFaixas(legs);
  html += '<div class="rot">Legenda</div>';
  if (!legs.length) html += '<div class="vazio">Este arquivo não tem legenda.</div>';
  else {
    const linhas = [['-1', 'Desligada', legAtual === -1], ...legs.map((l, i) => [String(l.idx), rotLegs[i], legAtual === l.idx])];
    const escolhida = linhas.findIndex(l => l[2]);
    const todas = folha.todas || linhas.length <= LEGENDAS_VISIVEIS + 1 || escolhida >= LEGENDAS_VISIVEIS;   // a escolhida nunca fica escondida
    const visiveis = todas ? linhas : linhas.slice(0, LEGENDAS_VISIVEIS);
    html += '<div role="radiogroup" aria-label="Legenda">' + visiveis.map(l => linhaOpcao('legenda', l[0], l[1], l[2])).join('') + '</div>';
    if (!todas) html += `<button class="op mais" data-fo="mais"><span>Mais ${linhas.length - LEGENDAS_VISIVEIS} idiomas</span>${ico('caret').replace('<path d="M9 6l6 6l-6 6"/>', '<path d="M6 9l6 6l6-6"/>')}</button>`;
  }

  // aspecto da legenda na TV: tamanho e posição (ajuste do aparelho, vale pra qualquer filme)
  const estLeg = ultimoEstadoRemoto || {};
  const tamLeg = estLeg.legendaTamanho || 'm', posLeg = estLeg.legendaPosicao || 'baixa';
  html += '<div class="rot">Tamanho da legenda</div><div class="seg2" role="radiogroup" aria-label="Tamanho da legenda">' +
    [['p', 'Pequena'], ['m', 'Média'], ['g', 'Grande']].map(([k, t]) => segOpcao('leg-tam', k, t, tamLeg === k)).join('') + '</div>' +
    '<div class="rot">Posição da legenda</div><div class="seg2" role="radiogroup" aria-label="Posição da legenda">' +
    [['baixa', 'Baixa'], ['alta', 'Mais alta']].map(([k, t]) => segOpcao('leg-pos', k, t, posLeg === k)).join('') + '</div>';

  if (f && f.ehEpisodio && f.serieChave) {
    const ligado = !!padroes[f.serieChave];
    html += `<div class="lin"><div id="rotUsarSerie">Usar em toda a série<small>Áudio e legenda como padrão</small></div>
      <button class="sw" role="switch" aria-checked="${ligado}" aria-labelledby="rotUsarSerie" data-fo="usar-serie" data-v="${ligado ? 0 : 1}"></button></div>`;
  }
  if (f && f.ehEpisodio && f.serieChave) {
    // os padrões da série e as chaves da temporada que toca agora (o resto da série: "Padrões da série")
    if (f.temporada != null && f.temporada > 0) html += `<div class="rot">Temporada ${f.temporada}</div>${interruptoresTemporadaHtml(f.serieChave, f.temporada)}`;
    html += `<button class="op" style="margin-top:14px" data-fo="abrir-serie"><span>Padrões da série e temporadas</span>${ico('caret')}</button>`;
  }
  corpo.innerHTML = html;
}

// Grava, como padrão da série, o que está escolhido AGORA no que toca (áudio e legenda).
async function gravarEscolhaComoPadraoDaSerie() {
  const chave = filmeAtualRemoto && filmeAtualRemoto.serieChave;
  if (!chave) return;
  const audio = remoteAudios.find(a => a.atual);
  const legIdx = ultimoEstadoRemoto ? ultimoEstadoRemoto.legendaIdx : -1;
  const leg = remoteLegendas.find(l => l.idx === legIdx);
  if (audio && audio.idioma && NOME_IDIOMA[audio.idioma]) await salvarPadrao(chave, 'audio', audio.idioma);
  await salvarPadrao(chave, 'legenda', legIdx === -1 || !leg ? 'off' : (NOME_IDIOMA[leg.idioma] ? leg.idioma : null));
}

document.getElementById('folhaCorpo').addEventListener('click', async e => {
  const b = e.target.closest('[data-fo]');
  if (!b) return;
  const { fo, v } = b.dataset;
  if (fo === 'mais') folha.todas = true;
  else if (fo === 's-audio') await salvarPadrao(folha.chave, 'audio', v);
  else if (fo === 's-legenda') await salvarPadrao(folha.chave, 'legenda', v);
  else if (fo === 'audio') {
    const idx = Number(v);
    await fetch(`${PLAYER_API}/audio`, opcoesJson('POST', { idx }));
    remoteAudios = remoteAudios.map(a => ({ ...a, atual: a.idx === idx }));
    if (padroes[filmeAtualRemoto?.serieChave]) await gravarEscolhaComoPadraoDaSerie();
    pollRemote();
  } else if (fo === 'legenda') {
    const idx = Number(v);
    await fetch(`${PLAYER_API}/legenda`, opcoesJson('POST', { idx }));
    if (ultimoEstadoRemoto) ultimoEstadoRemoto = { ...ultimoEstadoRemoto, legendaIdx: idx };
    if (padroes[filmeAtualRemoto?.serieChave]) await gravarEscolhaComoPadraoDaSerie();
    pollRemote();
  } else if (fo === 'leg-tam' || fo === 'leg-pos') {
    const corpo = fo === 'leg-tam' ? { tamanho: v } : { posicao: v };
    await fetch(`${PLAYER_API}/legenda-estilo`, opcoesJson('POST', corpo));
    if (ultimoEstadoRemoto) ultimoEstadoRemoto = { ...ultimoEstadoRemoto, ...(fo === 'leg-tam' ? { legendaTamanho: v } : { legendaPosicao: v }) };
    pollRemote();
  } else if (fo === 'sw-temp') {
    const { serie, temp, campo } = b.dataset;
    await definirInterruptor(serie, temp, campo, b.getAttribute('aria-checked') !== 'true');
  } else if (fo === 'abrir-serie') {
    const f = filmeAtualRemoto;
    abrirFolhaSerie(f.serieChave, f.serie);
    return;
  } else if (fo === 'usar-serie') {
    const chave = filmeAtualRemoto.serieChave;
    if (v === '1') await gravarEscolhaComoPadraoDaSerie();
    else { await salvarPadrao(chave, 'audio', null); await salvarPadrao(chave, 'legenda', null); }
  }
  desenharFolha();
});

// ─── Sono ("Parar em 15 / 30 / 60 min", "ao fim do episódio") e "Ainda está assistindo?" ─────────────────────────────────────
// O servidor guarda tudo e devolve no /player/state: `sono` { ativo, restanteSegundos, aoFim, disparou, versao }, `presenca` { pendente, rotulo }. A TV aplica
// (para o player e mostra "Boa noite"; depois de N avanços sozinha pergunta e fica parada); aqui só se arma, se vê a contagem e se responde ao aviso.
IC.moon = '<path d="M20 14.5A8.5 8.5 0 1 1 9.5 4a7 7 0 0 0 10.5 10.5z"/>';
document.getElementById('remoteSonoBtn').innerHTML = ico('moon');
let sonoVisto = null;   // versão do último disparo que já vi (null = ainda não vi o 1º estado: não mostra "Boa noite" velho ao abrir a página)

const fimDoQue = () => (filmeAtualRemoto && !filmeAtualRemoto.ehEpisodio ? 'filme' : 'episódio');
function textoDoSono(s) {
  const partes = [];
  if (s.restanteSegundos != null) partes.push('Dormir em ' + mmss(s.restanteSegundos));
  if (s.aoFim) partes.push(partes.length ? 'ou ao fim do ' + fimDoQue() : 'Dormir ao fim do ' + fimDoQue());
  return partes.join(' · ');
}

// Chamado a cada poll: contagem regressiva e "Cancelar" na barra, o aviso "Ainda está assistindo?" e, uma vez, o "Boa noite" quando a TV para.
function tratarSono(estado) {
  const s = estado.sono, p = estado.presenca;
  if (!s) return;
  document.getElementById('remoteSono').classList.toggle('on', !!s.ativo);
  if (s.ativo) document.getElementById('remoteSonoTxt').textContent = textoDoSono(s);
  document.getElementById('remoteSonoBtn').style.color = s.ativo ? 'var(--acc-txt)' : '';
  document.getElementById('remoteConfirma').classList.toggle('on', !!(p && p.pendente));
  if (p && p.pendente) document.getElementById('remoteConfirmaTxt').textContent = 'Ainda está assistindo?' + (p.rotulo ? ' · ' + p.rotulo : '');
  if (sonoVisto === null) sonoVisto = s.versao;
  else if (s.versao !== sonoVisto) { sonoVisto = s.versao; if (s.disparou) toast('Boa noite — a TV parou.'); }   // versão que muda sem "disparou" = servidor reiniciou
  if (folhaSonoAberta()) desenharSono();
}

// "Continuar" do aviso: o servidor zera a conta e manda a TV seguir pro próximo (como o OK dela).
async function remoteContinuar() {
  await fetch(`${PLAYER_API}/presenca-continuar`, { method: 'POST' }).catch(() => {});
  pollRemote();
}

// minutos > 0: parar em N minutos; aoFim: parar ao fim do episódio; (0, false) cancela. Um pedido novo substitui o anterior.
async function definirSono(minutos, aoFim, aviso) {
  const r = await fetch(`${PLAYER_API}/sono`, opcoesJson('POST', { minutos, aoFimDoEpisodio: aoFim })).catch(() => null);
  if (!r || !r.ok) { toast('Não consegui armar o sono. Tente de novo.'); return; }
  fecharSono();
  toast(aviso);
  pollRemote();
}

const folhaSonoAberta = () => document.getElementById('sonoFolha').classList.contains('on');
function abrirSono() {
  document.getElementById('sonoScrim').classList.add('on');
  const f = document.getElementById('sonoFolha');
  f.classList.add('on');
  desenharSono();
  f.focus({ preventScroll: true });
}
function fecharSono() {
  document.getElementById('sonoScrim').classList.remove('on');
  document.getElementById('sonoFolha').classList.remove('on');
}
document.addEventListener('keydown', e => { if (e.key === 'Escape' && folhaSonoAberta()) fecharSono(); });

function desenharSono() {
  const s = (ultimoEstadoRemoto && ultimoEstadoRemoto.sono) || {};
  document.getElementById('sonoSub').textContent = s.ativo ? textoDoSono(s) : 'A TV para sozinha quando chegar a hora.';
  document.getElementById('sonoCorpo').innerHTML =
    '<div class="rot">Parar em</div><div class="seg2" role="group" aria-label="Parar em">' +
      [15, 30, 60].map(m => `<button data-sono="${m}" aria-label="Parar em ${m} minutos">${m} min</button>`).join('') + '</div>' +
    '<div class="rot">Ou</div>' +
    `<button class="op" data-sono="fim"><span>Parar ao fim do ${fimDoQue()}</span>${s.aoFim ? ico('check') : ''}</button>` +
    (s.ativo ? '<button class="op" data-sono="cancelar"><span>Cancelar</span></button>' : '');
}

document.getElementById('sonoCorpo').addEventListener('click', e => {
  const b = e.target.closest('[data-sono]');
  if (!b) return;
  const v = b.dataset.sono, s = (ultimoEstadoRemoto && ultimoEstadoRemoto.sono) || {};
  if (v === 'cancelar') definirSono(0, false, 'Sono cancelado.');
  else if (v === 'fim') s.aoFim && s.restanteSegundos == null ? definirSono(0, false, 'Sono cancelado.') : definirSono(0, true, `A TV para ao fim do ${fimDoQue()}.`);   // tocar de novo desarma
  else definirSono(Number(v), false, `A TV para em ${v} min.`);
});

// A barra muda de altura (botões, aviso de próximo): publica a altura real pra lista não ficar atrás dela.
new ResizeObserver(() => {
  document.documentElement.style.setProperty('--remote-h', (els.remote.classList.contains('active') ? els.remote.offsetHeight : 0) + 'px');
}).observe(els.remote);

async function pollRemote() {
  if (remoteTimer) { clearTimeout(remoteTimer); remoteTimer = null; }
  try {
    const res = await fetch(`${PLAYER_API}/state`);
    const estado = await res.json();
    const estadoAnterior = ultimoEstadoRemoto;
    const trocouFilme = !estadoAnterior || estadoAnterior.filmeId !== estado.filmeId;
    ultimoEstadoRemoto = estado;
    tratarAviso(estado.aviso, estado.instancia);
    tratarSono(estado);
    tratarFila(estado);

    const bar = els.remote;
    if (estado.filmeId === null) {
      bar.classList.remove('active');
      filmeAtualRemoto = null;
      els.remoteProximoEpBtn.style.display = 'none';
      remoteAbertura = null; remoteCreditos = null; remoteTemProximo = false;
      document.getElementById('remoteFaixaCreditos').style.display = 'none';
      definirCta('neutro');
      document.getElementById('remoteFaixaAbertura').style.display = 'none';
      if (folha && folha.modo === 'agora') fecharFolha();   // a folha do que toca some quando para; a do padrão da série (⋯) fica até o Daniel fechar
      // Só re-renderiza se realmente parou de tocar algo (transição real) -- no primeiro poll
      // da página (estadoAnterior undefined) "nada tocando" já é exatamente o que o render()
      // inicial (linha ~303) desenhou, então um 2º fetch de /tela aqui seria só desperdício.
      if (estadoAnterior && estadoAnterior.filmeId !== null && !EMBED) render();
      atualizarCapa();
    } else {
      bar.classList.add('active');
      if (trocouFilme) {
        remoteCarregarLegendas(estado.filmeId);
        remoteCarregarAudios(estado.filmeId);
        remoteAbertura = null; remoteCreditos = null; remoteTemProximo = false;
        remoteCarregarAbertura(estado.filmeId);
        remoteCarregarCreditos(estado.filmeId);
        filmeAtualRemoto = await fetchJsonOuNull(`${API}/${estado.filmeId}`);
        atualizarBotaoProximoEp(estado.filmeId);
        atualizarCapa();
      }
      const f = filmeAtualRemoto;
      document.getElementById('remoteTitulo').textContent = f ? (f.ehEpisodio && f.serie ? f.serie : f.titulo) : 'Reproduzindo…';
      document.getElementById('remoteSub').textContent = f && f.ehEpisodio
        ? [f.temporada != null ? 'T' + f.temporada : '', codigoEp(f), f.tituloEpisodio].filter(Boolean).join(' · ')
        : (f && f.anoLancamento ? String(f.anoLancamento) : '');
      const pp = document.getElementById('remotePlayPauseBtn');
      if (pp.dataset.playing !== String(!!estado.playing)) {
        pp.dataset.playing = String(!!estado.playing);
        pp.innerHTML = ico(estado.playing ? 'pause' : 'play', 'f');
        pp.setAttribute('aria-label', estado.playing ? 'Pausar' : 'Continuar');
      }
      const vol = document.getElementById('remoteVolume');
      vol.value = estado.volume;
      vol.style.setProperty('--v', (100 * estado.volume) + '%');
      const pos = estado.posSegundos || 0, dur = estado.duracaoSegundos || 0;
      document.getElementById('remoteProgFill').style.width = dur ? Math.min(100, 100 * pos / dur) + '%' : '0';
      document.getElementById('remoteTempoDec').textContent = pos || dur ? mmss(pos) : '';
      document.getElementById('remoteTempoRest').textContent = dur ? '−' + mmss(dur - pos) : '';
      atualizarBotaoDireita(pos, dur);
      if (trocouFilme && !EMBED) render();
    }
    atualizarLinhaIa();
    const proxBox = document.getElementById('remoteProximo');
    proxBox.classList.toggle('on', estado.proximoFilmeId != null);
    if (estado.proximoFilmeId != null) document.getElementById('remoteProximoTxt').textContent = 'Próximo: ' + (estado.proximoRotulo || '');
  } catch { /* ignora falha pontual de rede */ }
  finally {
    const ativo = els.remote.classList.contains('active');
    remoteTimer = setTimeout(pollRemote, ativo ? POLL_ATIVO_MS : POLL_OCIOSO_MS);
  }
}

if (EMBED) {
  // ?serie=chave&nome=…: aberto pela ficha da série do index — os padrões dessa série (vale também com a TV parada)
  carregarPadroes().then(() => {
    const q = new URLSearchParams(location.search);
    if (q.get('serie')) abrirFolhaSerie(q.get('serie'), q.get('nome') || '');
  });
} else render();
pollRemote();
