using FilmesApi.Models;
using FilmesApi.Services;
using Microsoft.AspNetCore.Mvc;

namespace FilmesApi.Controllers;

/// <summary>Preferências por série (hoje: idioma de áudio padrão).</summary>
[ApiController]
[Route("api/series")]
public class SeriesController(PreferenciasAudioService prefs) : ControllerBase
{
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
