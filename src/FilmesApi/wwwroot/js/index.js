const API = '/api/filmes';
let aba = 'inicio';   // inicio | serie | filme | novela | buscar

// Elementos consultados em 2+ lugares (funções diferentes) — pegos uma vez só. Os que só
// aparecem 1x no código (grid, empty, loadbar, etc.) ficam com getElementById direto ali mesmo.
const els = {
  video: document.getElementById('video'),
  converting: document.getElementById('converting'),
  player: document.getElementById('player'),
  busca: document.getElementById('busca'),
  sheet: document.getElementById('sheet'),
  sheetBox: document.getElementById('sheetBox'),
  proximoBox: document.getElementById('proximoBox'),
};

// ─── Configuração (valores antes soltos pelo código, alguns repetidos em mais de um
// lugar com o mesmo literal significando coisas diferentes — ex.: dois "1500" e três "12") ──
const SCAN_MSG_DURATION_MS = 3000;   // quanto tempo o toast "N importados" fica visível
const LOADBAR_RESET_MS = 600;        // espera a transição de "done" terminar antes de zerar a barra

function loadbar(ligado) {
  const b = document.getElementById('loadbar');
  if (ligado) { b.className = 'on'; }
  else { b.className = 'done'; setTimeout(() => { b.className = ''; }, LOADBAR_RESET_MS); }
}

async function load() {
  loadbar(true);
  try { await render(); }
  finally { loadbar(false); }
}

// Filtro (tipo/visto/busca) e agrupamento (filme solto / pasta filme+extras / série com
// episódios) vêm prontos de /api/filmes/tela — esta tela só desenha o que chega, sem
// nenhuma lógica própria de agrupar (era assim que o mesmo bug precisava ser corrigido
// em 3 lugares diferentes: index.html, controle.html e tv.html).
// Miniatura pequena na grade (w185) e pôster maior só na ficha (w500): o TMDb serve o mesmo arquivo em vários tamanhos,
// trocando o segmento "/w342/" da URL guardada — se a URL não tiver esse segmento, usa como veio.
const pw = (url, w) => url ? url.replace('/w342/', `/w${w}/`) : '';

// Indicador do "Manter no cache": ✔ pronto, ⬇ marcado (preparando/na fila), '' nada.
function cacheDoFilme(id) {
  const c = cacheEst.itens.get(id);
  return !c ? '' : c.pronto ? '✔' : '⬇';
}
function cacheDaSerie(eps) {
  const marcados = eps.filter(e => cacheEst.itens.has(e.id));
  if (!marcados.length) return '';
  const prontos = marcados.filter(e => cacheEst.itens.get(e.id).pronto).length;
  return prontos === marcados.length ? `✔ ${prontos}` : `⬇ ${prontos}/${marcados.length}`;
}

const tipoDaAba = { inicio: 'all', serie: 'serie', filme: 'filme', novela: 'novela', buscar: 'all' };
let cacheEst = { itens: new Map(), tetoFixadoBytes: 0, fixadoProntoBytes: 0, remuxBytes: 0, remuxTetoBytes: 0 };   // 'Manter no cache' (GET /api/cache)
let itens = new Map();     // chave -> {tipo:'filme'|'serie'|'pasta', ...} do último render (a ficha lê daqui)
let fichaAberta = null;
let padroesDaSerie = null;   // série aberta na ficha: o botão de ajustes leva à folha de padrões dela no controle da TV    // chave do que está na ficha

function tile(o) {
  const pct = o.pct || 0;
  return `
    <div class="tile" onclick="${o.onclick}" role="button" tabindex="0">
      <div class="cap">
        ${o.poster ? `<img src="${attr(pw(o.poster, 185))}" loading="lazy" alt="">` : ''}
        ${o.poster ? '' : `<span class="sem">${esc(o.nome)}</span>`}
        ${o.visto ? '<span class="ok">✓</span>' : ''}
        ${o.remover ? `<button class="rm" title="Tirar de continuar assistindo" aria-label="Tirar de continuar assistindo" onclick="event.stopPropagation(); removerDeContinuar(${o.remover})">✕</button>` : ''}
        ${o.qtd ? `<span class="qtd">${esc(o.qtd)}</span>` : ''}
        ${o.cache ? `<span class="cch ${o.cache.startsWith('✔') ? 'pronto' : ''}" title="Manter no cache">${esc(o.cache)}</span>` : ''}
        ${pct ? `<div class="rprog"><div style="width:${pct}%"></div></div>` : ''}
      </div>
      <div class="nome">${esc(o.nome)}</div>
      ${o.sub ? `<div class="sub">${esc(o.sub)}</div>` : ''}
    </div>`;
}

