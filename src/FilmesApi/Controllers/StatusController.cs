using FilmesApi.Services;
using Microsoft.AspNetCore.Mvc;

namespace FilmesApi.Controllers;

/// <summary>Diagnóstico de runtime pro dono do servidor: temperatura da placa, fila de
/// transcode, uso do cache, estado da VPU. Consumido por <c>wwwroot/status.html</c>.</summary>
[ApiController]
[Route("api/status")]
public class StatusController : ControllerBase
{
    private readonly HlsTranscodeService _transcode;
    private readonly RkmppCapabilityService _rkmpp;
    private readonly ThermalService _thermal;

    public StatusController(HlsTranscodeService transcode, RkmppCapabilityService rkmpp, ThermalService thermal)
    {
        _transcode = transcode;
        _rkmpp = rkmpp;
        _thermal = thermal;
    }

    [HttpGet]
    public IActionResult Obter() => Ok(new
    {
        agora = DateTime.UtcNow,
        transcode = _transcode.ObterSnapshot(),
        rkmpp = _rkmpp.Snapshot(),
        termico = new
        {
            habilitado = _thermal.Habilitado,
            temperaturaC = _thermal.TemperaturaC(),
            throttlingAgora = _thermal.EstaThrottling,
        },
    });
}
