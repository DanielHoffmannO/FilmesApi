#!/usr/bin/env node
// Testa js/player-regras.js: as regras puras de abertura/créditos/botão da direita compartilhadas pelo controle e pelo player local. Node puro.
const fs = require('fs');
const path = require('path');
const vm = require('vm');
const assert = require('assert');

const ctx = vm.createContext({});
const wwwroot = path.join(__dirname, '..', 'src', 'FilmesApi', 'wwwroot', 'js');
['util.js', 'player-regras.js'].forEach(f => vm.runInContext(fs.readFileSync(path.join(wwwroot, f), 'utf8'), ctx));
const chama = (expr, vars) => { Object.assign(ctx, vars); return vm.runInContext(expr, ctx); };
let n = 0;
const caso = (nome, fn) => { fn(); n++; console.log(`ok   ${nome}`); };
const ab = { inicioSegundos: 100, fimSegundos: 190 };
const cr = { inicioSegundos: 1200 };

caso('abertura: aparece 5 s antes do início e some 1 s antes do fim', () => {
  const dentro = pos => chama('aberturaContem(a, p)', { a: ab, p: pos });
  assert.strictEqual(dentro(94.9), false); assert.strictEqual(dentro(95), true); assert.strictEqual(dentro(150), true);
  assert.strictEqual(dentro(188.9), true); assert.strictEqual(dentro(189), false);
});
caso('abertura: sem abertura conhecida nunca contém', () => assert.strictEqual(chama('aberturaContem(null, 120)'), false));
caso('créditos: só valem com próximo episódio e a partir do início', () => {
  const nos = (c, t, p) => chama('creditosAlcancados(c, t, p)', { c, t, p });
  assert.strictEqual(nos(cr, true, 1199), false); assert.strictEqual(nos(cr, true, 1200), true);
  assert.strictEqual(nos(cr, false, 1300), false); assert.strictEqual(nos(null, true, 1300), false);
});
caso('botão da direita: abertura ganha dos créditos; senão créditos; senão neutro', () => {
  const modo = (a, c, t, p) => chama('modoDoBotaoDireita(a, c, t, p)', { a, c, t, p });
  assert.strictEqual(modo(ab, cr, true, 120), 'abertura');
  assert.strictEqual(modo(ab, cr, true, 1250), 'proximo');
  assert.strictEqual(modo(ab, cr, true, 600), 'neutro');
  assert.strictEqual(modo(ab, cr, false, 1250), 'neutro');
  assert.strictEqual(modo(ab, { inicioSegundos: 120 }, true, 120), 'abertura');   // sobreposição: pula a abertura primeiro
});
caso('faixa em %: posição e largura, e some sem dado', () => {
  const f = chama('faixaEmPercentual(100, 200, 1000)');
  assert.strictEqual(f.left, 10); assert.strictEqual(f.width, 10);
  assert.strictEqual(chama('faixaEmPercentual(null, 200, 1000)'), null);
  assert.strictEqual(chama('faixaEmPercentual(100, 200, 0)'), null);
  assert.strictEqual(chama('faixaEmPercentual(undefined, 200, 1000)'), null);
});
caso('próximo manual só ensina nos créditos ou no fim', () => {
  const ensina = (c, p, d) => chama('proximoManualEnsina(c, p, d, PROXIMO_ENSINA_RESTANTE_PADRAO_S)', { c, p, d });   // 180 s, o padrão do servidor (ProximoManual)
  assert.strictEqual(ensina(cr, 1250, 1500), true);
  assert.strictEqual(ensina(null, 1450, 1500), true);     // restam 50 s
  assert.strictEqual(ensina(null, 1330, 1500), true);     // restam 170 s: dentro dos 180 do servidor (com 90 não ensinava)
  assert.strictEqual(ensina(null, 1310, 1500), false);    // restam 190 s
  assert.strictEqual(ensina(null, 600, 1500), false);     // meio do episódio: só toca
  assert.strictEqual(ensina(cr, 600, 1500), false);
  assert.strictEqual(ensina(null, 600, 0), false);        // duração desconhecida (HLS)
});
caso('estado local e abertura-para-salto alimentam as decisões de util.js', () => {
  const est = chama('estadoLocalDoPlayer(c)', { c: { proximoAutomatico: true, cancelado: false, creditos: cr } });
  assert.strictEqual(chama('decidirProximoAutomatico(e, 1250, false)', { e: est }), true);
  assert.strictEqual(chama('decidirProximoAutomatico(e, 1250, true)', { e: est }), false);
  assert.strictEqual(chama('contagemNoFim(e, 12)', { e: est }), 5);
  const cancelado = chama('estadoLocalDoPlayer(c)', { c: { proximoAutomatico: true, cancelado: true, creditos: cr } });
  assert.strictEqual(chama('contagemNoFim(e, 12)', { e: cancelado }), null);   // cancelou: não oferece de novo
  const sem = chama('estadoLocalDoPlayer(null)');
  assert.strictEqual(chama('contagemNoFim(e, 12)', { e: sem }), 12);
  const salto = (pular, pulada, pos, jaPulou) => chama('decidirSaltoAbertura(aberturaParaSalto(a, p, j), pos, ja)', { a: ab, p: pular, j: pulada, pos, ja: jaPulou });
  assert.strictEqual(salto(true, false, 120, false), 190);
  assert.strictEqual(salto(false, false, 120, false), null);
  assert.strictEqual(salto(true, true, 120, false), null);
  assert.strictEqual(salto(true, false, 120, true), null);
  assert.strictEqual(chama('aberturaParaSalto(null, true, false)'), null);
});

