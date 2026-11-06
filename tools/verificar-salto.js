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


// ─── A2: no 'ended' cancelado = sem oferta ───
const noFim = (estado) => { ctx.e = estado; return vm.runInContext('contagemNoFim(e, 12)', ctx); };
caso('fim do vídeo: cancelou o próximo automático neste episódio -> NÃO oferece (null), mesmo com a flag ligada', () => {
  assert.strictEqual(noFim(est({ proximoCancelado: true, proximoContagem: 5 })), null);
  assert.strictEqual(noFim(est({ proximoAutomatico: false, proximoCancelado: true })), null);
});
caso('fim do vídeo: sem cancelamento a contagem é a de sempre (5 s com o automático, 12 s sem)', () => {
  assert.strictEqual(noFim(est({ proximoContagem: 5 })), 5);
  assert.strictEqual(noFim(est({ proximoAutomatico: false })), 12);
  assert.strictEqual(noFim(null), 12);   // 1º poll ainda não chegou: oferta padrão
});

// ─── A1/A6: reinício do servidor ───
const reiniciou = (a, b) => { ctx.e = a; ctx.p = b; return vm.runInContext('servidorReiniciou(e, p)', ctx); };
caso('servidor reiniciou (instancia mudou): a linha de base é descartada', () => {
  assert.strictEqual(reiniciou({ instancia: 'b', seekAcumulado: 0 }, { instancia: 'a', seekAcumulado: 150 }), true);
  assert.strictEqual(reiniciou({ instancia: 'b' }, { }), true);   // anterior de servidor antigo, sem o campo
});
caso('mesma instância ou sem base: reage normalmente', () => {
  assert.strictEqual(reiniciou({ instancia: 'a', seekAcumulado: 180 }, { instancia: 'a', seekAcumulado: 150 }), false);
  assert.strictEqual(reiniciou({ instancia: 'a' }, null), false);
  assert.strictEqual(reiniciou(null, { instancia: 'a' }), false);
});
caso('A6 em números: sem o descarte o acumulado 0 contra 150 seria um recuo de 150 s', () => {
  assert.strictEqual(0 - 150, -150);
  assert.strictEqual(reiniciou({ instancia: 'b', seekAcumulado: 0, seekVersion: 1 }, { instancia: 'a', seekAcumulado: 150, seekVersion: 5 }), true);
});

console.log(`\n${n} casos ok`);
