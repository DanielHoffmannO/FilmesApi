// Player LOCAL do celular (o overlay do index.html): HLS/stream/remux, retomada, legendas e próximo episódio. Independente da TV:
// não fala com /api/player/*. Depende de util.js (decisões puras), player-regras.js e, em tempo de chamada, de `els`/`render`/`pw` do index.js.
const RETOMAR_MIN_SEGUNDOS = 5;      // abaixo disso não vale a pena retomar do meio
const RETOMAR_RECUO_S = 7;         // ao retomar de onde parou, volta esses segundos (recuarRetomada: não recua perto do início)
const RETOMAR_MARGEM_FIM_S = 5;      // dentro desses últimos segundos do fim, considera "já terminou" (não retoma)
const RETOMAR_TOLERANCIA_S = 1.5;    // diferença aceitável entre currentTime e a retomada aplicada
const MAX_RETOMAR_TENTATIVAS = 4;    // desiste de tentar re-aplicar o seek de retomada
const MAX_HLS_RETRIES = 3;           // reconexões automáticas em erro de rede do hls.js
const HLS_RETRY_DELAY_MS = 1500;     // espera antes de cada reconexão do hls.js
const POLL_PREPARANDO_MS = 1500;     // intervalo de poll enquanto o vídeo ainda está "preparando"
const MAX_FALHAS_POLL = 12;          // falhas de rede seguidas no poll até desistir com "erro"
const WATCHDOG_STALL_MS = 15000;     // tempo parado no live-edge do HLS até considerar travado
const WATCHDOG_POLL_MS = 5000;       // intervalo do watchdog de live-edge
const DICA_4K_S = 12;                // segundos preparando até mostrar a dica de "arquivo 4K"
const PROXIMO_COUNTDOWN_S = 12;      // contagem regressiva antes de tocar o próximo episódio sozinho
const AUTOSAVE_THROTTLE_MS = 10000;  // intervalo mínimo entre saves automáticos de progresso
const KEEPALIVE_MS = 20000;          // intervalo do aviso "ainda tem alguém aqui" pro transcode
const PLAYER_CONTROLS_HEIGHT_PX = 64; // altura estimada da barra de controle nativa do <video>

let hls = null;

function destruirHls() {
  if (hls) { hls.destroy(); hls = null; }
}

// ─── Watchdog do "live edge" ────────────────────────────────────────────
// A playlist HLS cresce sem #EXT-X-ENDLIST enquanto o servidor ainda converte. Se a
// reprodução alcança o fim do que já foi gerado (assiste mais rápido que o encode, ou o
// job travou/morreu no meio), o <video> só fica "bufferizando" pra sempre — sem nenhum
// aviso, indistinguível de travado de verdade. Esse watchdog detecta currentTime parado
// tocando e consulta /stream-status pra decidir: ainda convertendo (avisa e segue
// esperando), falhou de vez (mostra erro em vez de spinner eterno), ou já terminou e o
// hls.js só não recarregou a playlist ainda (força reload — ele nota o ENDLIST e conclui).
let watchdogLiveEdge = null;
let vigiaPos = -1;
let vigiaDesde = 0;

function pararWatchdogLiveEdge() {
  if (watchdogLiveEdge) { clearInterval(watchdogLiveEdge); watchdogLiveEdge = null; }
  vigiaPos = -1;
  vigiaDesde = 0;
}

