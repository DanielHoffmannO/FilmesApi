using FilmesApi.Services;

namespace FilmesApi.Tests;

/// <summary>
/// Corpus do <see cref="MediaNomeParser"/> — a peça que mais quebra com nome de arquivo do
/// mundo real. Casos reais (Breaking Bad em 3 layouts diferentes) + armadilhas conhecidas
/// (ano no título, resolução parecendo NxNN, pasta de filmes numerados).
/// </summary>
public class MediaNomeParserTests
{
    // ─── OrdemEpisodio: o que é episódio e em que ordem ──────────────────

    [Theory]
    // SxxExx no nome
    [InlineData("Better Call Saul/BCS.S01E03.1080p.mkv", 1, 3)]
    [InlineData("Show S02E10.mkv", 2, 10)]
    [InlineData("algo/Show.s3e7.mkv", 3, 7)]
    // NxNN no nome
    [InlineData("Serie/Serie 1x02 piloto.mkv", 1, 2)]
    [InlineData("Serie/3x14 - final.mkv", 3, 14)]
    // "Episódio N"
    [InlineData("Anime/Episódio 5.mkv", 0, 5)]
    [InlineData("Anime/Capitulo 12 - x.mkv", 0, 12)]
    // número solto no nome + temporada NA PASTA (caso Breaking Bad "The Pirate Filmes")
    [InlineData("Breaking Bad 3 Temporada - The Pirate Filmes/8 - I See You.mp4", 3, 8)]
    [InlineData("Breaking Bad 5 Temporada Parte 2 -  The Pirate Filmes/14 - Ozymandias.mp4", 5, 14)]
    [InlineData("Show/2ª Temporada/07. Titulo.mkv", 2, 7)]
    [InlineData("Show/Season 4/[12] Titulo.mkv", 4, 12)]
    // SxxExx ganha da temporada da pasta
    [InlineData("Breaking Bad 2011 4ª Temporada Completa [WWW.BLUDV.COM]/Breaking.Bad.2011.S04E09.720p.BluRay.x264.DUAL.mkv", 4, 9)]
    // "Temp N - Epi M" (Hora de Aventura, Apenas um Show) -- sem "S/E" nem palavra por extenso
    [InlineData("Hora de Aventura 1ª Temporada/Temp 01 - Epi 03 - Prisioneiras do Amor.mkv", 1, 3)]
    // "p1"/"p2" de release dividida em 2 partes -- mesma temporada, ignora o sufixo
    [InlineData("Hora de Aventura 5ª Temporada/Temp 05p1 - Epi 08 - Masmorra do Mistério.mkv", 5, 8)]
    // episódio combinado (2+ episódios grudados no mesmo arquivo) -- usa o primeiro como representante
    [InlineData("Serie/S07E14E15 - Titulo Combinado.mkv", 7, 14)]
    [InlineData("Serie 10 Temporada - Site/13E14E15E16 - Venha Comigo.mkv", 10, 13)]
    public void OrdemEpisodio_reconhece(string path, int temp, int ep)
    {
        Assert.Equal((temp, ep), MediaNomeParser.OrdemEpisodio(path));
    }

    [Theory]
    [InlineData("Filmes/Interestelar 2014 1080p Dublado.mkv")]        // filme solto
    [InlineData("Colecao Rocky/1 - Rocky (1976).mp4")]                 // pasta de filmes numerados, SEM token de temporada
    [InlineData("Colecao Rocky/2 - Rocky II (1979).mp4")]
    [InlineData("Filmes/Blade Runner 2049 (2017) 2160p.mkv")]         // "2049" não é NxNN
    [InlineData("Filmes/1917 (2019).mkv")]                            // número puro no começo, sem separador
    [InlineData("Filmes/2001 A Space Odyssey.mkv")]
    [InlineData("Documentarios/Cosmos 1980 1x01.mkv")]                // tem NxNN mas... (ver nota no teste)
    public void OrdemEpisodio_nao_confunde_filme_com_episodio(string path)
    {
        // "Cosmos 1980 1x01" REALMENTE casa NxNN — é o preço de aceitar "1x01".
        // Mantido aqui só como lembrete; se um dia o parser ficar mais esperto, trocar p/ Null.
        if (path.Contains("1x01")) { Assert.NotNull(MediaNomeParser.OrdemEpisodio(path)); return; }
        Assert.Null(MediaNomeParser.OrdemEpisodio(path));
    }