// Monta os cartões da grade a partir de /tela e registra cada um em `itens`.
function cartoesDaTela(t) {
  const lista = [];
  const filmeTile = f => {
    itens.set('f:' + f.id, { tipo: 'filme', f });
    return { nome: f.titulo, sort: f.titulo, poster: f.posterUrl, visto: f.assistido, pct: pctProgresso(f),
             sub: f.anoLancamento ? String(f.anoLancamento) : '', cache: cacheDoFilme(f.id), onclick: `abrirFicha('f:${f.id}')` };
  };
  t.filmesSoltos.forEach(f => lista.push(filmeTile(f)));
  t.pastasFilme.forEach(p => {
    const k = 'p:' + p.chave;
    itens.set(k, { tipo: 'pasta', p });
    const pst = p.itens.find(f => f.posterUrl);
    lista.push({ nome: p.nome, sort: p.nome, poster: pst && pst.posterUrl, qtd: `📁 ${p.itens.length}`, onclick: `abrirFicha('${attr(k)}')` });
  });
  t.series.forEach(s => {
    const k = 's:' + s.chave;
    itens.set(k, { tipo: 'serie', s });
    const pst = s.episodios.find(f => f.posterUrl);
    const vistos = s.episodios.filter(e => e.assistido).length;
    lista.push({ nome: s.nome, sort: s.nome, poster: pst && pst.posterUrl, visto: vistos === s.episodios.length, cache: cacheDaSerie(s.episodios),
                 qtd: contagemEpisodios(s.episodios.length).replace(' episódios', ' eps').replace(' episódio', ' ep'),
                 onclick: `abrirFicha('${attr(k)}')` });
  });
  lista.sort((a, b) => a.sort.localeCompare(b.sort, 'pt', { sensitivity: 'base' }));
  return lista;
}

// Fileira horizontal (Continuar assistindo): tocar já reproduz — é o que a pessoa quer ali.
function fileira(titulo, cartoes) {
  if (!cartoes.length) return '';
  return `<section class="fileira"><div class="fileira-tit">${titulo} <span class="count">(${cartoes.length})</span></div><div class="trilho">${cartoes.join('')}</div></section>`;
}

// Cartão do "Continuar assistindo": abre a ficha do título (enviar para a TV / assistir aqui) — o episódio abre a ficha da série.
function cartaoDaFila(i) {
  const f = i.filme;
  const ep = f.ehEpisodio ? (f.temporada != null ? `T${f.temporada} · ` : '') + (codigoEpisodio(f) || f.rotulo || '') : (f.anoLancamento || '');
  const chave = f.ehEpisodio && itens.has('s:' + f.serieChave) ? 's:' + f.serieChave : itens.has('f:' + f.id) ? 'f:' + f.id : null;
  return tile({
    nome: f.ehEpisodio ? (f.serie || f.titulo) : f.titulo, poster: f.posterUrl, pct: i.motivo === 'proximo' ? 0 : pctProgresso(f),
    sub: i.motivo === 'proximo' ? `A seguir · ${ep}` : ep,
    remover: i.motivo === 'proximo' ? 0 : f.id, onclick: chave ? `abrirFicha('${attr(chave)}')` : `play(${f.id})`,
  });
}

async function render() {
  const grid = document.getElementById('grid');
  const empty = document.getElementById('empty');
  const scrollAntes = window.scrollY;  // não pular pro topo ao re-renderizar
  const mostrarVazio = html => { grid.innerHTML = ''; empty.innerHTML = html; empty.style.display = 'block'; };

  if (!grid.innerHTML) { empty.style.display = 'none'; grid.innerHTML = `<section class="fileira esqueleto"><div class="posters">${'<div class="tile"><div class="cap"></div><div class="nome"></div></div>'.repeat(6)}</div></section>`; }   // 1º carregamento: cartões-fantasma
  const q = aba === 'buscar' ? els.busca.value.trim() : '';
  if (aba === 'buscar' && !q) {
    mostrarVazio('<span class="ico">🔍</span>Digite o nome de um filme, série ou novela.');
    return;
  }
  const params = new URLSearchParams({ tipo: tipoDaAba[aba], visto: 'all' });
  if (q) params.set('busca', q);
  const [t, cacheResp, fila] = await Promise.all([fetchJsonOuNull(`${API}/tela?${params}`), fetchJsonOuNull('/api/cache'),
    aba === 'inicio' ? fetchJsonOuNull(`${API}/fila`) : null]);   // fila = Continuar assistindo + "A seguir" (o próximo episódio de série vista há pouco)
  if (cacheResp) cacheEst = { ...cacheResp, itens: new Map(cacheResp.itens.map(i => [i.id, i])) };

  if (!t) {
    mostrarVazio('<span class="ico">📡</span>Não consegui falar com o servidor.<br><button class="tentar" onclick="load()">Tentar de novo</button>');
    return;
  }
  itens = new Map();
  const cartoes = cartoesDaTela(t);

  let html = '';
  if (aba === 'inicio') {
    html += fileira('▶ Continuar assistindo', (fila || t.continuarAssistindo.map(f => ({ filme: f, motivo: 'continuar' }))).map(cartaoDaFila));
  }
  if (cartoes.length) {
    const titulos = { inicio: 'Tudo', serie: 'Séries', filme: 'Filmes', novela: 'Novelas', buscar: 'Resultados' };
    html += `<section class="fileira"><div class="fileira-tit">${titulos[aba]} <span class="count">(${cartoes.length})</span></div><div class="posters">${cartoes.map(c => tile(c)).join('')}</div></section>`;
  }

  if (!html) {
    mostrarVazio(t.catalogoVazio
      ? `<span class="ico">🎬</span>Catálogo vazio.<br><button onclick="scanMedia()">📂 Escanear a pasta de mídia</button>`
      : aba === 'novela' && !q
      ? `<span class="ico">🎭</span>Nenhuma novela ainda.<br>Coloque os capítulos em <b>novelas/Nome da novela/</b> na pasta de mídia e escaneie.`
      : `<span class="ico">🔍</span>${q ? 'Nada encontrado pra “' + esc(q) + '”.' : 'Nada por aqui ainda.'}`);
  } else {
    empty.style.display = 'none';
    grid.innerHTML = html;
  }
  window.scrollTo(0, scrollAntes);
  if (fichaAberta) { if (itens.has(fichaAberta)) montarFicha(fichaAberta); else fecharFicha(); }   // ficha aberta reflete o que mudou (visto, progresso)
}

