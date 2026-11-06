using System.Collections.Concurrent;
using FilmesApi.Data;
using FilmesApi.Models;
using Microsoft.EntityFrameworkCore;

namespace FilmesApi.Services;

/// <summary>Que faixa de áudio abrir: idioma padrão por série e escolha manual por episódio, ambos gravados
/// no banco (sobrevivem a restart). Singleton com tudo em memória — o remux/HLS consulta a cada job e não
/// pode abrir escopo de EF pra isso; a tabela só é lida uma vez e a cada escrita memória e banco andam juntos.
/// A decisão em si (qual faixa ganha) mora em <see cref="EscolhaAudio.Escolher"/>.</summary>
public class PreferenciasAudioService(IServiceScopeFactory escopos)
{
    private readonly object _trava = new();
    private Dictionary<string, string>? _series;
    private Dictionary<string, string>? _legendas;
    private Dictionary<int, int>? _episodios;
    private readonly ConcurrentDictionary<int, string?> _chaves = new();

    /// <summary>Idioma padrão da série (<c>eng</c>…), ou null = automático.</summary>
    public string? IdiomaDaSerie(string chave)
    {
        lock (_trava)
        {
            Carregar();
            return _series!.GetValueOrDefault(chave);
        }
    }

    /// <summary>Todas as séries com idioma definido (chave → idioma) — as telas desenham o seletor com isso.</summary>
    public Dictionary<string, string> TodasAsSeries()
    {
        lock (_trava)
        {
            Carregar();
            return new Dictionary<string, string>(_series!);
        }
    }

    /// <summary>Define (ou, com null, remove) o idioma padrão da série. false se o idioma não é reconhecido.</summary>
    public bool DefinirIdiomaDaSerie(string chave, string? idioma)
    {
        var canonico = EscolhaAudio.NormalizarIdioma(idioma);
        if (idioma is not null && canonico is null) return false;

        lock (_trava)
        {
            Carregar();
            Gravar(chave, linha => linha.IdiomaAudio = canonico ?? "");
            if (canonico is null) _series!.Remove(chave); else _series![chave] = canonico;
        }
        return true;
    }

    /// <summary>Legenda padrão da série: <c>"off"</c>, um idioma, ou null (sem preferência).</summary>
    public string? LegendaDaSerie(string chave)
    {
        lock (_trava)
        {
            Carregar();
            return _legendas!.GetValueOrDefault(chave);
        }
    }

    /// <summary>Séries com legenda padrão definida (chave → "off" | idioma).</summary>
    public Dictionary<string, string> TodasAsLegendas()
    {
        lock (_trava)
        {
            Carregar();
            return new Dictionary<string, string>(_legendas!);
        }
    }

    /// <summary>Define a legenda padrão: <c>"off"</c>, um idioma reconhecido, ou null pra voltar ao automático.
    /// false se o idioma não é reconhecido.</summary>
    public bool DefinirLegendaDaSerie(string chave, string? idioma)
    {
        var valor = idioma is null ? null : idioma.Trim().ToLowerInvariant() == "off" ? "off" : EscolhaAudio.NormalizarIdioma(idioma);
        if (idioma is not null && valor is null) return false;

        lock (_trava)
        {
            Carregar();
            Gravar(chave, linha => linha.IdiomaLegenda = valor);
            if (valor is null) _legendas!.Remove(chave); else _legendas![chave] = valor;
        }
        return true;
    }

    /// <summary>Legenda padrão da série a que o episódio pertence (null = sem preferência).</summary>
    public string? LegendaDoEpisodio(int filmeId)
    {
        bool haLegendas;
        lock (_trava) { Carregar(); haLegendas = _legendas!.Count > 0; }
        return haLegendas ? LegendaDaSerie(ChaveDoFilme(filmeId) ?? "") : null;
    }

    /// <summary>Cria/atualiza a linha da série e apaga quando não sobra preferência nenhuma (nem áudio, nem legenda). Dentro da trava.</summary>
    private void Gravar(string chave, Action<PreferenciaSerie> alterar)
    {
        using var escopo = escopos.CreateScope();
        var db = escopo.ServiceProvider.GetRequiredService<AppDbContext>();
        var linha = db.PreferenciasSerie.Find(chave);
        var nova = linha is null;
        linha ??= new PreferenciaSerie { ChaveSerie = chave };
        alterar(linha);
        var vazia = linha.IdiomaAudio == "" && linha.IdiomaLegenda is null;
        if (vazia) { if (!nova) db.PreferenciasSerie.Remove(linha); }
        else { if (nova) db.PreferenciasSerie.Add(linha); }
        db.SaveChanges();
    }

    public void DefinirFaixaDoEpisodio(int filmeId, int audioIndex)
    {
        lock (_trava)
        {
            Carregar();
            using var escopo = escopos.CreateScope();
            var db = escopo.ServiceProvider.GetRequiredService<AppDbContext>();
            // Filme apagado entre a tela e o clique: a FK recusaria; só não guarda.
            if (!db.Filmes.Any(f => f.Id == filmeId)) { _episodios!.Remove(filmeId); return; }
            var existente = db.PreferenciasEpisodio.Find(filmeId);
            if (existente is null) db.PreferenciasEpisodio.Add(new PreferenciaEpisodio { FilmeId = filmeId, AudioIndex = audioIndex });
            else existente.AudioIndex = audioIndex;
            db.SaveChanges();
            _episodios![filmeId] = audioIndex;
        }
    }

    /// <summary>Entradas pro <see cref="EscolhaAudio.Escolher"/> deste episódio: a faixa manual (se houver) e o
    /// idioma da série a que ele pertence (se a série tem preferência).</summary>
    public (int? Manual, string? IdiomaDaSerie) Resolver(int filmeId)
    {
        int? manual;
        bool haSeries;
        lock (_trava)
        {
            Carregar();
            manual = _episodios!.TryGetValue(filmeId, out var m) ? m : null;
            haSeries = _series!.Count > 0;
        }
        return (manual, haSeries ? IdiomaDaSerie(ChaveDoFilme(filmeId) ?? "") : null);
    }

    private string? ChaveDoFilme(int filmeId) => _chaves.GetOrAdd(filmeId, id =>
    {
        using var escopo = escopos.CreateScope();
        var caminho = escopo.ServiceProvider.GetRequiredService<AppDbContext>().Filmes
            .AsNoTracking().Where(f => f.Id == id).Select(f => f.ArquivoPath).FirstOrDefault();
        return caminho is null ? null : MediaNomeParser.ChaveSerie(caminho);
    });

    private void Carregar()
    {
        if (_series is not null) return;
        using var escopo = escopos.CreateScope();
        var db = escopo.ServiceProvider.GetRequiredService<AppDbContext>();
        var linhas = db.PreferenciasSerie.AsNoTracking().ToList();
        _series = linhas.Where(p => p.IdiomaAudio != "").ToDictionary(p => p.ChaveSerie, p => p.IdiomaAudio);
        _legendas = linhas.Where(p => p.IdiomaLegenda is not null).ToDictionary(p => p.ChaveSerie, p => p.IdiomaLegenda!);
        _episodios = db.PreferenciasEpisodio.AsNoTracking().ToDictionary(p => p.FilmeId, p => p.AudioIndex);
    }
}