function iniciarWatchdogLiveEdge(id) {
  pararWatchdogLiveEdge();
  watchdogLiveEdge = setInterval(async () => {
    const video = els.video;
    if (filmeAtual !== id || video.paused || video.ended || reproducaoConcluida) {
      vigiaPos = -1; vigiaDesde = 0;
      return;
    }
    if (video.currentTime !== vigiaPos) {
      vigiaPos = video.currentTime;
      vigiaDesde = Date.now();
      const converting = els.converting;
      if (converting.dataset.liveEdge) { converting.style.display = 'none'; delete converting.dataset.liveEdge; }
      return;
    }
    if (!vigiaDesde) vigiaDesde = Date.now();
    if (Date.now() - vigiaDesde < WATCHDOG_STALL_MS) return;  // parado há pouco: buffer normal, não é o live-edge

    const status = await checarStatus(id);
    if (filmeAtual !== id) return;
    const converting = els.converting;
    if (status === 'erro') {
      converting.innerHTML = 'A conversão falhou no meio do vídeo. <button onclick="play(filmeAtual)">Tentar de novo</button>';
      converting.style.display = 'block';
      converting.dataset.liveEdge = '1';
      pararWatchdogLiveEdge();
    } else if (status === 'preparando') {
      converting.textContent = 'Ainda convertendo — a reprodução alcançou o que já foi processado. Aguarde…';
      converting.style.display = 'block';
      converting.dataset.liveEdge = '1';
      vigiaDesde = Date.now();  // não repete o /stream-status a cada 5s enquanto ainda prepara
    } else if (status === 'disponivel' && hls) {
      // Terminou (ganhou #EXT-X-ENDLIST) mas o hls.js ainda não recarregou a playlist pra
      // perceber — forçar o reload costuma bastar pra ele notar o fim e disparar 'ended'.
      hls.loadSource(`${API}/${id}/hls/playlist.m3u8`);
      vigiaDesde = Date.now();
    }
  }, WATCHDOG_POLL_MS);
}

let hlsRetries = 0;
function iniciarHls(video, url, inicio) {
  destruirHls();
  const t = inicio > 5 ? Math.floor(inicio) : 0;
  const idAlvo = filmeAtual;
  if (window.Hls && Hls.isSupported()) {
    hls = new Hls(t ? { startPosition: t } : {});  // começa já na retomada, sem pulo
    hls.loadSource(url);
    hls.attachMedia(video);
    hls.on(Hls.Events.MANIFEST_PARSED, () => video.play());
    hls.on(Hls.Events.ERROR, (_e, data) => {
      if (!data.fatal) return;
      if (data.type === Hls.ErrorTypes.MEDIA_ERROR) { hls.recoverMediaError(); return; }
      // erro fatal de rede: normalmente o transcode reiniciou e um segmento sumiu.
      // Reabre o filme (retoma perto de onde estava) em vez de deixar o player travado.
      if (data.type === Hls.ErrorTypes.NETWORK_ERROR && filmeAtual === idAlvo && hlsRetries++ < MAX_HLS_RETRIES) {
        setTimeout(() => { if (filmeAtual === idAlvo) play(idAlvo, true); }, HLS_RETRY_DELAY_MS);
      } else {
        destruirHls();
      }
    });
  } else if (video.canPlayType('application/vnd.apple.mpegurl')) {
    video.src = t ? `${url}#t=${t}` : url;
    video.play();
  } else {
    console.error('Navegador sem suporte a HLS.');
  }
}