// ─── "Manter no cache": cópia pronta no NVMe (não depende do SSD USB de mídia) ───
function botaoCacheHtml(ids, modo) {
  if (!ids.length) return '';
  const lista = JSON.stringify(ids);
  if (modo === 'filme') return `<button onclick="fixarCache(${lista})">⬇ Manter no cache</button>`;
  if (modo === 'filme-fixado') return `<button class="no-cache" onclick="desafixarCache(${lista})" aria-label="No cache: tocar para remover" title="Tocar para remover do cache">✔ No cache</button>`;
  if (modo === 'serie') return `<button onclick="fixarCache(${lista})" title="Manter os ${ids.length} episódios não vistos no cache">⬇ Manter no cache <small>${ids.length}</small></button>`;
  return `<button class="no-cache" onclick="desafixarCache(${lista})" aria-label="${ids.length} no cache: tocar para remover" title="Tocar para remover do cache">✔ No cache <small>${ids.length}</small></button>`;
}

async function postarJson(url, corpo) {
  try {
    const r = await fetch(url, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(corpo) });
    return r.ok ? r.json() : null;
  } catch { return null; }
}

async function fixarCache(ids) {
  const r = await postarJson('/api/cache/fixar', { ids: ids.slice(0, 500) });
  msgScanErro(r ? (r.novos ? `⬇ ${r.novos} marcado(s). A cópia é preparada aos poucos.` : 'Já estava no cache.') : 'Não consegui marcar.');
  await render();
}

async function desafixarCache(ids) {
  const r = await postarJson('/api/cache/desafixar', { ids });
  msgScanErro(r ? 'Removido de "Manter no cache".' : 'Não consegui remover.');
  await render();
  if (document.getElementById('cfgCache')) abrirConfig();
}

async function limparCache() {
  if (!confirm('Apagar o cache de reprodução? O que está marcado em "Manter no cache" fica.')) return;
  const r = await postarJson('/api/cache/limpar', {});
  msgScanErro(r ? `🧹 ${r.itens} item(ns), ${(r.bytes / 1073741824).toFixed(1)} GB liberados.` : 'Não consegui limpar.');
  abrirConfig();
}

function trocarTokenDoScan() {
  let atual = ''; try { atual = localStorage.getItem('adminToken') || ''; } catch { /* storage bloqueado */ }
  const novo = prompt('Token de administrador do scan (deixe vazio para apagar):', atual);
  if (novo === null) return;
  try { if (novo) localStorage.setItem('adminToken', novo); else localStorage.removeItem('adminToken'); } catch { /* storage bloqueado */ }
  msgScanErro(novo ? 'Token guardado neste aparelho.' : 'Token apagado.');
}

const ICONES = {
  ajustes: '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M4 6h8M16 6h4M4 12h2M10 12h10M4 18h12M20 18h0"/><circle cx="14" cy="6" r="2"/><circle cx="8" cy="12" r="2"/><circle cx="18" cy="18" r="2"/></svg>',
  ok: '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M5 12l5 5l10-10"/></svg>',
  vazio: '<svg viewBox="0 0 24 24" aria-hidden="true"><circle cx="12" cy="12" r="8"/></svg>',
  tv: '<svg viewBox="0 0 24 24" aria-hidden="true"><rect x="3" y="7" width="18" height="12" rx="2"/><path d="M16 3l-4 4l-4-4"/></svg>',
  fila: '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M4 7h11M4 12h11M4 17h6M18 13v8M14 17h8"/></svg>',
};

// "Tocar a seguir": entra no fim da fila manual da TV (toca antes do próximo episódio da série; com a TV parada, só toca).
async function tocarASeguir(id) {
  try {
    const r = await fetch(`${PLAYER_API}/fila/${id}`, { method: 'POST' });
    if (r.status === 409) { msgScanErro('A fila da TV está cheia.'); return; }
    if (!r.ok) throw new Error();
    const d = await r.json();
    msgScanErro(d.resultado === 'JaNaFila' ? 'Já está na fila da TV.' : '➕ Toca a seguir na TV.');
    consultarAgora();
  } catch { msgScanErro('Não consegui falar com a TV.'); }
}

