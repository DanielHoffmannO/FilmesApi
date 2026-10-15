namespace FilmesApi.Services;

/// <summary>As decisões (puras) do pré-aquecimento do remux do que o dono mais abre: o "continuar assistindo" número 1.
/// O remux de um episódio de 533 MB leva ~25 s e o de um filme de 4,8 GB ~4 min com a TV mostrando "Preparando áudio";
/// com o arquivo pronto antes, a espera some. Só vale a pena se não atrapalhar nada — daí as regras abaixo.</summary>
internal static class PoliticaPreAquecimento
{
    /// <summary>O remux novo (~o tamanho do original: o vídeo é copiado, só o áudio muda) cabe no teto do cache SEM
    /// expulsar nada? Teto ilimitado (<c>long.MaxValue</c>) sempre cabe. Expulsar o que está em uso pra aquecer o que talvez
    /// seja aberto seria trocar um bem certo por um incerto.</summary>
    public static bool CabeNoCache(long bytesNoCache, long tamanhoDoOriginal, long tetoBytes) =>
        tetoBytes == long.MaxValue || bytesNoCache + tamanhoDoOriginal <= tetoBytes;

    /// <summary>Um remux em andamento SÓ pra aquecer cede o lugar quando alguém precisa do encoder (job na fila esperando
    /// slot) ou a placa passou da temperatura — o pedido de verdade nunca espera por um palpite.</summary>
    public static bool DeveCeder(int esperandoSlot, bool placaQuente) => esperandoSlot > 0 || placaQuente;
}