async function play(id, ehRetry, semRecuo) {
  const video = els.video;
  const converting = els.converting;
  cancelarProximo();
  removerLegendas();
  video.removeAttribute('src');
  destruirHls();
  pararWatchdogLiveEdge();
  converting.style.display = 'none';
  els.player.classList.add('active');

  filmeAtual = id;
  ultimoSalvo = 0;
  posicaoParaRetomar = 0;
  retomarTentativas = 0;
  checarFalhas = 0;
  if (!ehRetry) hlsRetries = 0;  // retry preserva o contador, senão o teto de 3 nunca conta
  duracaoConfiavel = false;
  reproducaoConcluida = false;
  reiniciarContextoLocal();
  carregarContextoLocal(id);   // abertura, créditos, próximo e interruptores da temporada: chegam em segundo plano
  const prog = await fetchJsonOuNull(`${API}/${id}/progresso`);
  if (prog && prog.posicaoSegundos) posicaoParaRetomar = (ehRetry || semRecuo) ? prog.posicaoSegundos : recuarRetomada(prog.posicaoSegundos, RETOMAR_RECUO_S, RETOMAR_MIN_SEGUNDOS);

  const inicioPrep = Date.now();
  converting.style.display = 'block';
  const prepTimer = setInterval(() => {
    const s = Math.round((Date.now() - inicioPrep) / 1000);
    const dica = s > DICA_4K_S ? '\nArquivos 4K levam 1-2 min na primeira vez — depois fica em cache.' : '';
    converting.innerHTML = preparandoHtml(s, dica);
  }, 1000);
  converting.innerHTML = preparandoHtml(0, '');

  // Cópia pronta no NVMe ("Manter no cache")? Toca o .mp4 dela em vez do original do SSD USB.
  let viaCache = false;
  if (cacheEst.itens.get(id)?.pronto) {
    const rs = await fetchJsonOuNull(`${API}/${id}/remux-status`);
    viaCache = !!rs && rs.status === 'disponivel';
  }
  let status;
  try {
    status = viaCache ? 'compativel' : await checarStatus(id);
    while (status === 'preparando') {
      await new Promise(r => setTimeout(r, POLL_PREPARANDO_MS));
      if (filmeAtual !== id) return;  // fechou/trocou de filme durante a espera
      status = await checarStatus(id);
    }
  } finally {
    clearInterval(prepTimer);
  }
  converting.style.display = 'none';

  if (status === 'erro') {
    converting.innerHTML = `<div class="erro-tit">Não consegui preparar o vídeo</div><div class="prep-sub">O arquivo pode estar no disco que caiu ou ainda sendo baixado. Tente de novo em instantes.</div><button onclick="play(${id}, true)">Tentar de novo</button> <button onclick="closePlayer()">Fechar</button>`;
    converting.style.display = 'block';
    return;
  }

  if (status === 'compativel') {
    duracaoConfiavel = true;
    const frag = posicaoParaRetomar > RETOMAR_MIN_SEGUNDOS ? `#t=${Math.floor(posicaoParaRetomar)}` : '';
    video.src = `${API}/${id}/${viaCache ? 'remux' : 'stream'}${frag}`;  // fragmento = começa na retomada, sem pulo
    video.play();
  } else {
    iniciarHls(video, `${API}/${id}/hls/playlist.m3u8`, posicaoParaRetomar);
    iniciarWatchdogLiveEdge(id);
  }
  carregarLegendas(id);
  // "assistido" agora é decidido pelo backend quando a reprodução chega perto do fim
  // (só no stream direto, onde a duração é confiável), ou manualmente no badge —
  // não mais ao simplesmente abrir.
}

// nunca lança: erro de rede transitório vira 'preparando' (segue tentando);
// depois de muitas falhas seguidas, desiste com 'erro'.
let checarFalhas = 0;
async function checarStatus(id) {
  try {
    const res = await fetch(`${API}/${id}/stream-status`);
    if (!res.ok) throw new Error('http ' + res.status);
    const data = await res.json();
    checarFalhas = 0;
    return data.status;
  } catch {
    return (++checarFalhas > MAX_FALHAS_POLL) ? 'erro' : 'preparando';
  }
}

// ─── Legendas embutidas ───────────────────────────────────────────────
// Cada faixa de texto vira um <track> apontando pro endpoint .vtt; o menu "CC"
// nativo do <video controls> cuida da troca. Funciona no stream direto e no HLS.
const LANG_BCP = { por: 'pt', pob: 'pt', pt: 'pt', eng: 'en', spa: 'es', fre: 'fr', fra: 'fr', ger: 'de', deu: 'de', ita: 'it', jpn: 'ja' };

function removerLegendas() {
  document.querySelectorAll('#video track').forEach(t => t.remove());
}

async function carregarLegendas(id) {
  let faixas;
  try { faixas = await fetch(`${API}/${id}/legendas`).then(r => r.ok ? r.json() : []); }
  catch { return; }
  if (filmeAtual !== id) return;  // trocou de filme enquanto buscava

  const video = els.video;
  removerLegendas();
  faixas.filter(x => x.convertivel).forEach(x => {
    const tr = document.createElement('track');
    tr.kind = 'subtitles';
    tr.src = `${API}/${id}/legenda/${x.idx}`;
    tr.srclang = LANG_BCP[(x.idioma || '').toLowerCase()] || x.idioma || 'und';
    tr.label = x.titulo || x.idioma || `Legenda ${x.idx + 1}`;
    if (x.recomendada || (x.forced && (x.idioma === 'por' || x.idioma === 'pob'))) tr.default = true;
    video.appendChild(tr);
  });
}