// ─── Controle da TV: a folha com o controle completo (play/pausa, barra, volume, abertura/créditos/próximo, idioma e legenda, sono, fila…) ───
// É a página controle-tv.html em modo embutido (?embed=1): a MESMA lógica e o mesmo visual do controle de sempre, sem duplicar código.
function abrirControleTv(serie) {   // serie ({ chave, nome }): abre direto nos padrões dessa série (áudio, legenda, abertura por temporada)
  const f = document.getElementById('tvFrame'), sheet = document.getElementById('tvSheet');
  fecharFicha();
  f.src = '/controle-tv.html?embed=1' + (serie ? `&serie=${encodeURIComponent(serie.chave)}&nome=${encodeURIComponent(serie.nome)}` : '');
  sheet.classList.add('on'); sheet.setAttribute('aria-hidden', 'false');
  document.body.style.overflow = 'hidden';
}
function fecharControleTv() {
  const f = document.getElementById('tvFrame'), sheet = document.getElementById('tvSheet');
  sheet.classList.remove('on'); sheet.setAttribute('aria-hidden', 'true');
  f.src = 'about:blank';   // para o poll do controle enquanto a folha está fechada
  document.body.style.overflow = '';
  consultarAgora();
}
window.addEventListener('message', e => {
  if (e.origin !== location.origin || !e.data || e.data.tipo !== 'fechar-controle') return;
  if (e.data.parou) {   // ■ Parar no controle: a TV parou — a barra "Tocando na TV" e o estado velho saem já, sem esperar o próximo poll
    estadoTv = null;
    document.getElementById('agora').classList.remove('on');
    document.body.classList.remove('com-agora');
  }
  fecharControleTv();
  if (e.data.parou) render();   // a lista reflete o que mudou (posição salva, "na TV agora")
});

// ─── Celular ⇄ TV (sem fila, sem confirmação: o último envio vale) ───────
const PLAYER_API = '/api/player';

async function enviarParaTv(id) {
  fetch(`${API}/${id}/pre-aquecer?paraTv=1`, { method: 'POST' }).catch(() => {});   // aquece o remux (e o do próximo) enquanto a TV carrega
  try {
    const r = await fetch(`${PLAYER_API}/selecionar/${id}`, { method: 'POST' });
    if (!r.ok) throw new Error();
    fecharFicha();
    consultarAgora();
    acompanharPreparoTv(id);
  } catch { msgScanErro('Não consegui falar com a TV.'); }
}

// Feedback do envio: a TV só toca o .mp4 quando o servidor termina de preparar (áudio 5.1 → AAC leva ~1,5 min se a cópia não estava pronta). Mostra o estado
// real — porcentagem e segundos — em vez de silêncio; some quando a TV pode tocar.
let preparoTvToken = 0, preparoTvTimer = null;
function mostrarPreparoTv(texto, fixo) {
  const msg = document.getElementById('scanMsg');
  clearTimeout(preparoTvTimer);
  msg.textContent = texto; msg.style.display = 'block';
  if (!fixo) preparoTvTimer = setTimeout(() => { msg.style.display = 'none'; }, SCAN_MSG_DURATION_MS + 1500);
}
async function acompanharPreparoTv(id) {
  const meu = ++preparoTvToken, t0 = Date.now();
  mostrarPreparoTv('📺 Enviado para a TV');
  const pd = await fetchJsonOuNull(`${API}/${id}/pode-direto`);
  if (meu !== preparoTvToken || !pd || !pd.remuxavel) return;   // sem remux: a TV toca direto, nada a esperar
  for (let i = 0; i < 150 && meu === preparoTvToken; i++) {
    const s = await fetchJsonOuNull(`${API}/${id}/remux-status`);
    const seg = Math.round((Date.now() - t0) / 1000);
    if (meu !== preparoTvToken) return;
    if (!s || s.status === 'disponivel') { mostrarPreparoTv(seg > 3 ? `▶ Pronto para a TV (${seg}s)` : '📺 Enviado para a TV'); return; }
    if (s.status === 'erro') { mostrarPreparoTv('A TV não conseguiu preparar este vídeo.'); return; }
    mostrarPreparoTv(`⏳ Preparando para a TV… ${s.progresso != null ? s.progresso + '% · ' : ''}${seg}s`, true);
    await new Promise(r => setTimeout(r, 2000));
  }
}

// Do player do celular para a TV: grava onde parou, manda o mesmo título (a TV retoma dali) e fecha o player local.
async function continuarNaTv() {
  const id = filmeAtual;
  if (id == null) return;
  await salvarProgresso();
  await enviarParaTv(id);
  await closePlayer();
}

// Da TV para o celular: grava a posição que a TV está, para a TV e abre o player local exatamente ali.
async function continuarNoCelular() {
  const est = estadoTv;
  if (!est || est.filmeId == null) return;
  const id = est.filmeId;
  if (est.posSegundos > 15) {
    await fetch(`${API}/${id}/progresso`, { method: 'PUT', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ posicao: est.posSegundos, duracao: est.duracaoSegundos > 0 ? est.duracaoSegundos : null }) }).catch(() => {});
  }
  await fetch(`${PLAYER_API}/parar`, { method: 'POST' }).catch(() => {});
  consultarAgora();
  play(id, false, true);   // semRecuo: abre na posição exata da TV
}

