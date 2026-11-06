using FilmesApi.Models;
using FilmesApi.Services;
using Microsoft.AspNetCore.Mvc;

namespace FilmesApi.Controllers;

/// <summary>Preferências por série: idioma de áudio e legenda padrão, e por temporada o que liga sozinho (pular abertura, próximo episódio).</summary>
[ApiController]
[Route("api/series")]
public class SeriesController(PreferenciasAudioService prefs, PreferenciasTemporadaService temporadas, AutomacaoDoPlayer automacao, PlayerStateService player) : ControllerBase
{
    /// <summary>Os dois padrões de uma vez, pro controle desenhar os chips: <c>{ chave: { audio, legenda } }</c> (null = automático).</summary>
    [HttpGet("padroes")]
    public IActionResult Padroes()
    {
        var audios = prefs.TodasAsSeries();
        var legendas = prefs.TodasAsLegendas();
        return Ok(audios.Keys.Union(legendas.Keys).ToDictionary(
            k => k, k => new { audio = audios.GetValueOrDefault(k), legenda = legendas.GetValueOrDefault(k) }));
    }

    /// <summary>Legenda padrão da série: <c>idioma</c> = <c>"off"</c>, um idioma (<c>eng</c>, <c>por</c>…) ou null (automático).</summary>
    [HttpPut("{chave}/legenda")]
    public IActionResult DefinirLegenda(string chave, [FromBody] SerieAudioRequest req)
        => prefs.DefinirLegendaDaSerie(chave, req.Idioma)
            ? Ok(new SerieAudioResponse(prefs.LegendaDaSerie(chave)))
            : BadRequest(new { mensagem = "idioma não reconhecido (use off, eng, por, spa, fra, deu, ita ou jpn)" });

    /// <summary>O que está ligado em cada temporada de cada série: <c>{ chave: { "1": { pularAbertura, proximoAutomatico } } }</c>.</summary>
    [HttpGet("temporadas")]
    public IActionResult TodasTemporadas() => Ok(temporadas.Todas());

    /// <summary>Liga/desliga, numa temporada, o pulo automático da abertura e/ou o próximo episódio automático (o que não vier fica como está).</summary>
    [HttpPut("{chave}/temporadas/{n:int}")]
    public async Task<IActionResult> DefinirTemporada(string chave, int n, [FromBody] TemporadaRequest req)
    {
        var l = temporadas.Definir(chave, n, req.PularAbertura, req.ProximoAutomatico);
        if (player.FilmeId is int tocando) await automacao.AtualizarContextoAsync(tocando);   // a TV passa a obedecer já
        return Ok(new { pularAbertura = l.PularAbertura, proximoAutomatico = l.ProximoAutomatico });
    }

    /// <summary>Séries com idioma de áudio definido: <c>{ chave: idioma }</c>. Quem não aparece está no automático.</summary>
    [HttpGet("audio")]
    public IActionResult TodosAudios() => Ok(prefs.TodasAsSeries());

    /// <summary>Idioma de áudio padrão da série; <c>idioma</c> null = automático.</summary>
    [HttpGet("{chave}/audio")]
    public IActionResult ObterAudio(string chave) => Ok(new SerieAudioResponse(prefs.IdiomaDaSerie(chave)));

    /// <summary>Define o idioma padrão (<c>eng</c>, <c>por</c>…) ou, com <c>idioma</c> null, volta ao automático.
    /// Vale pro PRÓXIMO episódio que abrir: não derruba quem está assistindo nem apaga cache de ninguém.</summary>
    [HttpPut("{chave}/audio")]
    public IActionResult DefinirAudio(string chave, [FromBody] SerieAudioRequest req)
        => prefs.DefinirIdiomaDaSerie(chave, req.Idioma)
            ? Ok(new SerieAudioResponse(prefs.IdiomaDaSerie(chave)))
            : BadRequest(new { mensagem = "idioma não reconhecido (use eng, por, spa, fra, deu, ita ou jpn)" });
}