    [Theory]
    [InlineData("Serie/Show.720p.x264.mkv")]      // 720p / x264 não são NxNN
    [InlineData("Serie/Show 1920x1080.mkv")]      // resolução não é NxNN
    public void OrdemEpisodio_ignora_resolucao_e_codec(string path)
    {
        Assert.Null(MediaNomeParser.OrdemEpisodio(path));
    }

    // ─── OrdemEpisodio com contagem: antologia sem NENHUM marcador de temporada ─────────
    // Regressão real: "Além da Imaginação" (43 episódios, só "01 Título.avi" — nem hífen/ponto
    // separando número de título, e nenhuma pasta "Temporada N"/"Season N" em lugar nenhum)
    // nunca virava série — cada arquivo caía como filme solto e a pasta inteira (43 "filmes")
    // "achatava" como pasta de filme+extras. Estruturalmente indistinguível de uma coleção de
    // filme numerada (Coleção Rocky) sem olhar pra QUANTOS arquivos assim tem na pasta.

    [Fact]
    public void OrdemEpisodio_numero_com_so_espaco_sem_contagem_nao_vira_episodio()
    {
        // Sem o parâmetro de contagem (default 0, comportamento de sempre) um "01 Título.avi"
        // solto continua filme — cobre quem ainda chama OrdemEpisodio(path) só com 1 argumento.
        Assert.Null(MediaNomeParser.OrdemEpisodio("Extra/Alem da Imaginação/01 O Paraíso Verde.avi"));
    }

    [Fact]
    public void OrdemEpisodio_poucos_arquivos_numerados_continua_colecao_de_filme()
    {
        // Mesmo formato de nome da Coleção Rocky, mas explícito com a contagem real da pasta
        // (2) -- bem abaixo do limiar, continua null.
        Assert.Null(MediaNomeParser.OrdemEpisodio("Colecao Rocky/1 - Rocky (1976).mp4", arquivosNumeradosNaPasta: 2));
    }

    [Fact]
    public void OrdemEpisodio_muitos_arquivos_numerados_vira_episodio_mesmo_sem_separador()
    {
        var r = MediaNomeParser.OrdemEpisodio(
            "Extra/Alem da Imaginação/01 O Paraíso Verde.avi", arquivosNumeradosNaPasta: 43);
        Assert.Equal((0, 1), r);
    }

    [Fact]
    public void OrdemEpisodio_pasta_com_temporada_explicita_nunca_usa_o_fallback_de_antologia()
    {
        // Se a pasta já diz a temporada, o tier normal (ReEpPrefixo, com separador exigido)
        // já resolve -- o fallback frouxo de antologia só entra quando NÃO há temporada
        // nenhuma na pasta, pra não abrir uma porta de falso positivo desnecessária ali.
        var r = MediaNomeParser.OrdemEpisodio(
            "Show 2 Temporada/01 Sem Separador.mkv", arquivosNumeradosNaPasta: 43);
        Assert.Null(r);
    }

    [Fact]
    public void ContarNumeradosPorPasta_agrupa_por_pasta_e_ignora_nomes_sem_numero()
    {
        var contagem = MediaNomeParser.ContarNumeradosPorPasta([
            "Extra/Alem da Imaginação/01 Título.avi",
            "Extra/Alem da Imaginação/02 Título.avi",
            "Colecao Rocky/1 - Rocky (1976).mp4",
            "Filmes/Interestelar (2014).mkv",  // sem número no início -- não conta
            null,
        ]);

        Assert.Equal(2, contagem["Extra/Alem da Imaginação"]);
        Assert.Equal(1, contagem["Colecao Rocky"]);
        Assert.False(contagem.ContainsKey("Filmes"));
    }

