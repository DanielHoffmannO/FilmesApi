// Regras PURAS do player (sem DOM, ES5): o que o controle da TV (controle.html) e o player local do celular (index.html) decidem do mesmo
// jeito sobre abertura, créditos e o botão da direita. Coberto por tools/verificar-regras.js. As decisões de salto/próximo automático
// (decidirSaltoAbertura, decidirProximoAutomatico, contagemNoFim…) continuam em util.js.

// `abertura` = GET /api/filmes/{id}/abertura ({ inicioSegundos, fimSegundos }) ou null. O botão aparece 5 s antes do início e some 1 s antes do fim.
function aberturaContem(abertura, pos) {
  return !!abertura && pos >= abertura.inicioSegundos - 5 && pos < abertura.fimSegundos - 1;
}

// `creditos` = GET /api/filmes/{id}/creditos ({ inicioSegundos }) ou null. Só vale se há próximo episódio.
function creditosAlcancados(creditos, temProximo, pos) {
  return !!creditos && !!temProximo && pos >= creditos.inicioSegundos;
}

// Forma do botão da direita: 'abertura' (Pular abertura) | 'proximo' (Próximo episódio) | 'neutro' (30s »). Abertura ganha dos créditos.
function modoDoBotaoDireita(abertura, creditos, temProximo, pos) {
  if (aberturaContem(abertura, pos)) return 'abertura';
  if (creditosAlcancados(creditos, temProximo, pos)) return 'proximo';
  return 'neutro';
}

// Faixa [ini, fim] sobre uma barra de duração `dur`, em % ({ left, width }); null se falta dado (a faixa some).
function faixaEmPercentual(ini, fim, dur) {
  if (ini == null || !dur) return null;
  return { left: 100 * ini / dur, width: 100 * (fim - ini) / dur };
}

// "Próximo episódio" apertado: vale como "é nessa hora que o Daniel pula" (liga o próximo automático da temporada) quando já está nos créditos
// ou restam `restanteMax` s ou menos — apertar no meio do episódio só toca o próximo, não ensina nada (mesma regra do servidor, ProximoManual).
function proximoManualEnsina(creditos, pos, dur, restanteMax) {
  if (creditos && pos >= creditos.inicioSegundos) return true;
  return dur > 0 && dur - pos <= restanteMax;
}

// O estado que as decisões de util.js esperam (o mesmo formato do /api/player/state), montado a partir do contexto do player local.
function estadoLocalDoPlayer(ctx) {
  return {
    proximoAutomatico: !!(ctx && ctx.proximoAutomatico),
    proximoCancelado: !!(ctx && ctx.cancelado),
    proximoContagem: 5,
    inicioCreditos: ctx && ctx.creditos ? ctx.creditos.inicioSegundos : null
  };
}

// A abertura no formato de decidirSaltoAbertura ({ inicio, fim, pular, pulada }) a partir do GET /abertura e do interruptor da temporada.
function aberturaParaSalto(abertura, pularLigado, jaPulada) {
  if (!abertura) return null;
  return { inicio: abertura.inicioSegundos, fim: abertura.fimSegundos, pular: !!pularLigado, pulada: !!jaPulada };
}
