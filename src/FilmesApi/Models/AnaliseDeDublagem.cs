namespace FilmesApi.Models;

/// <summary>O que a análise em segundo plano (<see cref="Services.DubladoIaService"/>) descobriu sobre as faixas de áudio de um arquivo:
/// uma linha por filme/episódio já olhado. É o que deixa as listagens dizerem "dublado por IA" sem rodar ffprobe a cada requisição.
/// <see cref="ArquivoBytes"/> é o tamanho do arquivo quando foi analisado — se mudar (o <c>scripts/dublar</c> acrescentou a faixa, ou a release
/// foi trocada), o arquivo volta à fila e a linha é refeita.</summary>
public class AnaliseDeDublagem
{
    public int FilmeId { get; set; }
    public Filme? Filme { get; set; }

    /// <summary>O arquivo tem uma faixa de áudio de "dublagem por IA" (<see cref="Services.EscolhaAudio.EhDubladoIa(Services.FaixaAudio?)"/>).</summary>
    public bool DubladoIa { get; set; }

    public long ArquivoBytes { get; set; }
    public DateTime AnalisadoEm { get; set; } = DateTime.UtcNow;
}
