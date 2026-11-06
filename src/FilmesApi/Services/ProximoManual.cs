namespace FilmesApi.Services;

/// <summary>O "próximo episódio" apertado no controle foi no FIM do episódio? Só então o servidor aprende (liga o próximo automático da
/// temporada); apertar no minuto 5 só toca o próximo, sem ensinar nada. Pura.</summary>
public static class ProximoManual
{
    /// <summary>Restam no máximo isto (padrão <c>ProximoRestanteSegundos</c> = 180 s) pra contar como "já acabou" quando os créditos não são conhecidos.</summary>
    public const double RestantePadraoSegundos = 180;

    /// <summary>Já acabou: a posição está nos créditos conhecidos, OU restam ≤ <paramref name="restanteMaxSegundos"/>, OU a TV já está oferecendo o próximo.</summary>
    public static bool JaAcabou(double pos, double duracao, double? inicioCreditos, bool ofertaAtiva, double restanteMaxSegundos) =>
        ofertaAtiva
        || (inicioCreditos is double c && pos >= c)
        || (duracao > 0 && duracao - pos <= restanteMaxSegundos);
}
