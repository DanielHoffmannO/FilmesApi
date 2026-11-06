#!/usr/bin/env node
// Testa nomeDoIdioma / ordenarPorIdioma (js/util.js): o rótulo em português dos idiomas crus do ffprobe que o controle mostra. Node puro.
const fs = require('fs');
const path = require('path');
const vm = require('vm');
const assert = require('assert');

const ctx = vm.createContext({});
vm.runInContext(fs.readFileSync(path.join(__dirname, '..', 'src', 'FilmesApi', 'wwwroot', 'js', 'util.js'), 'utf8'), ctx);
const nome = (i, t) => { ctx.i = i; ctx.t = t; return vm.runInContext('nomeDoIdioma(i, t)', ctx); };
let n = 0;
const caso = (titulo, fn) => { fn(); n++; console.log(`ok   ${titulo}`); };

caso('códigos do dia a dia', () => {
  const esperados = { por: 'Português', eng: 'Inglês', spa: 'Espanhol', ger: 'Alemão', deu: 'Alemão', fre: 'Francês', fra: 'Francês', rus: 'Russo',
    ara: 'Árabe', ita: 'Italiano', jpn: 'Japonês', kor: 'Coreano', chi: 'Chinês', zho: 'Chinês' };
  for (const [cod, rot] of Object.entries(esperados)) assert.strictEqual(nome(cod, null), rot, cod);
});
caso('maiúsculas e espaços no código não atrapalham', () => assert.strictEqual(nome(' POR ', ''), 'Português'));
caso('português: "Brazilian"/"Brasil" vira (Brasil); título comum não muda nada', () => {
  assert.strictEqual(nome('por', 'Brazilian'), 'Português (Brasil)');
  assert.strictEqual(nome('por', 'Português - Brasil'), 'Português (Brasil)');
  assert.strictEqual(nome('por', 'SDH'), 'Português');
});
caso('espanhol: América Latina e Europa', () => {
  assert.strictEqual(nome('spa', 'Latin American'), 'Espanhol (América Latina)');
  assert.strictEqual(nome('spa', 'European'), 'Espanhol (Europa)');
  assert.strictEqual(nome('spa', null), 'Espanhol');
});
caso('"Saudi Arabia" não muda o idioma (ara -> Árabe)', () => assert.strictEqual(nome('ara', 'Saudi Arabia'), 'Árabe'));
caso('desconhecido: o código em maiúsculas; sem código, o título ou "Desconhecido"', () => {
  assert.strictEqual(nome('xyz', null), 'XYZ');
  assert.strictEqual(nome('und', 'Faixa 3'), 'Faixa 3');
  assert.strictEqual(nome('', null), 'Desconhecido');
  assert.strictEqual(nome(null, undefined), 'Desconhecido');
});
caso('ordem: português, inglês, espanhol, depois o resto em ordem alfabética (estável)', () => {
  const itens = ['rus', 'ger', 'spa', 'eng', 'ara', 'por', 'fre', 'jpn'].map((c, i) => ({ idioma: c, id: i }));
  ctx.itens = itens;
  const ordem = vm.runInContext('ordenarPorIdioma(itens, function (x) { return { idioma: x.idioma, titulo: null }; }).map(function (x) { return x.idioma; })', ctx);
  assert.deepStrictEqual(Array.from(ordem), ['por', 'eng', 'spa', 'ger', 'ara', 'fre', 'jpn', 'rus']);   // Alemão, Árabe, Francês, Japonês, Russo
});
caso('ordem: variantes do mesmo idioma ficam juntas e estáveis', () => {
  ctx.itens = [{ idioma: 'spa', titulo: 'European' }, { idioma: 'eng', titulo: '' }, { idioma: 'por', titulo: 'Brazilian' }, { idioma: 'spa', titulo: 'Latin American' }];
  const ordem = vm.runInContext('ordenarPorIdioma(itens, function (x) { return x; }).map(function (x) { return x.idioma + "/" + x.titulo; })', ctx);
  assert.deepStrictEqual(Array.from(ordem), ['por/Brazilian', 'eng/', 'spa/Latin American', 'spa/European']);   // América Latina < Europa em ordem alfabética
});

console.log(`\n${n} casos ok`);