// Barra "tocando agora": só aparece quando a TV está com algo. Independente do player local do celular.
let estadoTv = null, tvFilmeInfo = {};
async function consultarAgora() {
  const caixa = document.getElementById('agora');
  const est = await fetchJsonOuNull(`${PLAYER_API}/state`);
  estadoTv = est;
  if (!est || est.filmeId == null) { caixa.classList.remove('on'); document.body.classList.remove('com-agora'); return; }
  let info = tvFilmeInfo[est.filmeId];
  if (!info) { info = tvFilmeInfo[est.filmeId] = await fetchJsonOuNull(`${API}/${est.filmeId}`) || {}; }
  const nome = info.ehEpisodio ? `${info.serie || ''} · ${codigoEpisodio(info) || info.rotulo || ''}` : (info.titulo || 'Tocando na TV');
  const pos = est.posSegundos > 0 ? ` · ${formatarTempo(est.posSegundos)}` : '';
  const pct = est.duracaoSegundos > 0 ? Math.min(100, 100 * est.posSegundos / est.duracaoSegundos) : 0;
  const ico = {
    pausa: '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M8 5v14M16 5v14"/></svg>',
    toca: '<svg class="cheio" viewBox="0 0 24 24" aria-hidden="true"><path d="M7 4.5v15l12-7.5z"/></svg>',
    celular: '<svg viewBox="0 0 24 24" aria-hidden="true"><rect x="7" y="2.5" width="10" height="19" rx="2.5"/><path d="M11 18.5h2"/></svg>',
  };
  caixa.innerHTML = `
    <div class="fio" style="width:${pct}%"></div>
    ${info.posterUrl ? `<img src="${attr(pw(info.posterUrl, 185))}" alt="">` : '<img alt="">'}
    <a class="txt" role="button" tabindex="0" onclick="abrirControleTv()" title="Abrir o controle completo da TV"><small>${est.playing ? 'Tocando na TV' : 'Pausado na TV'}${esc(pos)}</small><b>${esc(nome)}</b></a>
    <button onclick="fetch('${PLAYER_API}/play-pause',{method:'POST'}).then(consultarAgora)" aria-label="${est.playing ? 'Pausar a TV' : 'Tocar na TV'}" title="${est.playing ? 'Pausar' : 'Tocar'}">${est.playing ? ico.pausa : ico.toca}</button>
    <button onclick="continuarNoCelular()" aria-label="Continuar no celular" title="Continuar no celular">${ico.celular}</button>`;
  caixa.classList.add('on');
  document.body.classList.add('com-agora');
}
// Rolando a grade pra baixo a barra recolhe (mais área pra ver); subindo, volta.
let ultimoScrollY = 0;
window.addEventListener('scroll', () => {
  const y = window.scrollY, caixa = document.getElementById('agora');
  if (Math.abs(y - ultimoScrollY) < 8) return;
  caixa.classList.toggle('recolhida', y > ultimoScrollY && y > 80);
  ultimoScrollY = y;
}, { passive: true });
setInterval(() => { if (!document.hidden) consultarAgora(); }, 3000);
consultarAgora();

// ─── Configurações (menu simples; as opções reais entram aqui conforme forem aprovadas) ───
async function abrirConfig() {
  fichaAberta = null;
  const c = await fetchJsonOuNull('/api/cache');
  const gb = n => (n / 1073741824).toFixed(1);
  let cacheHtml = '<div class="cfg-info">Não consegui ler o cache agora.</div>';
  if (c) {
    const prontos = c.itens.filter(i => i.pronto).length;
    cacheHtml = `
      <div class="cfg-info" id="cfgCache">Cache de reprodução: <b>${gb(c.remuxBytes)} GB</b>${c.remuxTetoBytes ? ` de ${gb(c.remuxTetoBytes)} GB` : ''} usados.<br>
        Mantidos: <b>${c.itens.length}</b> (${prontos} pronto(s), ${gb(c.fixadoProntoBytes)}${c.tetoFixadoBytes ? ` de ${gb(c.tetoFixadoBytes)}` : ''} GB). O que está marcado nunca é apagado.</div>
      ${c.itens.map(i => `<div class="cfg-item"><span title="${attr(i.nome)}">${i.pronto ? '✔' : i.preparando ? '⏳' : '⬇'} ${esc(i.nome)}</span><button onclick="desafixarCache([${i.id}])" aria-label="Remover do cache">✕</button></div>`).join('')}`;
  }
  els.sheetBox.scrollTop = 0;
  els.sheetBox.innerHTML = `
    <button class="sheet-x" onclick="fecharFicha()" aria-label="Fechar">✕</button>
    <div class="sheet-tit">Configurações</div>
    <div class="cfg-lista">
      <button onclick="abrirControleTv()">🎛 Controle da TV <small>completo</small></button>
      <button onclick="fecharFicha(); scanMedia()">📂 Escanear a pasta de mídia</button>
      <button onclick="trocarTokenDoScan()">🔑 Token do scan <small>neste aparelho</small></button>
      <a href="/diagnostico.html">🩺 Diagnóstico do catálogo</a>
      <a href="/status.html">📊 Status do servidor</a>
    </div>
    <div class="sheet-tit" style="margin-top:20px;font-size:16px">⬇ Manter no cache</div>
    <div class="cfg-lista">${cacheHtml}
      <button onclick="limparCache()">🧹 Limpar cache <small>mantém os marcados</small></button>
    </div>`;
  els.sheet.classList.add('on');
  document.body.style.overflow = 'hidden';
}