    [Fact]
    public void Classificar_usa_a_contagem_pra_decidir_antologia()
    {
        // Título sem nenhuma outra palavra-gatilho ("Episódio"/"Capítulo" já bate ReEpNum
        // sozinho, sem precisar da contagem) -- só o número solto no início mesmo.
        var arquivos = Enumerable.Range(1, 20)
            .Select(i => $"Extra/Alem da Imaginação/{i:00} Um Titulo Qualquer {i}.avi")
            .ToList();
        var contagem = MediaNomeParser.ContarNumeradosPorPasta(arquivos);

        var c = MediaNomeParser.Classificar(arquivos[0], "01 Um Titulo Qualquer 1");

        Assert.False(c.EhEpisodio);  // sem passar a contagem, comportamento de sempre

        var comContagem = MediaNomeParser.Classificar(arquivos[0], "01 Um Titulo Qualquer 1", contagem);
        Assert.True(comContagem.EhEpisodio);
        Assert.Equal(0, comContagem.Temporada);
        Assert.Equal(1, comContagem.Episodio);
    }

    // ─── ChaveSerie: nome pra agrupar ───────────────────────────────────

    [Theory]
    [InlineData("Breaking Bad 3 Temporada - The Pirate Filmes/8 - I See You.mp4", "Breaking Bad")]
    [InlineData("Breaking Bad 5 Temporada Parte 2 -  The Pirate Filmes/14 - Ozymandias.mp4", "Breaking Bad")]
    [InlineData("Breaking Bad 2011 4ª Temporada Completa [WWW.BLUDV.COM]/Breaking.Bad.2011.S04E09.mkv", "Breaking Bad")]
    [InlineData("Better Call Saul/Season 1/BCS.S01E03.mkv", "Better Call Saul")]
    [InlineData("The Office (US)/S03/The.Office.S03E02.mkv", "The Office (US)")]
    [InlineData("Show.S01E01.mkv", "Show")]
    public void ChaveSerie(string path, string esperado)
    {
        Assert.Equal(esperado, MediaNomeParser.ChaveSerie(path));
    }

    [Fact]
    public void ChaveSerie_agrupa_temporadas_em_subpastas()
    {
        var s1 = MediaNomeParser.ChaveSerie("Breaking Bad/Season 1/BB.S01E07.mkv");
        var s2 = MediaNomeParser.ChaveSerie("Breaking Bad/Season 2/BB.S02E01.mkv");
        Assert.Equal(s1, s2);
        Assert.Equal("Breaking Bad", s1);
    }

    // Regressão: "Avatar A Lenda de Aang/Livro 1 - Água/S1E02....mkv" — a pasta do arco
    // ("Livro N - Nome") não é só "Livro N", tem mais texto depois do hífen. Sem reconhecer
    // esse prefixo, ChaveSerie ficava presa em "Livro 1" e nunca subia pra pasta da série de
    // verdade — 3 arcos viravam 3 séries distintas em vez de 1 com 3 "temporadas".
    [Theory]
    [InlineData("Avatar A Lenda de Aang/Livro 1 - Água/S1E02 - A Volta Do Avatar.mkv")]
    [InlineData("Avatar A Lenda de Aang/Livro 2 - Terra/S2E01 - O Estado Avatar.mkv")]
    [InlineData("Avatar A Lenda de Aang/Livro 3 - Fogo/S3E06 - O Avatar e o Senhor do Fogo.mkv")]
    public void ChaveSerie_sobe_pra_pasta_pai_quando_segmento_e_arco_tipo_livro(string path)
    {
        Assert.Equal("Avatar A Lenda de Aang", MediaNomeParser.ChaveSerie(path));
    }

    // Regressão: cada episódio do Severance veio numa pasta própria de release, nome igual
    // ao arquivo ("Severance.S02E05.1080p.WEB-DL.DUAL.5.1") — sem cortar no marcador SxxExx,
    // ChaveSerie devolvia a pasta inteira (com resolução/codec/áudio), e cada episódio virava
    // uma "série" de 1 só, em vez de 5 episódios da mesma série.
    [Theory]
    [InlineData("Severance.S02E01.1080p.WEB-DL.DUAL.5.1/Severance.S02E01.1080p.WEB-DL.DUAL.5.1.mkv")]
    [InlineData("Severance.S02E05.1080p.WEB-DL.DUAL.5.1/Severance.S02E05.1080p.WEB-DL.DUAL.5.1.mkv")]
    public void ChaveSerie_corta_pasta_de_release_no_marcador_sxxexx(string path)
    {
        Assert.Equal("Severance", MediaNomeParser.ChaveSerie(path));
    }

