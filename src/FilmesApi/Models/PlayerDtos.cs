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
public record AudioTrackRequest(int Idx);
public record ProximoOfertaRequest(int FilmeId, string Rotulo);