function irParaAba(nome) {
  aba = nome;
  document.querySelectorAll('#abas button').forEach(b => b.classList.toggle('on', b.dataset.aba === nome));
  document.getElementById('buscaWrap').classList.toggle('on', nome === 'buscar');
  if (nome === 'buscar') els.busca.focus();
  window.scrollTo(0, 0);
  load();
}
document.getElementById('abas').addEventListener('click', e => {
  const b = e.target.closest('button[data-aba]');
  if (b) irParaAba(b.dataset.aba);
});

let buscaTimer = null;
function buscarDigitando() {
  clearTimeout(buscaTimer);
  buscaTimer = setTimeout(render, 250);
}

// ─── Ficha do título (folha que sobe de baixo) ──────────────────────────
function sinopseHtml(texto) {
  if (!texto) return '';
  return `<p class="sinopse" id="sinopse">${esc(texto)}</p>${texto.length > 180 ? '<button class="mais" onclick="document.getElementById(\'sinopse\').classList.toggle(\'aberta\'); this.textContent = this.textContent === \'mais\' ? \'menos\' : \'mais\'">mais</button>' : ''}`;
}

async function montarFicha(chave) {
  const it = itens.get(chave);
  if (!it) return;
  let poster = '', titulo = '', meta = '', sinopse = '', acoes = '', corpo = '';
  if (it.tipo === 'filme') {
    const f = it.f;
    poster = f.posterUrl; titulo = f.titulo;
    meta = [f.anoLancamento, f.tituloOriginal && f.tituloOriginal !== f.titulo ? f.tituloOriginal : ''].filter(Boolean).join(' · ');
    sinopse = f.sinopse;
    const retomar = f.posicaoSegundos ? `▶ Continuar aqui (${formatarTempo(f.posicaoSegundos)})` : '▶ Assistir aqui';
    acoes = `<button class="pri" onclick="enviarParaTv(${f.id})">📺 Enviar para a TV</button>
      <button onclick="play(${f.id})">${retomar}</button>
      <button onclick="tocarASeguir(${f.id})">➕ Tocar a seguir</button>
      ${botaoCacheHtml([f.id], cacheDoFilme(f.id) ? 'filme-fixado' : 'filme')}
      <button onclick="toggleAssistido(${f.id})">${f.assistido ? '✓ Assistido' : 'Marcar como assistido'}</button>
      <a href="/api/filmes/${f.id}/download" download title="Baixar o arquivo original" class="btn-baixar">⬇ Baixar</a>`;
  } else if (it.tipo === 'pasta') {
    const p = it.p, pst = p.itens.find(f => f.posterUrl);
    poster = pst && pst.posterUrl; titulo = p.nome; meta = `${p.itens.length} arquivos`;
    sinopse = (p.itens.find(f => f.sinopse) || {}).sinopse;
    corpo = p.itens.map(f => rowHtml(f)).join('');
  } else {
    const s = it.s, pst = s.episodios.find(f => f.posterUrl);
    poster = pst && pst.posterUrl; titulo = s.nome;
    const temps = new Set(s.episodios.map(e => e.temporada).filter(t => t > 0)).size;
    const orig = s.episodios.map(e => e.tituloOriginal).find(Boolean);
    meta = [temps > 1 ? `${temps} temporadas` : '', contagemEpisodios(s.episodios.length), textoVistos(s.episodios)].filter(Boolean).join(' · ');
    sinopse = (s.episodios.find(f => f.sinopse) || {}).sinopse;
    const proximo = s.episodios.find(e => !e.assistido) || s.episodios[0];
    const emAndamento = s.episodios.find(e => e.posicaoSegundos);
    const alvo = emAndamento || proximo;
    const nomeAlvo = esc(codigoEpisodio(alvo) || alvo.rotulo || '');
    acoes = alvo ? `<button class="pri" onclick="enviarParaTv(${alvo.id})">📺 Enviar para a TV · ${nomeAlvo}</button>
      <button onclick="play(${alvo.id})">${emAndamento ? '▶ Continuar aqui' : '▶ Assistir aqui'}</button>` : '';
    const idsNaoVistos = s.episodios.filter(e => !e.assistido && !e.ehExtra).map(e => e.id);
    const idsFixados = s.episodios.filter(e => cacheEst.itens.has(e.id)).map(e => e.id);
    acoes += botaoCacheHtml(idsFixados.length ? idsFixados : idsNaoVistos, idsFixados.length ? 'serie-fixada' : 'serie');
    corpo = corpoDaSerieHtml(s.episodios, '', f => rowHtml(f, true));   // padrões de áudio/legenda/abertura moram na folha do controle da TV (botão de ajustes da ficha)
    padroesDaSerie = { chave: s.chave, nome: s.nome };
  }
  if (fichaAberta !== chave && fichaAberta !== null && !itens.has(fichaAberta)) return;
  const abertoAntes = els.sheetBox.scrollTop;
  const naTv = estadoTv && estadoTv.filmeId != null && (it.tipo === 'filme' ? it.f.id === estadoTv.filmeId
    : it.tipo === 'serie' && it.s.episodios.some(e => e.id === estadoTv.filmeId));
  els.sheetBox.innerHTML = `
    <button class="sheet-x" onclick="fecharFicha()" aria-label="Fechar">✕</button>
    ${it.tipo === 'serie' ? `<button class="sheet-cfg" onclick="abrirControleTv(padroesDaSerie)" aria-label="Padrões desta série: áudio, legenda e abertura" title="Padrões desta série">${ICONES.ajustes}</button>` : ''}
    ${naTv ? '<div class="agora-tag">📺 Tocando na TV agora</div>' : ''}
    <div class="ficha">
      ${poster ? `<img src="${attr(pw(poster, 500))}" alt="">` : '<div class="semp">🎬</div>'}
      <div><h2>${esc(titulo)}</h2><div class="meta">${esc(meta)}</div>${sinopseHtml(sinopse)}</div>
    </div>
    <div class="acoes">${acoes}</div>
    ${corpo ? `<div class="corpo-ficha">${corpo}</div>` : ''}`;
  els.sheetBox.scrollTop = abertoAntes;
}