caso('créditos implausíveis (começo do episódio) nunca disparam o botão nem o automático', () => {
  const ruim = { inicioSegundos: 33.1 };   // Efeitos Colaterais S01E03: capítulo "Credits" da vinheta
  assert.strictEqual(chama('creditosPlausiveis(c, 1322)', { c: ruim }), false);
  assert.strictEqual(chama('creditosAlcancados(c, true, 70, 1322)', { c: ruim }), false);
  assert.strictEqual(chama('modoDoBotaoDireita(null, c, true, 70, 1322)', { c: ruim }), 'neutro');
  assert.strictEqual(chama('creditosPlausiveis(c, 1322)', { c: { inicioSegundos: 1200 } }), true);
  assert.strictEqual(chama('creditosPlausiveis(c, 1322)', { c: { inicioSegundos: 700 } }), false);   // antes de 65% da duração
  assert.strictEqual(chama('creditosPlausiveis(c, 0)', { c: { inicioSegundos: 700 } }), true);       // HLS: duração desconhecida, vale o piso de 2 min
  const est = chama('estadoLocalDoPlayer(c, 1322)', { c: { proximoAutomatico: true, creditos: ruim } });
  assert.strictEqual(est.inicioCreditos, null);
  assert.strictEqual(chama('decidirProximoAutomatico(e, 70, false, 1322)', { e: est }), false);
});
caso('próximo automático: nunca antes de 50% da duração, nem depois de voltar a imagem', () => {
  const est = { proximoAutomatico: true, proximoCancelado: false, inicioCreditos: 300 };   // créditos "plausíveis" mas a duração diz que é cedo
  assert.strictEqual(chama('decidirProximoAutomatico(e, 400, false, 1322)', { e: est }), false);
  assert.strictEqual(chama('decidirProximoAutomatico(e, 700, false, 1322)', { e: est }), true);
  assert.strictEqual(chama('decidirProximoAutomatico(e, 400, false)', { e: est }), true);   // sem duração (TV, HLS): comportamento de sempre
  const voltou = chama('estadoLocalDoPlayer(c, 1322)', { c: { proximoAutomatico: true, seekTras: true, creditos: { inicioSegundos: 1200 } } });
  assert.strictEqual(chama('decidirProximoAutomatico(e, 1250, false, 1322)', { e: voltou }), false);
  assert.strictEqual(chama('contagemNoFim(e, 12)', { e: voltou }), null);   // (o player local passa seekTras:false no 'ended': o fim de verdade sempre oferece)
});

console.log(`${n} casos ok`);