    // Regressão real: "Hora da Aventura - TORRENTMEGAFILMES/7ª Temporada - Completa -
    // TORRENTMEGAFILMES/S07E05....mkv" — o nível do meio ("7ª Temporada - Completa - SITE")
    // começa com ordinal+"Temporada" (não bate ReSegmentoTemporadaPrefixo, que exige a palavra
    // ANTES do número), então ChaveSerie tentava limpar ali mesmo e o corte (que casa "7ª
    // Temporada" também) comia a pasta inteira, sobrando "" — o código antigo desistia e caía
    // no fallback de "arquivo solto", devolvendo o NOME DO ARQUIVO inteiro (com ".mkv" e tudo)
    // como se fosse a série. Agora sobe mais um nível quando um segmento limpa pra vazio.
    [Fact]
    public void ChaveSerie_sobe_mais_um_nivel_quando_o_segmento_limpa_pra_vazio()
    {
        var chave = MediaNomeParser.ChaveSerie(
            "Hora da Aventura - TORRENTMEGAFILMES/7ª Temporada - Completa - TORRENTMEGAFILMES/S07E05 - Futebol - TORRENTMEGAFILMES.TV.mkv");
        Assert.Equal("Hora da Aventura", chave);
    }

    // Regressão real: "COMANDO.TO - Contos do Loop 1ª Temporada Completa [1080p] [DUAL]" —
    // o domínio do uploader vem ANTES do nome de verdade, separado por " - ". O corte normal
    // (que assume ruído DEPOIS de um prefixo limpo) parava no primeiro " - " e devolvia
    // "COMANDO TO" como se fosse o nome da série, perdendo "Contos do Loop" de vez.
    [Fact]
    public void ChaveSerie_ignora_dominio_do_uploader_no_comeco_do_nome()
    {
        var chave = MediaNomeParser.ChaveSerie(
            "COMANDO.TO - Contos do Loop 1ª Temporada Completa [1080p] [DUAL]/Contos.do.Loop.S01E06.1080p.WEB-DL.x264.DUAL.COMANDO.TO.mp4");
        Assert.Equal("Contos do Loop", chave);
    }

    // Regressão real: "Hora da Aventura" (grafia de release) e "Hora de Aventura" (título
    // oficial BR, já com 85 episódios no catálogo) são a mesma série — sem apelido, a
    // temporada 7 (fonte diferente) virava uma segunda série à parte.
    [Fact]
    public void ChaveAgrupamento_resolve_apelido_hora_da_aventura()
    {
        Assert.Equal(
            MediaNomeParser.ChaveAgrupamento("Hora de Aventura"),
            MediaNomeParser.ChaveAgrupamento("Hora da Aventura"));
    }

    // ─── ChaveAgrupamento: tolerância a acento/maiúscula na chave de agrupar ────────────

    [Theory]
    [InlineData("Diários de Um Vampiro", "Diarios de um vampiro")]
    [InlineData("Diários de Um Vampiro", "Diários de um Vampiro")]
    [InlineData("Diários de Um Vampiro", "DIÁRIOS DE UM VAMPIRO")]
    [InlineData("Breaking  Bad", "Breaking Bad")]  // espaço duplo também não deveria separar
    public void ChaveAgrupamento_ignora_acento_maiuscula_espaco(string a, string b)
    {
        Assert.Equal(MediaNomeParser.ChaveAgrupamento(a), MediaNomeParser.ChaveAgrupamento(b));
    }

    [Fact]
    public void ChaveAgrupamento_null_ou_vazio_passa_direto()
    {
        Assert.Null(MediaNomeParser.ChaveAgrupamento(null));
        Assert.Equal("", MediaNomeParser.ChaveAgrupamento(""));
    }

    // ─── ChaveBaseFranquia/NomeBaseFranquia: franquia de filme por título ───────────────
    // Caso real: Homem-Aranha 1 numa pasta própria, Homem-Aranha 2 e 3 numa pasta
    // "TRILOGIA..." separada — sem pasta-mãe nenhuma ligando as três (diferente do caso que
    // RePastaColecao resolve). Só dá pra juntar comparando o título já limpo.