async function abrirFicha(chave) {
  fichaAberta = chave;
  els.sheetBox.scrollTop = 0;
  await montarFicha(chave);
  els.sheet.classList.add('on');
  document.body.style.overflow = 'hidden';
}

function fecharFicha() {
  fichaAberta = null;
  els.sheet.classList.remove('on');
  document.body.style.overflow = '';
}

function pctProgresso(item) {
  if (!item.posicaoSegundos || !item.duracaoSegundos) return 0;
  return Math.min(100, Math.round(100 * item.posicaoSegundos / item.duracaoSegundos));
}

function barraProgresso(item) {
  const pct = pctProgresso(item);
  return pct ? `<div class="rprog"><div style="width:${pct}%"></div></div>` : '';
}

// formatarTempo() vem de /js/util.js.

const pad2 = n => String(n).padStart(2, '0');

// "S01E02" quando dá pra saber temporada/episódio; null senão (chamador decide o fallback).
function codigoEpisodio(f) {
  return f.temporada != null ? `S${pad2(f.temporada)}E${pad2(f.episodio)}` : null;
}

// nome "limpo" do episódio: tira SxxExx embutido, o "8 - "/"8   " do começo e,
// se sobrar só o nome da série (nome de arquivo genérico), não repete.
function tituloEpisodio(f) {
  let s = String(f.titulo || '')
    .replace(/\bS\d{1,2}\s*E\d{1,3}\b/ig, ' ')
    .replace(/^\s*\d{1,3}[\s.\-–—):\]]+/, '')
    .replace(/\s+/g, ' ').trim();
  if (f.serie && s.toLowerCase() === f.serie.toLowerCase()) s = '';
  return s;
}

// Miniatura do pôster (TMDB) quando tem; senão um retângulo vazio, só pra manter as
// linhas alinhadas na lista. "loading=lazy" evita baixar tudo de uma vez em catálogo grande.
function posterHtml(f) {
  return f.posterUrl
    ? `<img class="rposter" src="${attr(f.posterUrl)}" loading="lazy" alt="">`
    : `<span class="rposter"></span>`;
}

// Tooltip: título original (quando diverge do exibido) + sinopse — TMDB às vezes traduz
// tão livremente que vale mostrar o nome original ao passar o mouse.
function tooltipFilme(f) {
  const original = f.tituloOriginal && f.tituloOriginal !== f.titulo ? f.tituloOriginal + '\n' : '';
  return attr(original + (f.sinopse || f.titulo));
}

// Nome do episódio sem repetir a série: o título do arquivo costuma vir "Avenida Brasil E010 iND"; tira o prefixo da série, o marcador e o grupo.
function nomeDoEpisodio(f) {
  if (f.tituloEpisodio) return f.tituloEpisodio;
  if (f.ehNovela) return '';   // "Capítulo N" já diz tudo
  const sem = t => String(t || '').normalize('NFD').replace(/[\u0300-\u036f]/g, '').toLowerCase().replace(/[^a-z0-9]+/g, ' ').trim();
  let nome = tituloEpisodio(f);
  const serie = sem(f.serie);
  if (serie && sem(nome).startsWith(serie)) nome = nome.slice(String(f.serie).length).replace(/^[\s\-–—:.]+/, '');
  nome = nome.replace(/\bE\d{1,4}\b/gi, ' ').replace(/\s+/g, ' ').trim();
  return sem(nome) === serie || nome.length < 3 ? '' : nome;
}

