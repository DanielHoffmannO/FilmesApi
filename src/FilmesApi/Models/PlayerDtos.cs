namespace FilmesApi.Models;

// Comandos do controle remoto (celular → TV). Ver PlayerController / PlayerStateService.
public record VolumeRequest(double Valor);
public record SeekRequest(double Delta);
public record PosicaoRequest(double Pos, double Dur);
public record SeekAbsRequest(double Pos);
public record LegendaRequest(int Idx);