    [Theory]
    [InlineData("Homem Aranha 2", "Homem Aranha")]
    [InlineData("Homem Aranha 3", "Homem Aranha")]
    [InlineData("Toy Story 2", "Toy Story")]
    [InlineData("Toy Story 3", "Toy Story")]
    [InlineData("Rocky V", "Rocky")]
    [InlineData("Missão Impossível Parte 2", "Missão Impossível")]
    public void NomeBaseFranquia_corta_o_marcador_de_sequencia_do_fim(string titulo, string esperado)
    {
        Assert.Equal(esperado, MediaNomeParser.NomeBaseFranquia(titulo));
    }

    [Theory]
    [InlineData("Homem Aranha")]       // sem número no fim -- já é a base, nada pra cortar
    [InlineData("Toy Story")]
    [InlineData("Poder")]
    [InlineData("Poder Absoluto")]     // "Absoluto" não é marcador de sequência nenhum
    [InlineData("V for Vendetta")]     // "V" no MEIO do título, não sozinho no fim
    [InlineData("Se7en")]              // dígito colado na palavra, não separado por espaço
    public void NomeBaseFranquia_titulo_sem_sequencia_fica_igual(string titulo)
    {
        Assert.Equal(titulo, MediaNomeParser.NomeBaseFranquia(titulo));
    }

    [Fact]
    public void ChaveBaseFranquia_junta_filmes_da_mesma_franquia()
    {
        var chaves = new[] { "Homem Aranha", "Homem Aranha 2", "Homem Aranha 3" }
            .Select(MediaNomeParser.ChaveBaseFranquia).Distinct().ToList();
        Assert.Single(chaves);
    }

    // Regressão do próprio risco que motivou não implementar por correspondência de nome
    // parecido: "Poder" e "Poder Absoluto" NÃO podem cair na mesma chave só por compartilhar
    // a primeira palavra -- só corte de marcador de sequência conta, não prefixo comum.
    [Fact]
    public void ChaveBaseFranquia_nao_confunde_titulos_parecidos_sem_sequencia()
    {
        Assert.NotEqual(
            MediaNomeParser.ChaveBaseFranquia("Poder"),
            MediaNomeParser.ChaveBaseFranquia("Poder Absoluto"));
    }

    // Regressão real: as 5 pastas de "Diários de Um Vampiro" vieram de fontes diferentes com
    // grafia levemente diferente — sem ChaveAgrupamento normalizando, isso rachava a série em
    // até 3 grupos na tela (visto em produção) em vez de 1 com as 5 temporadas.
    [Fact]
    public void ChaveAgrupamento_junta_temporadas_de_fontes_com_grafia_diferente()
    {
        var chaves = new[]
        {
            MediaNomeParser.ChaveSerie("Diarios de Um Vampiro 1 Temporada (www.ThePirateFilmes.com)/1 - Pilot.mp4"),
            MediaNomeParser.ChaveSerie("Diários de Um Vampiro 2ª Temporada [2010 DUAL AUDIO] 720p/2 - x.mp4"),
            MediaNomeParser.ChaveSerie("Diários de Um Vampiro 3ª Temporada (2011) DUAL AUDIO 720p_Douglasvip/3 - x.mp4"),
            MediaNomeParser.ChaveSerie("Diários de Um Vampiro 4ª Temporada (2012) BDRip 720p Dual Áudio - Douglasvip/4 - x.mp4"),
            MediaNomeParser.ChaveSerie("Diários de um Vampiro - 5ª Temporada (2014) 720p Dual Áudio - Douglasvip/5 - x.mp4"),
        }.Select(MediaNomeParser.ChaveAgrupamento).Distinct().ToList();

        Assert.Single(chaves);
    }