function rowHtml(f, episodio) {
  const retomar = f.posicaoSegundos ? `<span class="resume">▶ ${formatarTempo(f.posicaoSegundos)}</span>` : '';
  let titulo;
  if (episodio) {
    const cod = f.ehNovela ? (f.rotulo || codigoEpisodio(f) || '') : (codigoEpisodio(f) || f.rotulo || '');
    const nome = nomeDoEpisodio(f);
    titulo = (cod || nome)
      ? `${cod ? `<span class="rep">${esc(cod)}</span>` : ''}${esc(nome)}`
      : esc(f.titulo);
  } else {
    titulo = esc(f.titulo);
  }
  const naTv = estadoTv && estadoTv.filmeId === f.id;
  return `
    <div class="row ${f.assistido ? 'assistido' : ''}${naTv ? ' tc' : ''}" onclick="play(${f.id})" title="${tooltipFilme(f)}">
      ${episodio ? '' : posterHtml(f)}
      <span class="rtit">${titulo}</span>
      ${!episodio && f.anoLancamento ? `<span class="rano">${f.anoLancamento}</span>` : ''}
      ${retomar}
      ${naTv ? '<span class="resume">📺</span>' : ''}
      <button class="rbtn" aria-label="Enviar para a TV" title="Enviar para a TV" onclick="event.stopPropagation(); enviarParaTv(${f.id})">${ICONES.tv}</button>
      <button class="rbtn" aria-label="Tocar a seguir na TV" title="Tocar a seguir" onclick="event.stopPropagation(); tocarASeguir(${f.id})">${ICONES.fila}</button>
      <button class="rbtn ck ${f.assistido ? 'on' : ''}" aria-pressed="${!!f.assistido}" aria-label="${f.assistido ? 'Assistido: desmarcar' : 'Marcar como assistido'}" title="${f.assistido ? 'Assistido' : 'Marcar como assistido'}" onclick="event.stopPropagation(); toggleAssistido(${f.id})">${f.assistido ? ICONES.ok : ICONES.vazio}</button>
      <a class="baixar" href="/api/filmes/${f.id}/download" download title="Baixar o arquivo original" onclick="event.stopPropagation()">⬇</a>
      ${barraProgresso(f)}
    </div>`;
}

async function removerDeContinuar(id) {
  await fetch(`${API}/${id}/progresso`, { method: 'DELETE' });
  render();  // re-busca já refletindo a remoção — sem cache local pra remendar
}

async function toggleAssistido(id) {
  await fetch(`${API}/${id}/assistido`, { method: 'PUT' });
  render();  // re-busca já refletindo a mudança — sem cache local pra remendar
}

// AdminToken (opcional no servidor): se o scan voltar 401, pede o token uma vez e guarda neste navegador.
async function postarScan() {
  const headers = () => { try { const t = localStorage.getItem('adminToken'); return t ? { 'X-Admin-Token': t } : {}; } catch { return {}; } };
  let res = await fetch(`${API}/scan`, { method: 'POST', headers: headers() });
  if (res.status === 401) {
    const token = prompt('O servidor pede um token de administrador pro scan:');
    if (token) { try { localStorage.setItem('adminToken', token); } catch { /* storage bloqueado: vale só desta vez */ } res = await fetch(`${API}/scan`, { method: 'POST', headers: headers() }); }
  }
  return res;
}

function msgScanErro(texto) {
  const msg = document.getElementById('scanMsg');
  msg.textContent = texto;
  msg.style.display = 'block';
  setTimeout(() => msg.style.display = 'none', SCAN_MSG_DURATION_MS);
}

async function scanMedia() {
  const btn = document.querySelector('.actions button');
  if (btn) { btn.disabled = true; btn.textContent = '⏳ Escaneando…'; }
  try {
    const res = await postarScan();
    if (res.status === 401) { msgScanErro('Token de administrador errado ou ausente.'); return; }
    const data = await res.json();
    const msg = document.getElementById('scanMsg');
    const partes = [`${data.importados} importado(s)`];
    if (data.removidos) partes.push(`${data.removidos} removido(s)`);
    if (data.titulosLimpos) partes.push(`${data.titulosLimpos} título(s) limpo(s)`);
    msg.textContent = partes.join(' · ');
    msg.style.display = 'block';
    setTimeout(() => msg.style.display = 'none', SCAN_MSG_DURATION_MS);
    await load();
  } finally {
    if (btn) { btn.disabled = false; btn.textContent = '📂 Scan'; }
  }
}

document.addEventListener('keydown', e => {
  if (e.key === 'Escape') {
    if (document.getElementById('tvSheet').classList.contains('on')) fecharControleTv();
    else if (els.player.classList.contains('active')) closePlayer();
    else if (fichaAberta) fecharFicha();
    els.busca.blur();
  }
  // "/" foca a busca (a não ser que já esteja digitando em algum campo)
  if (e.key === '/' && !/^(INPUT|TEXTAREA)$/.test(document.activeElement.tagName)) {
    e.preventDefault();
    irParaAba('buscar');
  }
});
configurarProgresso();
load();
if (/[?&]tv=1\b/.test(location.search)) abrirControleTv();   // /controle.html redireciona pra cá
