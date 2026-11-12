namespace FilmesApi.Models;

// Corpos de POST /api/player/sono e /api/player/presenca-pergunta. Ver SonoController.

/// <summary>Armar o timer de sono. <c>Minutos</c> &gt; 0 = parar em N minutos; <c>AoFimDoEpisodio</c> = não avançar pro próximo, parar ao fim.
/// <c>Minutos</c> = 0 sem <c>AoFimDoEpisodio</c> cancela.</summary>
public record SonoRequest(int Minutos, bool AoFimDoEpisodio = false);

/// <summary>A TV avisa que está perguntando "Ainda está assistindo?"; <c>Rotulo</c> é o do episódio que começa se confirmarem.</summary>
public record PresencaPerguntaRequest(string? Rotulo);
