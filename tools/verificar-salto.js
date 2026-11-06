#!/usr/bin/env node
// Testa decidirSaltoAbertura (js/util.js): a regra do pulo automático da abertura que a TV executa. Node puro, sem dependência.
const fs = require('fs');
const path = require('path');
const vm = require('vm');
const assert = require('assert');

const ctx = vm.createContext({});
vm.runInContext(fs.readFileSync(path.join(__dirname, '..', 'src', 'FilmesApi', 'wwwroot', 'js', 'util.js'), 'utf8'), ctx);
const decidir = (abertura, pos, jaPulou) => { ctx.a = abertura; ctx.p = pos; ctx.j = !!jaPulou; return vm.runInContext('decidirSaltoAbertura(a, p, j)', ctx); };
const ab = (extra) => Object.assign({ inicio: 100, fim: 190, pular: true, pulada: false }, extra);
let n = 0;
const caso = (nome, fn) => { fn(); n++; console.log(`ok   ${nome}`); };

caso('dentro da abertura com a flag ligada: salta pro fim', () => assert.strictEqual(decidir(ab(), 120, false), 190));
caso('1 s antes do início já salta (a posição só é checada a cada poll)', () => assert.strictEqual(decidir(ab(), 99.2, false), 190));
caso('antes do início ou depois do fim: não faz nada', () => { assert.strictEqual(decidir(ab(), 50, false), null); assert.strictEqual(decidir(ab(), 190, false), null); assert.strictEqual(decidir(ab(), 400, false), null); });
caso('flag desligada na temporada: não salta', () => assert.strictEqual(decidir(ab({ pular: false }), 120, false), null));
caso('sem abertura conhecida: não salta', () => { assert.strictEqual(decidir(null, 120, false), null); assert.strictEqual(decidir(undefined, 120, false), null); });
caso('1x por reprodução: depois de pular, voltar pra dentro NÃO repete', () => {
  assert.strictEqual(decidir(ab(), 120, false), 190);
  assert.strictEqual(decidir(ab(), 120, true), null);
});
caso('o servidor já marcou como pulada (botão do controle): a TV não repete', () => assert.strictEqual(decidir(ab({ pulada: true }), 120, false), null));
caso('retomar o episódio no meio da abertura também salta', () => assert.strictEqual(decidir(ab(), 150, false), 190));


// ─── próximo automático ───
const prox = (estado, pos, jaFez) => { ctx.e = estado; ctx.p = pos; ctx.j = !!jaFez; return vm.runInContext('decidirProximoAutomatico(e, p, j)', ctx); };
const est = (extra) => Object.assign({ proximoAutomatico: true, proximoCancelado: false, inicioCreditos: 1200 }, extra);
caso('próximo automático: ao entrar nos créditos com a flag ligada', () => { assert.strictEqual(prox(est(), 1200, false), true); assert.strictEqual(prox(est(), 1290, false), true); });
caso('próximo automático: antes dos créditos não faz nada', () => assert.strictEqual(prox(est(), 1199, false), false));
caso('próximo automático: flag desligada, sem créditos conhecidos ou sem estado', () => {
  assert.strictEqual(prox(est({ proximoAutomatico: false }), 1250, false), false);
  assert.strictEqual(prox(est({ inicioCreditos: null }), 1250, false), false);
  assert.strictEqual(prox(null, 1250, false), false);
});
caso('próximo automático: 1x por episódio e cancelado pelo Daniel não repete', () => {
  assert.strictEqual(prox(est(), 1250, true), false);
  assert.strictEqual(prox(est({ proximoCancelado: true }), 1250, false), false);
});
const contagem = (estado) => { ctx.e = estado; return vm.runInContext('contagemDoProximo(e, 12)', ctx); };
caso('contagem no fim: 5 s com o próximo automático ligado, 12 s (padrão) sem ele ou cancelado', () => {
  assert.strictEqual(contagem(est({ proximoContagem: 5 })), 5);
  assert.strictEqual(contagem(est({ proximoContagem: 3 })), 3);
  assert.strictEqual(contagem(est({ proximoAutomatico: false, proximoContagem: 5 })), 12);
  assert.strictEqual(contagem(est({ proximoCancelado: true, proximoContagem: 5 })), 12);
  assert.strictEqual(contagem(est()), 12);   // servidor antigo sem proximoContagem
  assert.strictEqual(contagem(null), 12);
});

console.log(`\n${n} casos ok`);
