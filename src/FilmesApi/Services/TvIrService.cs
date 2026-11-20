using System.Net;
using System.Text;
using System.Text.Json;

namespace FilmesApi.Services;

/// <summary>Resultado de uma tecla IR: <see cref="Enviado"/> só diz que o painel aceitou o comando — o IR não devolve estado, então a TV pode não ter reagido.</summary>
public record TvIrResultado(bool Enviado, int Status, string Mensagem);

/// <summary>Teclas do controle IR da TV (hub Tuya local) via gateway do PainelServer (<c>POST {PainelIrUrl}/api/ir</c>). O navegador não chama o painel direto
/// (ele checa <c>Origin</c>); este serviço faz o POST sem Origin, com <c>X-Painel: 1</c>. URL do painel e id do hub vêm da configuração
/// (<c>PainelIrUrl</c>, <c>PainelIrId</c>); só as teclas da lista fechada passam, e só o nome da tecla vem do navegador.</summary>
public class TvIrService(IHttpClientFactory http, IConfiguration config, ILogger<TvIrService> logger)
{
    public static readonly IReadOnlyList<string> Teclas =
        ["energia", "fonte", "menu", "voltar", "ok", "vol+", "vol-", "mudo", "canal+", "canal-", "cima", "baixo", "esquerda", "direita", "smarthub", "smarthub2"];

    public TimeSpan Limite { get; init; } = TimeSpan.FromSeconds(8);

    public bool Configurado => !string.IsNullOrWhiteSpace(config["PainelIrUrl"]) && !string.IsNullOrWhiteSpace(config["PainelIrId"]);

    public static bool TeclaValida(string? tecla) => tecla is not null && Teclas.Contains(tecla);

    public async Task<TvIrResultado> EnviarAsync(string? tecla, CancellationToken ct = default)
    {
        if (!TeclaValida(tecla)) return new(false, StatusCodes.Status400BadRequest, "Tecla desconhecida.");
        if (!Configurado) return new(false, StatusCodes.Status503ServiceUnavailable, "O controle IR da TV não está configurado neste servidor.");

        var url = config["PainelIrUrl"]!.TrimEnd('/') + "/api/ir";
        // corpo montado aqui: só a tecla (já validada) e o id da configuração; nada do navegador além do nome da tecla
        var corpo = JsonSerializer.Serialize(new { id = config["PainelIrId"], tecla });
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(corpo, Encoding.UTF8, "application/json") };
        req.Headers.Add("X-Painel", "1");

        using var limite = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limite.CancelAfter(Limite);
        try
        {
            using var resp = await http.CreateClient().SendAsync(req, limite.Token);
            if (resp.IsSuccessStatusCode) return new(true, StatusCodes.Status200OK, "Enviado.");
            logger.LogWarning("IR da TV: o painel respondeu {Status} para a tecla {Tecla}.", (int)resp.StatusCode, tecla);
            return resp.StatusCode == HttpStatusCode.TooManyRequests
                ? new(false, StatusCodes.Status429TooManyRequests, "Muitas teclas seguidas. Espere um instante.")
                : new(false, StatusCodes.Status502BadGateway, "O painel recusou a tecla. O controle IR pode estar fora da rede.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("IR da TV: o painel não respondeu em {S} s (tecla {Tecla}).", Limite.TotalSeconds, tecla);
            return new(false, StatusCodes.Status504GatewayTimeout, "O controle IR não respondeu. O hub pode estar fora da rede.");
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "IR da TV: não consegui falar com o painel (tecla {Tecla}).", tecla);
            return new(false, StatusCodes.Status502BadGateway, "Não consegui falar com o painel. O controle IR pode estar fora da rede.");
        }
    }
}