// ─── Próximo episódio ─────────────────────────────────────────────────
let proximoInfo = null, proximoInterval = null, proximoOfertaId = 0;

function cancelarProximo() {
  if (proximoInterval) { clearInterval(proximoInterval); proximoInterval = null; }
  proximoInfo = null;
  proximoOfertaId++;  // invalida qualquer resposta de /proximo ainda em voo (ver ofereceProximo)
  els.proximoBox.classList.remove('on');
}

async function ofereceProximo(id) {
  // 'ended' pode disparar 2x em alguns navegadores. cancelarProximo() sozinho não basta: se
  // as 2 chamadas acontecem síncronas (uma logo após a outra, antes do fetch da 1ª
  // responder), as 2 leem o estado ainda limpo e nenhuma cancela nada — cada resposta monta
  // seu PRÓPRIO setInterval e só o mais recente fica em proximoInterval; o outro roda órfão
  // e chama irProximo() de novo pouco depois, reiniciando o episódio que acabou de começar.
  // O token (proximoOfertaId) resolve isso não importa o intervalo entre as 2 chamadas: só a
  // resposta da ÚLTIMA ofereceProximo (que "ganhou" o token por último) monta o timer.
  cancelarProximo();
  const contagem = contagemNoFim(estadoLocalDoPlayer(ctxLocal), PROXIMO_COUNTDOWN_S);   // null = cancelou o próximo automático neste episódio: fica parado no fim
  if (contagem === null) return;
  const meuToken = ++proximoOfertaId;
  let prox;
  try {
    const r = await fetch(`${API}/${id}/proximo`);
    if (r.status !== 200) return;
    prox = await r.json();
  } catch { return; }
  if (filmeAtual !== id || !prox || meuToken !== proximoOfertaId) return;

  proximoInfo = prox;
  preAquecerProximo(prox.id);
  document.getElementById('proximoTit').textContent = rotuloComTitulo(prox) + (prox.serie ? ' — ' + prox.serie : '');
  els.proximoBox.classList.add('on');

  let resta = contagem;
  const tick = () => {
    document.getElementById('proximoSeg').textContent = `em ${resta}s`;
    if (resta-- <= 0) irProximo();
  };
  tick();
  proximoInterval = setInterval(tick, 1000);
}

// O botão "Cancelar" da oferta: vale pro resto do episódio (não oferece de novo nos créditos nem no fim), como no controle da TV.
function cancelarProximoPeloUsuario() {
  ctxLocal.cancelado = true;
  cancelarProximo();
}

function irProximo() {
  const prox = proximoInfo;
  cancelarProximo();
  if (prox) play(prox.id, false, true);
}

// Dispara/reusa o transcode do próximo episódio assim que a oferta aparece (fim do atual) —
// o HLS entrega o primeiro segmento sem esperar o episódio inteiro, então só precisa começar
// cedo pra não ter o "Preparando..." quando o usuário realmente abrir. Se o arquivo for
// compatível direto, o endpoint só responde 409 na hora, sem custo. Resposta ignorada de
// propósito — é só pra cutucar o servidor a começar, ninguém vai consumir isso agora.
function preAquecerProximo(id) {
  fetch(`${API}/${id}/hls/playlist.m3u8`).catch(() => {});
}

async function closePlayer() {
  const id = filmeAtual;
  cancelarProximo();
  await salvarProgresso();  // grava ANTES de rebuscar, senão a lista volta desatualizada
  const video = els.video;
  removerLegendas();
  destruirHls();
  pararWatchdogLiveEdge();
  video.pause();
  video.src = '';
  filmeAtual = null;
  reiniciarContextoLocal();
  els.player.classList.remove('active');
  els.converting.style.display = 'none';
  await atualizarAposReproducao(id);
}

