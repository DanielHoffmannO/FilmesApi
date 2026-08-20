using FilmesApi.Services;
using Microsoft.AspNetCore.Mvc;

namespace FilmesApi.Controllers;

public record VolumeRequest(double Valor);
public record SeekRequest(double Delta);

/// <summary>Controle remoto do player da TV: o celular manda comandos, a TV consulta o estado.</summary>
[ApiController]
[Route("api/player")]
public class PlayerController : ControllerBase
{
    private readonly PlayerStateService _state;

    public PlayerController(PlayerStateService state) => _state = state;

    [HttpGet("state")]
    public IActionResult ObterEstado() => Ok(_state.Snapshot());

    [HttpPost("selecionar/{filmeId:int}")]
    public IActionResult Selecionar(int filmeId)
    {
        _state.Selecionar(filmeId);
        return Ok(_state.Snapshot());
    }

    [HttpPost("play-pause")]
    public IActionResult TogglePlayPause()
    {
        _state.TogglePlayPause();
        return Ok(_state.Snapshot());
    }

    [HttpPost("volume")]
    public IActionResult SetVolume([FromBody] VolumeRequest req)
    {
        _state.SetVolume(req.Valor);
        return Ok(_state.Snapshot());
    }

    [HttpPost("seek")]
    public IActionResult Seek([FromBody] SeekRequest req)
    {
        _state.Seek(req.Delta);
        return Ok(_state.Snapshot());
    }

    [HttpPost("parar")]
    public IActionResult Parar()
    {
        _state.Parar();
        return Ok(_state.Snapshot());
    }
}
