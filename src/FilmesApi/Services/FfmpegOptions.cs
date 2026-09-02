namespace FilmesApi.Services;

/// <summary>Caminho dos binários do ffmpeg/ffprobe. No Docker apontam pro jellyfin-ffmpeg
/// (<c>FfmpegPath</c>/<c>FfprobePath</c> via env); fora dele, o PATH.</summary>
public record FfmpegOptions(string Ffmpeg, string Ffprobe)
{
    public static FfmpegOptions From(IConfiguration config) => new(
        config.GetValue<string>("FfmpegPath") ?? "ffmpeg",
        config.GetValue<string>("FfprobePath") ?? "ffprobe");
}
