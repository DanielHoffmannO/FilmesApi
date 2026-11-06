using FilmesApi.Models;
using FilmesApi.Services;
using Microsoft.AspNetCore.Mvc;

namespace FilmesApi.Controllers;

/// <summary>Controle remoto do player da TV: o celular (controle.html) manda comandos, a
/// TV (tv.html) consulta o estado. A TV também chama Selecionar/Parar quando o usuário
/// mexe localmente (D-pad), pra o celular ficar sincronizado não importa a origem.</summary>
[ApiController]
[Route("api/player")]
public class PlayerController : ControllerBase
{
    private readonly PlayerStateService _state;
    private readonly AutomacaoDoPlayer _automacao;
    private readonly FilmeService _filmes;

    public PlayerController(PlayerStateService state, AutomacaoDoPlayer automacao, FilmeService filmes)
    {
        _state = state;
        _automacao = automacao;
        _filmes = filmes;
    }

    [HttpGet("state")]
    public IActionResult ObterEstado() => Ok(_state.Snapshot());

    [HttpPost("selecionar/{filmeId:int}")]
    public async Task<IActionResult> Selecionar(int filmeId)
    {
        _state.Selecionar(filmeId);
        await _automacao.AtualizarContextoAsync(filmeId);   // abertura conhecida + interruptores da temporada vão no snapshot
        return Ok(_state.Snapshot());
    }

    /// <summary>O botão "Pular abertura" do controle: o servidor lê a abertura do que está tocando, manda a TV pro fim dela e liga o pulo
    /// automático da temporada (o Daniel confirmou que ali é abertura). 404 se não há abertura conhecida. Use este e não o <c>seek-abs</c> genérico.</summary>
    [HttpPost("pular-abertura")]
    public async Task<IActionResult> PularAbertura()
    {
        if (_state.FilmeId is not int id || await _filmes.ObterAberturaAsync(id) is not var (_, fim))
            return NotFound(new { mensagem = "não há abertura conhecida no que está tocando" });
        _state.SeekAbsoluto(fim);
        _state.MarcarAberturaPulada();
        await _automacao.LigarPuloAutomaticoAsync(id);
        return Ok(_state.Snapshot());
    }

    /// <summary>O botão "Próximo episódio" do controle. Toca o próximo; se foi apertado no fim do episódio (créditos conhecidos, ou restam ≤
    /// <c>ProximoRestanteSegundos</c>, ou há oferta de próximo no ar), liga o próximo automático da temporada. 404 se não há próximo.</summary>
    [HttpPost("proximo-manual")]
    public async Task<IActionResult> ProximoManual()
    {
        var (proximo, aprendeu) = await _automacao.ProximoManualAsync();
        return proximo is int id ? Ok(new { proximoId = id, aprendeu }) : NotFound(new { mensagem = "não há próximo episódio" });
    }

    /// <summary>O Daniel cancelou o "Indo para o próximo" (próximo automático): a TV desiste e não repete neste episódio.</summary>
    [HttpPost("proximo-cancelar")]
    public IActionResult ProximoCancelar()
    {
        _state.CancelarProximoAutomatico();
        return Ok(_state.Snapshot());
    }

    /// <summary>A TV avisa que vai pro próximo sozinha (próximo automático): o celular mostra "Indo para o próximo · cancelar".</summary>
    [HttpPost("proximo-auto")]
    public IActionResult ProximoAuto()
    {
        _state.Avisar("proximo", "Indo para o próximo", "cancelar");
        return NoContent();
    }

    /// <summary>A TV avisa que pulou a abertura sozinha (temporada com pulo automático): o celular mostra "Abertura pulada · voltar" e não se repete neste episódio.</summary>
    [HttpPost("abertura-pulada")]
    public IActionResult AberturaPulada()
    {
        _state.MarcarAberturaPulada();
        _state.Avisar("abertura", "Abertura pulada", "voltar");
        return NoContent();
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

    /// <summary>A TV reporta a posição atual (a cada ~2s) pra o celular desenhar o progresso.</summary>
    [HttpPost("posicao")]
    public IActionResult ReportarPosicao([FromBody] PosicaoRequest req)
    {
        _state.ReportarPosicao(req.Pos, req.Dur);
        return NoContent();
    }

    /// <summary>Celular arrastou a barra — pula pra posição absoluta.</summary>
    [HttpPost("seek-abs")]
    public IActionResult SeekAbsoluto([FromBody] SeekAbsRequest req)
    {
        _state.SeekAbsoluto(req.Pos);
        return Ok(_state.Snapshot());
    }

    /// <summary>Celular escolheu a faixa de legenda da TV (-1 = desligar).</summary>
    [HttpPost("legenda")]
    public IActionResult SetLegenda([FromBody] LegendaRequest req)
    {
        _state.SetLegenda(req.Idx);
        return Ok(_state.Snapshot());
    }

    /// <summary>A TV avisa a legenda que ela mesma ligou/desligou (vermelho ou automática), pro celular
    /// mostrar o estado certo. Não gera comando de volta pra TV.</summary>
    [HttpPost("legenda-tv")]
    public IActionResult LegendaDaTv([FromBody] LegendaTvRequest req)
    {
        _state.RegistrarLegendaDaTv(req.Idx, req.Manual);
        return NoContent();
    }

    /// <summary>Celular escolheu a faixa de áudio ("dublagem") da TV.</summary>
    [HttpPost("audio")]
    public IActionResult SetAudio([FromBody] AudioTrackRequest req)
    {
        _state.SetAudio(req.Idx);
        return Ok(_state.Snapshot());
    }

    [HttpPost("parar")]
    public IActionResult Parar()
    {
        _state.Parar();
        return Ok(_state.Snapshot());
    }

    /// <summary>A TV avisa que começou a contagem de "próximo episódio" — o celular passa a
    /// exibir a oferta (ver <c>OferecerProximo</c> no estado).</summary>
    [HttpPost("proximo-oferece")]
    public IActionResult ProximoOferece([FromBody] ProximoOfertaRequest req)
    {
        _state.OferecerProximo(req.FilmeId, req.Rotulo);
        return Ok(_state.Snapshot());
    }

    /// <summary>Celular confirmou o próximo episódio oferecido pela TV.</summary>
    [HttpPost("proximo-aceita")]
    public IActionResult ProximoAceita()
    {
        _state.AceitarProximo();
        return Ok(_state.Snapshot());
    }
}
