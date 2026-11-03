#!/usr/bin/env node
// Testa a regra da sanfona série -> temporada de js/util.js (as 3 telas só desenham a partir dela).
// Uso: node tools/verificar-temporadas.js   (node puro, sem dependência)
const fs = require('fs');
const path = require('path');
const vm = require('vm');
const assert = require('assert');

const ctx = vm.createContext({ console });
vm.runInContext(fs.readFileSync(path.join(__dirname, '..', 'src', 'FilmesApi', 'wwwroot', 'js', 'util.js'), 'utf8'), ctx);
const rodar = (codigo) => JSON.parse(vm.runInContext(`JSON.stringify(${codigo})`, ctx));

const ep = (temporada, episodio, assistido) => ({ temporada, episodio, assistido: !!assistido, rotulo: `E${episodio}` });
const chaves = (gs) => gs.map(g => g.chave);
const agrupar = (eps) => { ctx.eps = eps; return rodar('agruparPorTemporada(eps)'); };
let n = 0;
const caso = (nome, fn) => { fn(); n++; console.log(`ok   ${nome}`); };

caso('agrupa e ordena numericamente (1, 2, 10), Especiais e Outros por último', () => {
  const g = agrupar([ep(10, 1), ep(null, 1), ep(2, 1), ep(0, 1), ep(1, 1), ep(2, 2), ep(1, 2)]);
  assert.deepStrictEqual(chaves(g), ['1', '2', '10', 'especiais', 'outros']);
  assert.deepStrictEqual(g.map(x => x.rotulo), ['Temporada 1', 'Temporada 2', 'Temporada 10', 'Especiais', 'Outros']);
  assert.deepStrictEqual(g[1].episodios.map(e => e.episodio), [1, 2]);   // ordem recebida preservada
});

caso('Mentalista: 7 temporadas, episódios duplos (E23-24) ficam na sua temporada', () => {
  const eps = [];
  for (let t = 1; t <= 7; t++) for (let e = 1; e <= 21; e++) eps.push(ep(t, e));
  eps.push(ep(1, 23), ep(1, 24));
  const g = agrupar(eps);
  assert.strictEqual(g.length, 7);
  assert.strictEqual(g[0].episodios.length, 23);
});

caso('Além da Imaginação (42 episódios, todos temporada 0): um grupo só -> sem nível', () => {
  assert.strictEqual(agrupar(Array.from({ length: 42 }, (_, i) => ep(0, i + 1))), null);
});

caso('uma temporada só, ou só "Outros" (1 episódio): sem nível de temporada', () => {
  assert.strictEqual(agrupar([ep(1, 1), ep(1, 2)]), null);
  assert.strictEqual(agrupar([ep(9, 1)]), null);
  assert.strictEqual(agrupar([ep(null, 1), ep(null, 2)]), null);
});

caso('temporada 0 e null viram grupos distintos', () => {
  assert.deepStrictEqual(chaves(agrupar([ep(null, 1), ep(0, 1), ep(1, 1)])), ['1', 'especiais', 'outros']);
});

const estado = () => rodar('sanfona');
const reset = () => vm.runInContext('sanfona.serieAberta = null; sanfona.temporadaAberta = null', ctx);

caso('começa tudo fechado', () => {
  reset();
  assert.deepStrictEqual(estado(), { serieAberta: null, temporadaAberta: null });
});

caso('abrir série B fecha A; clicar na aberta fecha', () => {
  reset();
  vm.runInContext("alternarSerieAberta('a')", ctx);
  vm.runInContext("alternarSerieAberta('b')", ctx);
  assert.strictEqual(estado().serieAberta, 'b');
  assert.strictEqual(rodar("serieEstaAberta('a')"), false);
  vm.runInContext("alternarSerieAberta('b')", ctx);
  assert.strictEqual(estado().serieAberta, null);
});