// render() já busca /tela de novo — reflete a posição/progresso salvo sem precisar
// remendar nada em cache local.
async function atualizarAposReproducao(id) {
  render();
}

// ─── Continuar de onde parou ────────────────────────────────────────────
let filmeAtual = null;
let ultimoSalvo = 0;
let posicaoParaRetomar = 0;
let retomarTentativas = 0;
// video.duration só é confiável no stream direto; no HLS ele cresce enquanto
// transcodifica, então nem mandamos pro backend nem usamos pra decidir o "fim".
let duracaoConfiavel = false;
// depois do 'ended' o /concluir já limpou a retomada — impede um save tardio de recriá-la.
let reproducaoConcluida = false;

// Aplicar a retomada só no 'loadedmetadata' não cola: o Chromium volta o
// currentTime pra 0 logo depois. 'loadeddata'/'canplay'/'seeked' já pegam, e
// re-tentamos algumas vezes caso o primeiro seek seja revertido.
function aplicarRetomada() {
  const video = els.video;
  if (!(posicaoParaRetomar > RETOMAR_MIN_SEGUNDOS)) return;
  const dur = video.duration;
  if (duracaoConfiavel && isFinite(dur) && posicaoParaRetomar >= dur - RETOMAR_MARGEM_FIM_S) { posicaoParaRetomar = 0; return; }
  if (Math.abs(video.currentTime - posicaoParaRetomar) <= RETOMAR_TOLERANCIA_S) { posicaoParaRetomar = 0; return; }
  if (retomarTentativas++ > MAX_RETOMAR_TENTATIVAS) { posicaoParaRetomar = 0; return; }
  try { video.currentTime = posicaoParaRetomar; } catch { /* ainda não seekável */ }
}

function configurarProgresso() {
  const video = els.video;

  video.addEventListener('loadeddata', aplicarRetomada);
  video.addEventListener('canplay', aplicarRetomada);
  video.addEventListener('seeked', aplicarRetomada);

  // Clicar em qualquer lugar do player pausa/despausa: na imagem (fora da faixa
  // de baixo, que é a barra de controle nativa) ou nas bordas pretas em volta.
  const alternarPausa = () => { if (video.paused) video.play().catch(() => {}); else video.pause(); };
  video.addEventListener('click', e => {
    const r = video.getBoundingClientRect();
    // ignora a faixa de baixo (barra de controle nativa, ~48-64px)
    if (r.height > 0 && e.clientY > r.bottom - PLAYER_CONTROLS_HEIGHT_PX) return;
    alternarPausa();
  });
  els.player.addEventListener('click', e => {
    if (e.target.id === 'player') alternarPausa();  // bordas pretas em volta do vídeo
  });

  video.addEventListener('timeupdate', () => {
    if (filmeAtual == null || video.paused) return;
    const agora = Date.now();
    if (agora - ultimoSalvo < AUTOSAVE_THROTTLE_MS) return;
    ultimoSalvo = agora;
    salvarProgresso();
  });

  ['pointerdown', 'pointermove', 'touchstart', 'keydown'].forEach(ev => els.player.addEventListener(ev, marcarAtividadeLocal, { passive: true }));
  video.addEventListener('timeupdate', atualizarControlesLocais);
  window.addEventListener('orientationchange', () => setTimeout(atualizarControlesLocais, 250));
  video.addEventListener('loadedmetadata', atualizarControlesLocais);
  window.addEventListener('resize', atualizarControlesLocais);
  video.addEventListener('pause', () => salvarProgresso());
  window.addEventListener('pagehide', () => salvarProgresso());
  video.addEventListener('ended', () => {
    // fim da reprodução: marca assistido + limpa a retomada (funciona também no
    // HLS, onde a regra de "perto do fim" do backend não roda).
    if (filmeAtual == null) return;
    reproducaoConcluida = true;
    const id = filmeAtual;
    fetch(`${API}/${id}/concluir`, { method: 'POST', keepalive: true }).catch(() => {});
    ofereceProximo(id);
  });

  // Keepalive: avisa o backend que ainda tem alguém aqui, senão o transcode em
  // andamento é abortado por inatividade (HlsOrphanTimeoutSeconds).
  setInterval(() => {
    if (filmeAtual != null) fetch(`${API}/${filmeAtual}/assistindo`, { method: 'POST' }).catch(() => {});
  }, KEEPALIVE_MS);
}