    // Regressão real: "T.V.D.S07.WWW.TORRENTDOSFILMES.COM/The.Vampire.Diaries.S07E01....mkv" —
    // ReCorteSerie só cortava em "SxxExx" completo (episódio embutido) ou "Temporada N"/"Season
    // N" por extenso; "S07" sozinho (comum em pasta de release) não disparava nada, e a pasta
    // inteira (com "WWW.TORRENTDOSFILMES.COM" e tudo) virava o nome da série.
    [Fact]
    public void ChaveSerie_corta_temporada_solta_tipo_S07_sem_episodio_embutido()
    {
        var chave = MediaNomeParser.ChaveSerie(
            "T.V.D.S07.WWW.TORRENTDOSFILMES.COM/The.Vampire.Diaries.S07E01.WEB-DL.720p.Dual.TORRENTDOSFILMES.COM.mkv");
        Assert.Equal("T V D", chave);
    }

    // Regressão real: a mesma série ("Diários de Um Vampiro") tinha temporadas em 3 nomes
    // fundamentalmente diferentes (não só acento/maiúscula) — "Diários de Um Vampiro" (PT),
    // "The Vampire Diaries" (EN) e "T V D" (abreviação de release) — cada um virando uma
    // "série" separada na tela. ChaveAgrupamento sozinha (acento/case) não resolve isso.
    [Theory]
    [InlineData("The Vampire Diaries")]
    [InlineData("the vampire diaries")]
    [InlineData("T V D")]
    [InlineData("t v d")]
    public void ChaveAgrupamento_resolve_apelidos_conhecidos_pra_chave_canonica(string apelido)
    {
        Assert.Equal(
            MediaNomeParser.ChaveAgrupamento("Diários de Um Vampiro"),
            MediaNomeParser.ChaveAgrupamento(apelido));
    }

    // ─── NomePastaExibicao: nome limpo de pasta filme+extras ────────────

    [Fact]
    public void NomePastaExibicao_tira_o_prefixo_de_diretorio()
    {
        Assert.Equal("Alem da Imaginação", MediaNomeParser.NomePastaExibicao("extra/Alem da Imaginação"));
    }

    // Regressão real: pasta com pontos, tag de qualidade e assinatura de release solta no fim
    // aparecia crua na tela ("extra/TRILOGIA.Homem-Aranha.1080p-RICKSZ").
    [Fact]
    public void NomePastaExibicao_tira_pontos_qualidade_e_assinatura_de_release()
    {
        Assert.Equal("TRILOGIA Homem-Aranha",
            MediaNomeParser.NomePastaExibicao("extra/TRILOGIA.Homem-Aranha.1080p-RICKSZ"));
    }

    // Regressão real: propaganda de site dentro de colchete ("[ACESSE COMANDOTORRENTS.COM]")
    // aparecia junto do nome do filme; as outras tags entre colchetes ([1080p], [WEB-DL],
    // [NACIONAL]) tem que sumir também sem deixar colchete vazio pra trás.
    [Fact]
    public void NomePastaExibicao_tira_propaganda_de_site_e_tags_entre_colchetes()
    {
        var nome = MediaNomeParser.NomePastaExibicao(
            "extra/[ACESSE COMANDOTORRENTS.COM] Democracia em Vertigem 2019 [1080p] [WEB-DL] [NACIONAL]");
        Assert.Equal("Democracia em Vertigem 2019", nome);
    }

    [Fact]
    public void NomePastaExibicao_nao_remove_colchete_legitimo_sem_palavra_gatilho()
    {
        // "[Extended]" não tem gatilho de propaganda (acesse/baixe/download/www/domínio) --
        // não pode ser removido só por estar entre colchetes.
        var nome = MediaNomeParser.NomePastaExibicao("Filme [Extended]");
        Assert.Contains("Extended", nome);
    }

    // Regressão: ReAssinaturaRelease ("-RICKSZ") sozinha cortava qualquer final maiúsculo de
    // 3+ letras/dígitos, inclusive títulos de filme de verdade que terminam assim -- "X-MEN"
    // virava "X", "K-PAX" virava "K". Só corta quando a pasta já tem outro indício de nome de
    // release (ponto/underscore, ou tag de qualidade/codec como "1080p"/"WEB-DL").
    [Theory]
    [InlineData("X-MEN")]
    [InlineData("K-PAX")]
    [InlineData("Mad-MAX")]
    public void NomePastaExibicao_nao_corta_titulo_de_filme_so_por_terminar_em_maiuscula(string titulo)
    {
        Assert.Equal(titulo, MediaNomeParser.NomePastaExibicao(titulo));
    }

