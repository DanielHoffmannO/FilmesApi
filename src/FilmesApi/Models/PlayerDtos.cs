namespace FilmesApi.Models;

// Comandos do controle remoto (celular → TV). Ver PlayerController / PlayerStateService.
public record VolumeRequest(double Valor);
public record SeekRequest(double Delta);
public record PosicaoRequest(double Pos, double Dur);
public record SeekAbsRequest(double Pos);
public record LegendaRequest(int Idx);
/// <summary>A TV informa a legenda que ELA ligou/desligou (botão vermelho ou automática). <c>Manual</c>:
/// foi escolha de quem assiste — vale até o fim do filme, não é religada sozinha.</summary>
public record LegendaTvRequest(int Idx, bool Manual);
/// <summary>Estilo da legenda na TV: <c>Tamanho</c> p|m|g, <c>Posicao</c> baixa|alta (null = não muda).</summary>
public record LegendaEstiloRequest(string? Tamanho, string? Posicao);
public record AudioTrackRequest(int Idx);
public record ProximoOfertaRequest(int FilmeId, string Rotulo);

/// <summary>Corpo de <c>POST /api/admin/nomes</c>: <c>Secao</c> = "apelidos" | "nomes".</summary>
public record NomeRequest(string? Secao, string? Chave, string? Valor);