function salvarProgresso() {
  const video = els.video;
  if (filmeAtual == null || reproducaoConcluida || !(video.currentTime > 15)) return Promise.resolve();
  return fetch(`${API}/${filmeAtual}/progresso`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({
      posicao: video.currentTime,
      duracao: duracaoConfiavel && isFinite(video.duration) && video.duration > 0 ? video.duration : null
    }),
    keepalive: true
  }).catch(() => {});
}


// ─── Abertura, créditos e próximo episódio no player local ────────────────
// O mesmo que o controle faz pela TV, aqui executado no <video> do celular: o servidor só informa (abertura/créditos/próximo de cada episódio e
// os interruptores da temporada); as decisões são as de util.js / player-regras.js. Nada disso toca no estado da TV (/api/player/*).
const CTA_NEUTRO_VISIVEL_MS = 3500;   // o "30s »" some sozinho depois disso sem toque; "Pular abertura"/"Próximo episódio" ficam enquanto valerem
const SALTO_PADRAO_S = 30;
const PROXIMO_ENSINA_RESTANTE_S = 90;   // apertar "Próximo" a ≤ isto do fim liga o próximo automático da temporada (igual ao servidor)

let ctxLocal = novoContextoLocal();
function novoContextoLocal() {
  return { id: null, filme: null, abertura: null, creditos: null, prox: null, pularAbertura: false, proximoAutomatico: false,
           jaPulou: false, jaProximo: false, cancelado: false, modo: null, atividade: 0 };
}
function reiniciarContextoLocal() {
  ctxLocal = novoContextoLocal();
  const cta = document.getElementById('ctaLocal'), tr = document.getElementById('trilhoLocal'), av = document.getElementById('avisoLocal');
  if (cta) cta.classList.remove('on');
  if (tr) tr.classList.remove('on');
  if (av) av.classList.remove('on');
}

async function carregarContextoLocal(id) {
  const meu = ctxLocal;
  meu.id = id;
  const [filme, abertura, creditos, prox, prefs] = await Promise.all([
    fetchJsonOuNull(`${API}/${id}`), fetchJsonOuNull(`${API}/${id}/abertura`), fetchJsonOuNull(`${API}/${id}/creditos`),
    fetch(`${API}/${id}/proximo`).then(r => r.status === 200 ? r.json() : null).catch(() => null),
    fetchJsonOuNull('/api/series/temporadas'),
  ]);
  if (ctxLocal !== meu || filmeAtual !== id) return;   // trocou de episódio/fechou enquanto buscava
  const lig = filme && filme.serieChave && prefs && prefs[filme.serieChave] ? (prefs[filme.serieChave][filme.temporada] || {}) : {};
  Object.assign(meu, { filme, abertura, creditos, prox, pularAbertura: !!lig.pularAbertura, proximoAutomatico: !!lig.proximoAutomatico });
  atualizarControlesLocais();
}

function posLocal() { return els.video.currentTime || 0; }
function duracaoLocal() { return duracaoConfiavel && isFinite(els.video.duration) ? els.video.duration : 0; }

