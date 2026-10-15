#!/usr/bin/env node
// A TV roda o tv.html num navegador antigo: const/let/arrow/template string/async quebram a página
// INTEIRA e em silêncio (a TV não tem console). Este script faz o parser (acorn) recusar qualquer
// coisa acima de ES5 no script do tv.html e no util.js (que a TV também carrega).
// Uso: node tools/verificar-es5.js   (precisa de node/npx; baixa o acorn na primeira vez)
const fs = require('fs');
const os = require('os');
const path = require('path');
const { spawnSync } = require('child_process');

const raiz = path.join(__dirname, '..', 'src', 'FilmesApi', 'wwwroot');
const tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'es5-'));
const alvos = [];

// scripts inline do tv.html (os com src= ficam de fora: vendor/hls.min.js é de terceiros)
const html = fs.readFileSync(path.join(raiz, 'tv.html'), 'utf8');
const inline = [...html.matchAll(/<script(?![^>]*\bsrc=)[^>]*>([\s\S]*?)<\/script>/g)].map(m => m[1]);
inline.forEach((codigo, i) => {
  const arq = path.join(tmp, `tv-inline-${i}.js`);
  fs.writeFileSync(arq, codigo);
  alvos.push([`tv.html (script inline ${i + 1})`, arq]);
});
alvos.push(['js/util.js', path.join(raiz, 'js', 'util.js')]);

let falhou = false;
for (const [nome, arq] of alvos) {
  const r = spawnSync('npx', ['--yes', 'acorn@8', '--ecma5', '--silent', arq], { encoding: 'utf8' });
  if (r.status === 0) { console.log(`ok   ${nome}`); continue; }
  falhou = true;
  console.error(`ERRO ${nome}: ${(r.stderr || r.stdout || '').trim().replace(new RegExp(tmp, 'g'), '<tmp>')}`);
}
fs.rmSync(tmp, { recursive: true, force: true });
process.exit(falhou ? 1 : 0);
