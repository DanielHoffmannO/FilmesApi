namespace FilmesApi.Models;

/// <summary>O que o ffprobe achou de português nas faixas de áudio de um arquivo, sem contar a dublagem por IA
/// (<see cref="Services.EscolhaAudio.EhDubladoIa(Services.FaixaAudio?)"/>). Guardado em <see cref="AnaliseDeDublagem.AudioPtNaoIa"/>.
/// <see cref="Indefinido"/>: nenhuma faixa marcada como português, mas há faixa sem idioma informado — o probe não diz nada; vale só
/// a dica do nome do arquivo (<see cref="Services.DublagemEmPortugues.DicaNoNome"/>).</summary>
public enum AudioPortugues
{
    Nao = 0,
    Sim = 1,
    Indefinido = 2,
}
