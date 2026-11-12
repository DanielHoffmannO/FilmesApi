using FilmesApi.Services;

namespace FilmesApi.Tests;

/// <summary>Webhook do Sonarr/Radarr: validação da chave (desligado sem chave, tempo constante) e leitura do payload
/// (<see cref="WebhookArr.Interpretar"/>) — o que dispara scan, o que é teste, o que se ignora.</summary>
public class WebhookArrTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("x", true)]
    [InlineData("chave-longa-123", true)]
    public void Sem_chave_configurada_o_webhook_esta_desligado(string? configurada, bool ligado) =>
        Assert.Equal(ligado, WebhookArr.Habilitado(configurada));

    [Theory]
    [InlineData("segredo", "segredo", true)]
    [InlineData("segredo", "Segredo", false)]        // diferencia maiúscula
    [InlineData("segredo", "segredo ", false)]       // espaço no fim não vale
    [InlineData("segredo", "segred", false)]
    [InlineData("segredo", "segredo-mais-longo", false)]
    [InlineData("segredo", "", false)]
    [InlineData("segredo", null, false)]             // sem ?chave=
    [InlineData("", "", false)]                      // vazio não é "chave vazia confere": é desligado
    [InlineData("", "qualquer", false)]
    [InlineData(null, null, false)]
    [InlineData(null, "qualquer", false)]
    [InlineData("   ", "   ", false)]
    public void Chave_confere_so_quando_igual_e_configurada(string? configurada, string? enviada, bool confere) =>
        Assert.Equal(confere, WebhookArr.ChaveConfere(configurada, enviada));

    [Fact]
    public void Chave_com_acento_e_comparada_pelos_bytes()
    {
        Assert.True(WebhookArr.ChaveConfere("sênha-çerta", "sênha-çerta"));
        Assert.False(WebhookArr.ChaveConfere("sênha-çerta", "senha-certa"));
    }

    [Theory]
    [InlineData("""{"eventType":"Test","series":{"id":1,"title":"Série de teste"},"episodes":[{"id":123}]}""", AcaoDoWebhook.Teste, "Test", "Sonarr")]
    [InlineData("""{"eventType":"Test","movie":{"id":1,"title":"Filme de teste"},"remoteMovie":{}}""", AcaoDoWebhook.Teste, "Test", "Radarr")]
    [InlineData("""{"eventType":"Download","series":{"id":7},"episodes":[{"id":1}],"episodeFile":{"id":9},"isUpgrade":false}""", AcaoDoWebhook.Escanear, "Download", "Sonarr")]
    [InlineData("""{"eventType":"Download","movie":{"id":3},"movieFile":{"id":4},"isUpgrade":true}""", AcaoDoWebhook.Escanear, "Download", "Radarr")]
    [InlineData("""{"eventType":"ImportComplete","series":{"id":7}}""", AcaoDoWebhook.Escanear, "ImportComplete", "Sonarr")]
    [InlineData("""{"eventType":"Rename","series":{"id":7},"renamedEpisodeFiles":[]}""", AcaoDoWebhook.Escanear, "Rename", "Sonarr")]
    [InlineData("""{"eventType":"Rename","movie":{"id":7}}""", AcaoDoWebhook.Escanear, "Rename", "Radarr")]
    [InlineData("""{"eventType":"EpisodeFileDelete","series":{"id":7}}""", AcaoDoWebhook.Escanear, "EpisodeFileDelete", "Sonarr")]
    [InlineData("""{"eventType":"MovieFileDelete","movie":{"id":7}}""", AcaoDoWebhook.Escanear, "MovieFileDelete", "Radarr")]
    [InlineData("""{"eventType":"SeriesDelete","series":{"id":7},"deletedFiles":true}""", AcaoDoWebhook.Escanear, "SeriesDelete", "Sonarr")]
    [InlineData("""{"eventType":"MovieDelete","movie":{"id":7},"deletedFiles":true}""", AcaoDoWebhook.Escanear, "MovieDelete", "Radarr")]
    [InlineData("""{"eventType":"Grab","series":{"id":7}}""", AcaoDoWebhook.Ignorar, "Grab", "Sonarr")]               // começou a baixar: nada no disco ainda
    [InlineData("""{"eventType":"Health","level":"warning","message":"x"}""", AcaoDoWebhook.Ignorar, "Health", "arr")]
    [InlineData("""{"eventType":"HealthRestored"}""", AcaoDoWebhook.Ignorar, "HealthRestored", "arr")]
    [InlineData("""{"eventType":"ApplicationUpdate","message":"x"}""", AcaoDoWebhook.Ignorar, "ApplicationUpdate", "arr")]
    [InlineData("""{"eventType":"ManualInteractionRequired","series":{"id":7}}""", AcaoDoWebhook.Ignorar, "ManualInteractionRequired", "Sonarr")]
    [InlineData("""{"eventType":"SeriesAdd","series":{"id":7}}""", AcaoDoWebhook.Ignorar, "SeriesAdd", "Sonarr")]
    [InlineData("""{"eventType":"MovieAdded","movie":{"id":7}}""", AcaoDoWebhook.Ignorar, "MovieAdded", "Radarr")]
    [InlineData("""{"eventType":"AlgoNovoQueNaoConheco"}""", AcaoDoWebhook.Ignorar, "desconhecido", "arr")]
    public void Evento_vira_a_acao_certa(string corpo, AcaoDoWebhook acao, string evento, string origem)
    {
        var r = WebhookArr.Interpretar(corpo);

        Assert.Equal((acao, evento, origem), (r.Acao, r.Evento, r.Origem));
    }

    [Theory]
    [InlineData("""{"eventType":"download"}""")]
    [InlineData("""{"eventType":"DOWNLOAD"}""")]
    [InlineData("""{"eventType":"  Download "}""")]
    public void Nome_do_evento_nao_diferencia_maiuscula_nem_espaco(string corpo)
    {
        var r = WebhookArr.Interpretar(corpo);

        Assert.Equal((AcaoDoWebhook.Escanear, "Download"), (r.Acao, r.Evento));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("isso não é json")]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("42")]
    [InlineData("\"Download\"")]
    [InlineData("{}")]                                  // sem eventType
    [InlineData("""{"eventType":null}""")]
    [InlineData("""{"eventType":5}""")]
    [InlineData("""{"EVENTTYPE_ERRADO":"Download"}""")]
    public void Corpo_que_nao_e_webhook_do_arr_e_invalido(string? corpo) =>
        Assert.Equal(AcaoDoWebhook.Invalido, WebhookArr.Interpretar(corpo).Acao);

    [Fact]
    public void Evento_desconhecido_nao_vai_pro_log_com_o_texto_de_quem_chamou()
    {
        // o nome do evento só sai normalizado: texto livre do corpo nunca é repetido (log injection)
        var r = WebhookArr.Interpretar("""{"eventType":"linha1\nlinha2 [FALSO] chave=abc"}""");

        Assert.Equal("desconhecido", r.Evento);
    }

    [Fact]
    public void Payload_grande_de_season_pack_e_lido_sem_problema()
    {
        var episodios = string.Join(",", Enumerable.Range(1, 200).Select(i =>
            "{\"id\":" + i + ",\"title\":\"Episódio " + i + "\",\"seasonNumber\":1,\"episodeNumber\":" + i + "}"));
        var corpo = "{\"eventType\":\"Download\",\"series\":{\"id\":1,\"title\":\"Série\"},\"episodes\":[" + episodios + "],\"episodeFile\":{\"id\":1}}";

        Assert.Equal(AcaoDoWebhook.Escanear, WebhookArr.Interpretar(corpo).Acao);
    }
}
