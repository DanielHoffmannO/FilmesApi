// Regras PURAS do player (sem DOM, ES5): o que o controle da TV (controle.html) e o player local do celular (index.html) decidem do mesmo
// jeito sobre abertura, créditos e o botão da direita. Coberto por tools/verificar-regras.js. As decisões de salto/próximo automático
// (decidirSaltoAbertura, decidirProximoAutomatico, contagemNoFim…) continuam em util.js.

// `abertura` = GET /api/filmes/{id}/abertura ({ inicioSegundos, fimSegundos }) ou null. O botão aparece 5 s antes do início e some 1 s antes do fim.
function aberturaContem(abertura, pos) {
  return !!abertura && pos >= abertura.inicioSegundos - 5 && pos < abertura.fimSegundos - 1;
}

// Créditos de FIM só valem a partir de 2 min e, com a duração conhecida (`dur` > 0), depois de 65% dela: um "Credits" logo no começo é a vinheta
// de abertura (Efeitos Colaterais: 3 s e 33 s), não o fim — fazia o "Próximo episódio" aparecer com 1 minuto de vídeo. Mesma regra do servidor.
var CREDITOS_MINIMO_S = 120;
var CREDITOS_FRACAO_MINIMA = 0.65;
function creditosPlausiveis(creditos, dur) {
  if (!creditos || typeof creditos.inicioSegundos !== 'number') return false;
  return creditos.inicioSegundos >= CREDITOS_MINIMO_S && (!(dur > 0) || creditos.inicioSegundos >= CREDITOS_FRACAO_MINIMA * dur);
}

// `creditos` = GET /api/filmes/{id}/creditos ({ inicioSegundos }) ou null. Só vale se plausível e se há próximo episódio.
function creditosAlcancados(creditos, temProximo, pos, dur) {
  return creditosPlausiveis(creditos, dur) && !!temProximo && pos >= creditos.inicioSegundos;
}

// Forma do botão da direita: 'abertura' (Pular abertura) | 'proximo' (Próximo episódio) | 'neutro' (30s »). Abertura ganha dos créditos.
function modoDoBotaoDireita(abertura, creditos, temProximo, pos, dur) {
  if (aberturaContem(abertura, pos)) return 'abertura';
  if (creditosAlcancados(creditos, temProximo, pos, dur)) return 'proximo';
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
  if (creditosPlausiveis(creditos, dur) && pos >= creditos.inicioSegundos) return true;
  return dur > 0 && dur - pos <= restanteMax;
}

// O estado que as decisões de util.js esperam (o mesmo formato do /api/player/state), montado a partir do contexto do player local.
function estadoLocalDoPlayer(ctx, dur) {
  return {
    proximoAutomatico: !!(ctx && ctx.proximoAutomatico),
    // voltar a imagem (seek pra trás) cancela o automático pro resto do episódio: quem volta quer rever, não pular
    proximoCancelado: !!(ctx && (ctx.cancelado || ctx.seekTras)),
    proximoContagem: 5,
    inicioCreditos: ctx && creditosPlausiveis(ctx.creditos, dur) ? ctx.creditos.inicioSegundos : null
  };
}

// A abertura no formato de decidirSaltoAbertura ({ inicio, fim, pular, pulada }) a partir do GET /abertura e do interruptor da temporada.
function aberturaParaSalto(abertura, pularLigado, jaPulada) {
  if (!abertura) return null;
  return { inicio: abertura.inicioSegundos, fim: abertura.fimSegundos, pular: !!pularLigado, pulada: !!jaPulada };
}