caso('abrir temporada 2 fecha a 1; clicar na aberta fecha', () => {
  reset();
  vm.runInContext("alternarSerieAberta('a'); alternarTemporadaAberta('1'); alternarTemporadaAberta('2')", ctx);
  assert.strictEqual(estado().temporadaAberta, '2');
  vm.runInContext("alternarTemporadaAberta('2')", ctx);
  assert.strictEqual(estado().temporadaAberta, null);
});

caso('trocar de série ou fechar a série zera a temporada', () => {
  reset();
  vm.runInContext("alternarSerieAberta('a'); alternarTemporadaAberta('3'); alternarSerieAberta('b')", ctx);
  assert.deepStrictEqual(estado(), { serieAberta: 'b', temporadaAberta: null });
  vm.runInContext("alternarTemporadaAberta('3'); alternarSerieAberta('b')", ctx);
  assert.deepStrictEqual(estado(), { serieAberta: null, temporadaAberta: null });
  vm.runInContext("alternarSerieAberta('b')", ctx);   // reabrir: temporadas fechadas de novo
  assert.strictEqual(rodar("temporadaEstaAberta('3')"), false);
});

caso('série aberta que sumiu do resultado (filtro/busca) não dá erro: tudo fechado', () => {
  reset();
  vm.runInContext("alternarSerieAberta('sumiu'); alternarTemporadaAberta('4')", ctx);
  const html = vm.runInContext(
    "corpoDaSerieHtml([{temporada:1,assistido:true},{temporada:2}], '', function(e){return '<r/>';})", ctx);
  assert.ok(!/data-serie/.test(html));
  assert.strictEqual((html.match(/ collapsed/g) || []).length, 2);   // as duas temporadas fechadas
});

caso('cabeçalho: "Temporada 3 (23 episódios)" e "20/23 vistos"; singular e sem vistos', () => {
  reset();
  const eps = [];
  for (let i = 1; i <= 23; i++) eps.push(ep(3, i, i <= 20));
  eps.push(ep(4, 1));
  ctx.eps = eps;
  const html = vm.runInContext("corpoDaSerieHtml(eps, '<AUDIO>', function(e){return '';})", ctx);
  assert.ok(html.startsWith('<AUDIO>'), 'seletor de áudio fica no topo');
  assert.ok(html.includes('Temporada 3 <span class="count">(23 episódios)</span> <span class="count">20/23 vistos</span>'));
  assert.ok(html.includes('Temporada 4 <span class="count">(1 episódio)</span></div>'));
});

caso('uma temporada só: episódios direto no corpo, sem .temporada', () => {
  ctx.eps = [ep(1, 1), ep(1, 2)];
  const html = vm.runInContext("corpoDaSerieHtml(eps, '<AUDIO>', function(e){return '<r'+e.episodio+'/>';})", ctx);
  assert.strictEqual(html, '<AUDIO><r1/><r2/>');
});

caso('serieHtml: .grupo da série (data-serie, onclick, contagem, corpo); aberto só se for a série aberta', () => {
  reset();
  ctx.s = { chave: 'a&b', nome: 'Série "X"', episodios: [ep(1, 1), ep(2, 1)] };
  const gerar = () => vm.runInContext("serieHtml(s, '<P>', '<AUDIO>', function(e){return '';})", ctx);
  let html = gerar();
  assert.ok(html.startsWith('<div class="grupo collapsed" data-serie="a&amp;b">'));
  assert.ok(html.includes('onclick="cliqueSerie(this)"><span class="caret">▸</span><P>Série &quot;X&quot; <span class="count">(2 episódios)</span>'));
  assert.ok(html.includes('<div class="grupo-body"><AUDIO><div class="grupo temporada collapsed" data-temp="1">'));
  vm.runInContext("alternarSerieAberta('a&b')", ctx);
  html = gerar();
  assert.ok(html.startsWith('<div class="grupo" data-serie="a&amp;b">'));
  assert.ok(html.includes('<span class="caret">▾</span><P>'));
});

console.log(`\n${n} casos ok`);
