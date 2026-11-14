#!/usr/bin/env node
// Testa recuarRetomada (js/util.js): o recuo de alguns segundos ao retomar de onde parou. Node puro, sem dependência.
const fs = require('fs');
const path = require('path');
const vm = require('vm');
const assert = require('assert');

const ctx = vm.createContext({});
vm.runInContext(fs.readFileSync(path.join(__dirname, '..', 'src', 'FilmesApi', 'wwwroot', 'js', 'util.js'), 'utf8'), ctx);
const rec = (pos, recuo, min) => { ctx.p = pos; ctx.r = recuo; ctx.m = min; return vm.runInContext('recuarRetomada(p, r, m)', ctx); };
let n = 0;
const caso = (nome, fn) => { fn(); n++; console.log(`ok   ${nome}`); };

caso('no meio do filme: volta 7 s', () => assert.strictEqual(rec(600, 7, 5), 593));
caso('posição fracionada preserva a fração', () => assert.strictEqual(rec(100.5, 7, 5), 93.5));
caso('recuo que cairia abaixo do mínimo: mantém a posição salva', () => { assert.strictEqual(rec(10, 7, 5), 10); assert.strictEqual(rec(11.9, 7, 5), 11.9); });
caso('limite: resultado exatamente no mínimo recua', () => assert.strictEqual(rec(12, 7, 5), 5));
caso('posição tiny ou zero: inalterada', () => { assert.strictEqual(rec(3, 7, 5), 3); assert.strictEqual(rec(0, 7, 5), 0); });

console.log(`\n${n} casos ok`);
