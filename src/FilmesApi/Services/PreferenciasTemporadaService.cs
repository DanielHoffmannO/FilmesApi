using FilmesApi.Data;
using FilmesApi.Models;
using Microsoft.EntityFrameworkCore;

namespace FilmesApi.Services;

/// <summary>O que está ligado em cada temporada (pular a abertura sozinho, ir ao próximo sozinho). Singleton com tudo em memória e
/// gravado no banco a cada mudança — mesmo desenho de <see cref="PreferenciasAudioService"/>. Tudo desligado até alguém ligar.</summary>
public class PreferenciasTemporadaService(IServiceScopeFactory escopos)
{
    public record Ligados(bool PularAbertura, bool ProximoAutomatico);

    private readonly object _trava = new();
    private Dictionary<(string Serie, int Temporada), Ligados>? _todas;

    public Ligados Da(string chaveSerie, int temporada)
    {
        lock (_trava)
        {
            Carregar();
            return _todas!.GetValueOrDefault((chaveSerie, temporada)) ?? new Ligados(false, false);
        }
    }

    /// <summary>Tudo de uma vez, no formato que o controle desenha: <c>{ serie: { "1": { pularAbertura, proximoAutomatico } } }</c>.</summary>
    public Dictionary<string, Dictionary<string, object>> Todas()
    {
        lock (_trava)
        {
            Carregar();
            return _todas!.GroupBy(kv => kv.Key.Serie).ToDictionary(
                g => g.Key,
                g => g.ToDictionary(kv => kv.Key.Temporada.ToString(),
                    kv => (object)new { pularAbertura = kv.Value.PularAbertura, proximoAutomatico = kv.Value.ProximoAutomatico }));
        }
    }

    /// <summary>Liga/desliga o que vier não nulo; o resto fica como está. Devolve o estado final.</summary>
    public Ligados Definir(string chaveSerie, int temporada, bool? pularAbertura, bool? proximoAutomatico)
    {
        lock (_trava)
        {
            Carregar();
            var todas = _todas!;
            var atual = todas.GetValueOrDefault((chaveSerie, temporada)) ?? new Ligados(false, false);
            var novo = new Ligados(pularAbertura ?? atual.PularAbertura, proximoAutomatico ?? atual.ProximoAutomatico);
            if (novo == atual && todas.ContainsKey((chaveSerie, temporada))) return novo;

            using var escopo = escopos.CreateScope();
            var db = escopo.ServiceProvider.GetRequiredService<AppDbContext>();
            var linha = db.PreferenciasTemporada.Find(chaveSerie, temporada);
            if (linha is null) db.PreferenciasTemporada.Add(linha = new PreferenciaTemporada { ChaveSerie = chaveSerie, Temporada = temporada });
            linha.PularAbertura = novo.PularAbertura;
            linha.ProximoAutomatico = novo.ProximoAutomatico;
            db.SaveChanges();
            todas[(chaveSerie, temporada)] = novo;
            return novo;
        }
    }

    private void Carregar()
    {
        if (_todas is not null) return;
        using var escopo = escopos.CreateScope();
        var db = escopo.ServiceProvider.GetRequiredService<AppDbContext>();
        _todas = db.PreferenciasTemporada.AsNoTracking().ToList()
            .ToDictionary(p => (p.ChaveSerie, p.Temporada), p => new Ligados(p.PularAbertura, p.ProximoAutomatico));
    }
}
