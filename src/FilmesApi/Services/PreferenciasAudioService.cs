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
            using var escopo = escopos.CreateScope();
            var db = escopo.ServiceProvider.GetRequiredService<AppDbContext>();
            if (canonico is null)
            {
                db.PreferenciasSerie.Where(p => p.ChaveSerie == chave).ExecuteDelete();
                _series!.Remove(chave);
            }
            else
            {
                var existente = db.PreferenciasSerie.Find(chave);
                if (existente is null) db.PreferenciasSerie.Add(new PreferenciaSerie { ChaveSerie = chave, IdiomaAudio = canonico });
                else existente.IdiomaAudio = canonico;
                db.SaveChanges();
                _series![chave] = canonico;
            }
        }
        return true;
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
        _series = db.PreferenciasSerie.AsNoTracking().ToDictionary(p => p.ChaveSerie, p => p.IdiomaAudio);
        _episodios = db.PreferenciasEpisodio.AsNoTracking().ToDictionary(p => p.FilmeId, p => p.AudioIndex);
    }
}