    [Fact]
    public void NomePastaExibicao_pasta_ja_limpa_fica_igual()
    {
        Assert.Equal("Nome Normal", MediaNomeParser.NomePastaExibicao("Nome Normal"));
    }

    // ─── Classificar: o pacote que vai pro FilmeResponse ────────────────

    [Fact]
    public void Classificar_filme()
    {
        var c = MediaNomeParser.Classificar("Filmes/Interestelar 2014 1080p.mkv", "Interestelar");
        Assert.False(c.EhEpisodio);
        Assert.False(c.EhExtra);
        Assert.Null(c.Serie);
        Assert.Equal("Interestelar", c.Rotulo);   // filme: rótulo = título passado
        Assert.Equal("Filmes", c.Pasta);
    }

    // Regressão real: "Quadrilogia A Era do Gelo .../A Era do Gelo 2 2006 .../filme.mkv" —
    // cada filme sozinho na sua própria subpasta (nomeada com o título de CADA filme, não um
    // "Volume N" genérico) nunca batia "2+ arquivos" com a pasta imediata, e cada um ia pra
    // filme solto em vez de agrupar como coleção. Pasta() agora sobe até a pasta-mãe quando
    // acha "trilogia/quadrilogia/coleção/..." num segmento ANTES do último.
    [Theory]
    [InlineData("Quadrilogia A Era do Gelo 2002 - 2012 [1080p] WWW.BLUDV.COM/A Era do Gelo 2 2006 [1080p] WWW.BLUDV.COM/A.Era.do.Gelo.2.2006.mkv")]
    [InlineData("Quadrilogia A Era do Gelo 2002 - 2012 [1080p] WWW.BLUDV.COM/A Era do Gelo 4 2012 [1080p] WWW.BLUDV.COM/A.Era.do.Gelo.4.2012.mkv")]
    public void ClassificarFilme_sobe_pra_pasta_mae_quando_e_uma_colecao(string path)
    {
        var c = MediaNomeParser.Classificar(path, "x");
        Assert.Equal("Quadrilogia A Era do Gelo 2002 - 2012 [1080p] WWW.BLUDV.COM", c.Pasta);
    }

    [Fact]
    public void ClassificarFilme_pasta_sem_palavra_de_colecao_fica_como_esta()
    {
        var c = MediaNomeParser.Classificar("Toy Story 2 (1999)/Toy Story 2 (1999) 1080p.mp4", "x");
        Assert.Equal("Toy Story 2 (1999)", c.Pasta);
    }

    [Fact]
    public void Classificar_episodio_com_temporada_na_pasta()
    {
        var c = MediaNomeParser.Classificar("Breaking Bad 3 Temporada - The Pirate Filmes/8 - I See You.mp4", "8 - I See You");
        Assert.True(c.EhEpisodio);
        Assert.Equal("Breaking Bad", c.Serie);
        Assert.Equal(3, c.Temporada);
        Assert.Equal(8, c.Episodio);
        Assert.Equal("S03E08", c.Rotulo);
    }

    [Theory]
    [InlineData("Filme (2020)/trailer.mkv")]
    [InlineData("Filme (2020)/Filme.2020.sample.mkv")]
    [InlineData("Serie/S01/promo 1xbet.mkv")]
    public void Classificar_marca_extra(string path)
    {
        Assert.True(MediaNomeParser.Classificar(path, "x").EhExtra);
    }

    // Regressão real: "Democracia em Vertigem" tinha 2 arquivos "reais" na pasta (o filme +
    // um vídeo de propaganda do site com extensão de vídeo de verdade), então nunca colapsava
    // pra filme solto — o uploader nomeia a propaganda só com o domínio, sem nenhuma palavra
    // de trailer/sample/promo que o ReExtra já pegaria.
    [Theory]
    [InlineData("Filme (2019)/COMANDOTORRENTS.COM.mp4")]
    [InlineData("Filme (2019)/TorrentDosFilmes.SE.mp4")]
    public void EhExtra_reconhece_arquivo_que_e_so_o_dominio_do_site(string path)
    {
        Assert.True(MediaNomeParser.EhExtra(path));
    }

