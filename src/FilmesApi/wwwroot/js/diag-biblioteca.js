// Seção "Saúde da biblioteca" do diagnostico.html: o que a varredura diária (GET /api/diagnostico/biblioteca) achou nos arquivos do acervo —
// truncados, ilegíveis, duplicatas, sem português, transcode pesado — e o botão "verificar agora" (POST .../verificar). Só relata: nada aqui
// apaga nem move arquivo. Depende de esc() (util.js); roda só no diagnostico.html (JS moderno, como a própria página).
(function () {
  var TIPOS = [
    { tipo: 'truncado', titulo: 'Arquivos truncados', aberto: true,
      desc: 'O arquivo acaba antes do fim que ele mesmo declara (cortado no download), ou não há dados perto do fim da duração. "atenção" = só a taxa de bits é absurda pro tamanho.' },
    { tipo: 'ilegivel', titulo: 'Arquivos ilegíveis', aberto: true,
      desc: 'O ffprobe não conseguiu ler o arquivo (corrompido, cortado no começo, ou disco lento). É tentado de novo na próxima varredura.' },
    { tipo: 'duplicado', titulo: 'Duplicatas', aberto: true,
      desc: 'O mesmo conteúdo em mais de um arquivo (mesmo nome em formatos diferentes, ou o mesmo episódio duas vezes). O candidato a manter é só uma sugestão — nada é apagado.' },
    { tipo: 'sem_portugues', titulo: 'Sem português', aberto: false,
      desc: 'Nenhuma faixa de áudio nem legenda em português (faixa sem idioma informado não conta como acusação).' },
    { tipo: 'transcode_pesado', titulo: 'Transcode pesado', aberto: false,
      desc: 'O vídeo não toca nem copiado (codec fora de H.264/VP9/AV1, ou 10-bit): reproduzir exige reencodar o vídeo, o que pesa nesta placa.' },
  ];
  var ROTULO_SEVERIDADE = { ruim: 'ruim', atencao: 'atenção', info: 'info' };

  var timer = null;

  function tempoAtras(iso) {
    if (!iso) return 'nunca';
    var seg = Math.max(0, (Date.now() - new Date(iso).getTime()) / 1000);
    if (seg < 90) return 'agora há pouco';
    if (seg < 5400) return 'há ' + Math.round(seg / 60) + ' min';
    if (seg < 172800) return 'há ' + Math.round(seg / 3600) + ' h';
    return 'há ' + Math.round(seg / 86400) + ' dias';
  }

  function celulaArquivo(i) {
    return '<span class="path">' + esc(i.arquivo) + '</span>';
  }

  function linhaSimples(i) {
    return '<tr><td>' + esc(i.titulo) + '<div>' + celulaArquivo(i) + '</div></td>' +
      '<td><span class="sev sev-' + esc(i.severidade) + '">' + esc(ROTULO_SEVERIDADE[i.severidade] || i.severidade) + '</span></td>' +
      '<td>' + esc(i.detalhe) + '</td></tr>';
  }

  function tabelaSimples(itens) {
    return '<table><tr><th>Arquivo</th><th></th><th>Detalhe</th></tr>' + itens.map(linhaSimples).join('') + '</table>';
  }

  // duplicatas: um bloco por grupo, com todos os arquivos dele e o candidato a manter marcado
  function tabelaDuplicatas(itens) {
    var grupos = [];
    var porChave = {};
    itens.forEach(function (i) {
      var k = i.grupo || ('#' + i.filmeId);
      if (!porChave[k]) { porChave[k] = []; grupos.push(porChave[k]); }
      porChave[k].push(i);
    });
    return '<table><tr><th>Grupo</th><th>Arquivos</th></tr>' + grupos.map(function (g) {
      return '<tr><td>' + esc(g[0].titulo) + '</td><td>' + g.map(function (i) {
        return celulaArquivo(i) + (i.candidatoManter ? ' <span class="cand">candidato a manter</span>' : '') +
          '<div class="size">' + esc(i.detalhe) + '</div>';
      }).join('<hr class="sep">') + '</td></tr>';
    }).join('') + '</table>';
  }

  function secao(def, itens, resumo) {
    var n = resumo[def.tipo] || 0;
    var corpo = '';
    if (itens.length) corpo = def.tipo === 'duplicado' ? tabelaDuplicatas(itens) : tabelaSimples(itens);
    var cabeca = '<h2>' + esc(def.titulo) + '<span class="n">' + n + '</span></h2><div class="desc">' + esc(def.desc) + '</div>';
    if (!itens.length) return '<div class="card ok">' + cabeca + '</div>';
    // o que é só informação (muitos itens) começa recolhido
    return '<div class="card"><details' + (def.aberto ? ' open' : '') + '><summary>' + cabeca + '</summary>' + corpo + '</details></div>';
  }

  function render(r) {
    var el = document.getElementById('biblioteca');
    if (!el) return;
    var itens = r.itens || [];
    var resumo = r.resumo || {};
    var estado = r.geradoEm
      ? r.analisados + ' de ' + r.total + ' arquivo(s) analisado(s) · última varredura ' + tempoAtras(r.geradoEm) +
        (r.ultimaRodadaCompleta ? ' · completa ' + tempoAtras(r.ultimaRodadaCompleta) : ' · ainda sem varredura completa')
      : 'ainda não rodou — a primeira varredura começa sozinha logo depois do servidor subir, ou clique em "verificar agora"';
    var topo = '<h1 style="margin-top:22px">🔎 Saúde da biblioteca</h1>' +
      '<div class="sub">Varredura diária em segundo plano, com prioridade baixa (um arquivo por vez; não roda com transcode em andamento). ' +
      'Só relata: nunca apaga, move nem altera arquivo. <button id="bibVerificar">verificar agora</button> ' +
      '<button id="bibAtualizar">atualizar</button> <span id="bibEstado">' + esc(estado) + (r.rodando ? ' · <b>rodando…</b>' : '') + '</span></div>';
    el.innerHTML = topo + TIPOS.map(function (def) {
      return secao(def, itens.filter(function (i) { return i.tipo === def.tipo; }), resumo);
    }).join('');
    document.getElementById('bibVerificar').disabled = !!r.rodando;
    document.getElementById('bibVerificar').addEventListener('click', verificar);
    document.getElementById('bibAtualizar').addEventListener('click', function () { carregar(); });
  }

  // enquanto uma varredura roda, olha de novo a cada poucos segundos
  function agendar(rodando) {
    if (timer) { clearTimeout(timer); timer = null; }
    if (rodando) timer = setTimeout(carregar, 4000);
  }

  async function carregar() {
    var el = document.getElementById('biblioteca');
    if (!el) return;
    var r = await fetchJsonOuNull('/api/diagnostico/biblioteca');
    if (!r) { el.innerHTML = '<div class="card"><h2>Saúde da biblioteca</h2><div class="desc">Não consegui carregar o relatório.</div></div>'; return; }
    render(r);
    agendar(r.rodando);
  }

  // Dispara a varredura. Com AdminToken no servidor, pede o token uma vez (o mesmo do scan do index e do "aplicar" do diagnóstico).
  async function verificar() {
    var botao = document.getElementById('bibVerificar');
    var headers = function () {
      try { var t = localStorage.getItem('adminToken'); return t ? { 'X-Admin-Token': t } : {}; } catch (e) { return {}; }
    };
    var enviar = function () { return fetch('/api/diagnostico/biblioteca/verificar', { method: 'POST', headers: headers() }); };
    botao.disabled = true;
    try {
      var r = await enviar();
      if (r.status === 401) {
        var token = prompt('O servidor pede um token de administrador:');
        if (!token) { botao.disabled = false; return; }
        try { localStorage.setItem('adminToken', token); } catch (e) { /* vale só desta vez */ }
        r = await enviar();
      }
      if (!r.ok) {
        var m = await r.json().catch(function () { return null; });
        alert((m && (m.mensagem || m.title)) || 'Não consegui começar a verificação.');
      }
    } catch (e) { alert('Não consegui falar com o servidor.'); }
    carregar();
  }

  window.carregarBiblioteca = carregar;
  carregar();
})();
