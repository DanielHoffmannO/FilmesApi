using System.Globalization;

namespace FilmesApi.Services;

/// <summary>Uma linha de log por episódio marcado como assistido: por qual caminho (concluir/progresso/manual), onde estava, o que o player
/// tinha selecionado e quem chamou. Existe pra achar quem marcou o episódio errado: <c>selecionado</c> diferente de <c>Id</c> denuncia a TV
/// reportando o fim do vídeo antigo com o id do novo. Só IP da LAN e User-Agent resumido.</summary>
public static class RegistroDeMarcacao
{
    public static void Registrar(ILogger logger, HttpContext http, PlayerStateService player, int id, string via, double? posicao = null, double? duracao = null)
    {
        var ua = http.Request.Headers.UserAgent.ToString();
        logger.LogInformation("{Texto}", Montar(id, via, posicao, duracao, player.FilmeId, http.Connection.RemoteIpAddress?.ToString(), ua));
    }

    internal static string Montar(int id, string via, double? posicao, double? duracao, int? selecionado, string? ip, string? userAgent)
    {
        static string Num(double? v) => v is double d ? d.ToString("0.#", CultureInfo.InvariantCulture) + "s" : "?";
        var ua = string.IsNullOrWhiteSpace(userAgent) ? "-" : userAgent.Length > 60 ? userAgent[..60] + "…" : userAgent;
        return $"Episódio {id} marcado como assistido: via={via}, posição={Num(posicao)}, duração={Num(duracao)}, selecionado={(selecionado?.ToString() ?? "nenhum")}, cliente={ip ?? "?"} {ua}";
    }
}