// Roda a cada timeupdate: pulo automático da abertura, próximo automático nos créditos, botão contextual e faixas da barra.
function atualizarControlesLocais() {
  const c = ctxLocal, video = els.video;
  if (filmeAtual == null || c.id !== filmeAtual) return;
  posicionarSobreVideo();
  const pos = posLocal();

  if (!video.paused && !reproducaoConcluida) {
    const destino = decidirSaltoAbertura(aberturaParaSalto(c.abertura, c.pularAbertura, false), pos, c.jaPulou);
    if (destino !== null) {
      c.jaPulou = true;
      const inicio = c.abertura.inicioSegundos;
      video.currentTime = destino;
      avisoLocal('Abertura pulada', { rotulo: 'Voltar', fazer: () => { video.currentTime = Math.max(0, inicio - 1); } });
    } else if (c.prox && decidirProximoAutomatico(estadoLocalDoPlayer(c), pos, c.jaProximo)) {
      c.jaProximo = true;
      ofereceProximo(filmeAtual);   // a contagem de 5 s (e o "Cancelar") é a mesma oferta do fim do episódio
    }
  }

  const modo = modoDoBotaoDireita(c.abertura, c.creditos, !!c.prox, pos);
  pintarBotaoLocal(modo);
  pintarTrilhoLocal();
}

function pintarBotaoLocal(modo) {
  const b = document.getElementById('ctaLocal');
  if (ctxLocal.modo !== modo) {
    ctxLocal.modo = modo;
    b.className = 'cta' + (modo === 'neutro' ? ' neutro' : '');
    b.textContent = modo === 'abertura' ? '⏭ Pular abertura' : modo === 'proximo' ? 'Próximo episódio ▶' : `${SALTO_PADRAO_S}s »`;
    b.setAttribute('aria-label', modo === 'abertura' ? 'Pular abertura' : modo === 'proximo' ? 'Próximo episódio' : `Avançar ${SALTO_PADRAO_S} segundos`);
  }
  // as formas vermelhas ficam enquanto valem; o neutro só aparece com atividade recente (não polui a imagem)
  const visivel = modo !== 'neutro' || Date.now() - ctxLocal.atividade < CTA_NEUTRO_VISIVEL_MS;
  b.classList.toggle('on', visivel && els.converting.style.display !== 'block');
}

function acaoDoBotaoLocal() {
  const c = ctxLocal, video = els.video;
  if (c.modo === 'abertura') return pularAberturaLocal();
  if (c.modo === 'proximo') return proximoEpisodioLocal();
  video.currentTime = Math.min(posLocal() + SALTO_PADRAO_S, isFinite(video.duration) ? video.duration : Infinity);
}

// O botão: vai pro fim da abertura e liga o pulo automático da temporada (ali é abertura, o dono confirmou) — igual ao /pular-abertura da TV.
async function pularAberturaLocal() {
  const c = ctxLocal;
  if (!c.abertura) return;
  c.jaPulou = true;
  els.video.currentTime = c.abertura.fimSegundos;
  const f = c.filme;
  if (!c.pularAbertura && f && f.serieChave && f.temporada != null) {
    if (await salvarInterruptorLocal(f.serieChave, f.temporada, 'pularAbertura', true)) {
      c.pularAbertura = true;
      avisoLocal(`Pulo automático da abertura ligado na temporada ${f.temporada}.`, { rotulo: 'Desligar', fazer: () => desligarInterruptorLocal('pularAbertura') });
    }
  }
}

// "Próximo episódio": toca já. Se foi apertado nos créditos/no fim, liga o próximo automático da temporada (igual ao servidor).
async function proximoEpisodioLocal() {
  const c = ctxLocal, prox = c.prox, f = c.filme;
  if (!prox) return;
  if (!c.proximoAutomatico && f && f.serieChave && f.temporada != null && proximoManualEnsina(c.creditos, posLocal(), duracaoLocal(), PROXIMO_ENSINA_RESTANTE_S)) {
    if (await salvarInterruptorLocal(f.serieChave, f.temporada, 'proximoAutomatico', true)) {
      c.proximoAutomatico = true;
      avisoLocal(`Próximo automático ligado na temporada ${f.temporada}.`, null);
    }
  }
  cancelarProximo();
  play(prox.id, false, true);
}

async function salvarInterruptorLocal(serie, temp, campo, valor) {
  const r = await fetch(`/api/series/${encodeURIComponent(serie)}/temporadas/${temp}`, {
    method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ [campo]: valor })
  }).catch(() => null);
  return !!r && r.ok;
}