    [Fact]
    public void EhExtra_nao_confunde_titulo_de_filme_com_dominio()
    {
        // "Ainda.Estou.Aqui.mkv" não é um domínio (não termina em .com/.net/...) -- garante
        // que ReNomeEhSoDominio não fica geral demais.
        Assert.False(MediaNomeParser.EhExtra("Filme (2024)/Ainda.Estou.Aqui.2024.1080p.mkv"));
    }

    [Fact]
    public void Classificar_episodio_sem_temporada_monta_rotulo_com_titulo()
    {
        var c = MediaNomeParser.Classificar("Anime/Episódio 12 - O Confronto.mkv", "Episódio 12 - O Confronto");
        Assert.True(c.EhEpisodio);
        Assert.Equal(0, c.Temporada);
        Assert.Equal(12, c.Episodio);
        Assert.Equal("Ep 12 · O Confronto", c.Rotulo);
    }

    // ─── TituloParaBusca: nome limpo pro TMDB ───────────────────────────

    [Theory]
    [InlineData("Filmes/Blade Runner 2049 2017 1080p BluRay x264.mkv", "Blade Runner 2049", 2017)]
    [InlineData("Filmes/Interestelar.2014.1080p.Dublado.mkv", "Interestelar", 2014)]
    [InlineData("Filmes/O Poderoso Chefao (1972).mkv", "O Poderoso Chefao", 1972)]
    [InlineData("Show/Show.S02E05.720p.WEB-DL.mkv", "Show", null)]
    [InlineData("Breaking Bad 2011 4ª Temporada Completa [WWW.BLUDV.COM]/Breaking.Bad.2011.S04E09.720p.BluRay.x264.DUAL.mkv", "Breaking Bad", 2011)]
    // episódio "N - Título": o número vira ruído (já está em Temporada/Episódio)
    [InlineData("Breaking Bad 3 Temporada - The Pirate Filmes/8 - I See You.mp4", "I See You", null)]
    [InlineData("Show/Season 2/07. O Retorno.mkv", "O Retorno", null)]
    // Regressão real: domínio do uploader grudado no COMEÇO do nome do arquivo (sem espaço
    // nenhum ao redor do hífen) — "COMANDO.LA-Ainda.Estou.Aqui.2024...mkv" virava "Comando
    // LA Ainda Estou Aqui" como se o site fizesse parte do título do filme.
    [InlineData("Ainda Estou Aqui (2024)/COMANDO.LA-Ainda.Estou.Aqui.2024.1080p.FULL.HD.WEB-DL.NACIONAL.5.1.mkv", "Ainda Estou Aqui", 2024)]
    public void TituloParaBusca(string path, string titulo, int? ano)
    {
        var r = MediaNomeParser.TituloParaBusca(path);
        Assert.Equal(titulo, r.Titulo);
        Assert.Equal(ano, r.Ano);
    }

    [Fact]
    public void TituloParaBusca_episodio_numerado_cai_pro_nome_da_serie()
    {
        // "8 - I See You" sozinho não serve de busca; usa a série da pasta.
        var r = MediaNomeParser.TituloParaBusca("Breaking Bad 3 Temporada - The Pirate Filmes/8.mp4");
        Assert.Equal("Breaking Bad", r.Titulo);
    }

    // Regressão: pasta cujo nome é só ruído de release ("Legendado", "1080p", "Dual"...) +
    // arquivo cujo nome reduz a ≤2 chars/número puro fazia ChaveSerie devolver o MESMO texto
    // de entrada (sem "/" pra extrair segmento de pasta) e a chamada recursiva reentrava com
    // argumento idêntico — StackOverflowException garantido, derrubando o processo inteiro
    // no meio de um /scan. Basta o teste terminar (sem estourar a pilha) pra travar o fix.
    [Theory]
    [InlineData("Legendado/1.mkv")]
    [InlineData("1080p/2.mkv")]
    [InlineData("Dual/9.mp4")]
    [InlineData("Dublado/03.mkv")]
    public void TituloParaBusca_pasta_so_ruido_nao_recursiona_infinitamente(string path)
    {
        var r = MediaNomeParser.TituloParaBusca(path);
        Assert.NotNull(r.Titulo);
    }
}
