#!/usr/bin/env python3
"""Inventário de comportamento do FilmesApi (guarda-corpo de refatoração): rotas, páginas, funções do front, chaves de configuração e tabelas.
Uso: python3 tools/gerar-inventario.py > docs/inventario-antes.md   (depois: ... > /tmp/depois.md; diff -u docs/inventario-antes.md /tmp/depois.md)
Não executa nada do projeto: só lê os fontes. Saída ordenada e estável (diff limpo)."""
import re, glob, os, sys
RAIZ = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..')
SRC = os.path.join(RAIZ, 'src', 'FilmesApi')
def ler(p): return open(p, encoding='utf-8').read()
def rel(p): return os.path.relpath(p, RAIZ)

def rotas():
    out = set()
    for f in sorted(glob.glob(os.path.join(SRC, 'Controllers', '*.cs'))):
        s = ler(f)
        for cls in re.finditer(r'(?:\[Route\("([^"]*)"\)\][^{]*?)?\bclass\s+(\w+)[^{]*\{', s):
            pass
        # [Route] da classe (primeiro [Route] do arquivo) + cada [HttpX("...")]
        base = re.search(r'\[Route\("([^"]*)"\)\]', s)
        base = base.group(1) if base else ''
        for m in re.finditer(r'\[Http(Get|Post|Put|Delete|Patch)(?:\("([^"]*)"\))?\]', s):
            caminho = m.group(2) or ''
            full = caminho if caminho.startswith('/') else (base.rstrip('/') + '/' + caminho).rstrip('/')
            out.add(f'{m.group(1).upper():6} {full if full.startswith("/") else "/" + full}   ({os.path.basename(f)})')
    p = ler(os.path.join(SRC, 'Program.cs'))
    for m in re.finditer(r'app\.Map(Get|Post|Put|Delete)\(\s*([^,]+),', p):
        out.add(f'{m.group(1).upper():6} {m.group(2).strip()}   (Program.cs, minimal API)')
    for m in re.finditer(r'MapHealthChecks\("([^"]+)"', p): out.add(f'GET    {m.group(1)}   (health check)')
    return sorted(out)

def paginas():
    return sorted(os.path.relpath(p, os.path.join(SRC, 'wwwroot')) for p in glob.glob(os.path.join(SRC, 'wwwroot', '*.html')))

def funcoes_front():
    res = {}
    arquivos = sorted(glob.glob(os.path.join(SRC, 'wwwroot', 'js', '*.js'))) + sorted(glob.glob(os.path.join(SRC, 'wwwroot', '*.html')))
    for f in arquivos:
        s = ler(f)
        nomes = set(re.findall(r'^\s*(?:async\s+)?function\s+([A-Za-z0-9_$]+)', s, re.M))
        nomes |= set(re.findall(r'^\s*(?:const|let|var)\s+([A-Za-z0-9_$]+)\s*=\s*(?:async\s*)?(?:\([^)]*\)|[A-Za-z0-9_$]+)\s*=>', s, re.M))
        res[rel(f)] = sorted(nomes)
    return res

def configuracao():
    chaves = {}
    for f in sorted(glob.glob(os.path.join(SRC, '**', '*.cs'), recursive=True)):
        for m in re.finditer(r'(?:GetValue<[^>]+>|\.GetValue)\(\s*"([A-Za-z0-9_:]+)"', ler(f)):
            chaves.setdefault(m.group(1), set()).add(os.path.basename(f))
    ap = ler(os.path.join(SRC, 'appsettings.json'))
    for m in re.finditer(r'^\s*"([A-Za-z0-9_]+)"\s*:', ap, re.M): chaves.setdefault(m.group(1), set()).add('appsettings.json')
    return {k: sorted(v) for k, v in sorted(chaves.items())}

def tabelas():
    s = ler(os.path.join(SRC, 'Data', 'DbInitializer.cs')) + ler(os.path.join(SRC, 'Data', 'AppDbContext.cs'))
    t = set(re.findall(r'CREATE TABLE IF NOT EXISTS "(\w+)"', s)) | set(re.findall(r'ToTable\("(\w+)"\)', s)) | set(re.findall(r'DbSet<\w+>\s+(\w+)', s))
    return sorted(t)

def publicos():
    # tipos públicos do back-end (um por linha): serve pra ver o que some numa reorganização
    out = []
    for f in sorted(glob.glob(os.path.join(SRC, '**', '*.cs'), recursive=True)):
        if '/obj/' in f or '/bin/' in f: continue
        for m in re.finditer(r'^\s*(?:public|internal)\s+(?:static\s+|sealed\s+|abstract\s+|partial\s+)*(class|record|enum|interface)\s+(\w+)', ler(f), re.M):
            out.append(f'{m.group(1):9} {m.group(2)}')
    return sorted(set(out))

def metodos_publicos():
    out = set()
    for f in sorted(glob.glob(os.path.join(SRC, 'Services', '*.cs')) + glob.glob(os.path.join(SRC, 'Controllers', '*.cs'))):
        for m in re.finditer(r'^\s*(?:public|internal)\s+(?:static\s+|async\s+|override\s+)*[\w<>\[\]?,\s\.()]+?\s+(\w+)\s*\(', ler(f), re.M):
            if m.group(1) not in ('if', 'for', 'while', 'switch', 'return', 'new'): out.add(m.group(1))
    return sorted(out)

print('# Inventário de comportamento (gerado por tools/gerar-inventario.py)\n')
r = rotas(); print(f'## Rotas HTTP ({len(r)})\n```'); print('\n'.join(r)); print('```\n')
p = paginas(); print(f'## Páginas ({len(p)})\n```'); print('\n'.join(p)); print('```\n')
t = tabelas(); print(f'## Tabelas/DbSets ({len(t)})\n```'); print('\n'.join(t)); print('```\n')
c = configuracao(); print(f'## Chaves de configuração ({len(c)})\n```')
for k, v in c.items(): print(f'{k}  <- {", ".join(v)}')
print('```\n')
fr = funcoes_front(); total = sum(len(v) for v in fr.values()); print(f'## Funções do front ({total})\n')
for f, nomes in fr.items(): print(f'### {f} ({len(nomes)})\n```\n' + '\n'.join(nomes) + '\n```\n')
pb = publicos(); print(f'## Tipos do back-end ({len(pb)})\n```'); print('\n'.join(pb)); print('```\n')
mp = metodos_publicos(); print(f'## Métodos públicos/internal de Services e Controllers ({len(mp)})\n```'); print('\n'.join(mp)); print('```')