async function desligarInterruptorLocal(campo) {
  const c = ctxLocal, f = c.filme;
  if (!f || !f.serieChave) return;
  if (await salvarInterruptorLocal(f.serieChave, f.temporada, campo, false)) c[campo] = false;
}

// A camada #sobreVideo cobre o elemento <video> (que ocupa a tela toda; a imagem fica centralizada e a barra nativa embaixo): o botão, as faixas e o
// aviso se ancoram na barra, não na imagem.
function posicionarSobreVideo() {
  const r = els.video.getBoundingClientRect(), p = els.player.getBoundingClientRect(), caixa = document.getElementById('sobreVideo');
  caixa.style.left = (r.left - p.left) + 'px'; caixa.style.top = (r.top - p.top) + 'px';
  caixa.style.width = r.width + 'px'; caixa.style.height = r.height + 'px';
}

// Faixas de abertura/créditos sobre a barra: uma linha fina logo acima da barra nativa do <video> (que não dá pra pintar), alinhada à imagem.
function pintarTrilhoLocal() {
  const tr = document.getElementById('trilhoLocal'), c = ctxLocal, dur = duracaoLocal();
  const fa = c.abertura ? faixaEmPercentual(c.abertura.inicioSegundos, c.abertura.fimSegundos, dur) : null;
  const fc = c.creditos ? faixaEmPercentual(c.creditos.inicioSegundos, dur, dur) : null;
  if (!dur || (!fa && !fc)) { tr.classList.remove('on'); return; }
  const pinta = (id, f) => { const z = document.getElementById(id); z.style.display = f ? 'block' : 'none'; if (f) { z.style.left = f.left + '%'; z.style.width = f.width + '%'; } };
  pinta('trilhoAbertura', fa); pinta('trilhoCreditos', fc);
  tr.classList.add('on');
}

// Aviso curto dentro do player ("Abertura pulada · Voltar"), some sozinho.
let avisoTimer = null;
function avisoLocal(texto, acao) {
  const el = document.getElementById('avisoLocal');
  el.innerHTML = `<span>${esc(texto)}</span>${acao ? `<button>${esc(acao.rotulo)}</button>` : ''}`;
  if (acao) el.querySelector('button').onclick = () => { el.classList.remove('on'); acao.fazer(); };
  el.classList.add('on');
  clearTimeout(avisoTimer);
  avisoTimer = setTimeout(() => el.classList.remove('on'), 6000);
}

// Toque/mouse no player: mostra o "30s »" um instante.
let ultimaAtividadeTratada = 0, inativoTimer = null;
function marcarAtividadeLocal() {
  const agora = Date.now();
  ctxLocal.atividade = agora;
  if (agora - ultimaAtividadeTratada < 300) return;   // pointermove dispara demais
  ultimaAtividadeTratada = agora;
  els.player.classList.remove('inativo');
  if (filmeAtual != null) pintarBotaoLocal(ctxLocal.modo || 'neutro');
  clearTimeout(inativoTimer);
  inativoTimer = setTimeout(() => {
    if (filmeAtual == null) return;
    if (!els.video.paused) els.player.classList.add('inativo');   // pausado: os botões ficam
    pintarBotaoLocal(ctxLocal.modo || 'neutro');
  }, CTA_NEUTRO_VISIVEL_MS);
}

// "Preparando vídeo": tempo, barra e a dica de 1ª vez (o servidor converte o contêiner, ~40 s por episódio; depois fica em cache).
function preparandoHtml(segundos, dica) {
  return `<div class="spin" aria-hidden="true"></div><div class="prep-tit">Preparando vídeo…</div>
    <div class="prep-sub">${segundos ? `${segundos}s` : 'começando'}${dica ? esc(dica).replace(/\n/g, '<br>') : '<br>Na primeira vez pode levar alguns segundos; depois fica em cache.'}</div>
    <div class="prep-barra"><div style="width:${Math.min(95, segundos * 100 / 45)}%"></div></div>
    <button onclick="closePlayer()">Cancelar</button>`;
}
