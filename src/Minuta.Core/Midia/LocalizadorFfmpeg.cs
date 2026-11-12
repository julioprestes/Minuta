namespace Minuta.Core.Midia;

/// <summary>Caminhos dos executáveis do FFmpeg: ffmpeg extrai o áudio; ffprobe inspeciona o arquivo.</summary>
public sealed record FerramentasFfmpeg(string Ffmpeg, string Ffprobe);

public static class LocalizadorFfmpeg
{
    private static readonly string Extensao = OperatingSystem.IsWindows() ? ".exe" : "";

    public static FerramentasFfmpeg Localizar(string? caminhoConfigurado) =>
        Localizar(caminhoConfigurado, Environment.GetEnvironmentVariable("PATH"));

    /// <param name="caminhoConfigurado">FFMPEG_PATH: o ffmpeg.exe ou a pasta dele. Se vazio, procura no PATH.</param>
    /// <param name="variavelPath">Conteúdo da variável PATH (parâmetro para permitir teste).</param>
    public static FerramentasFfmpeg Localizar(string? caminhoConfigurado, string? variavelPath)
    {
        if (!string.IsNullOrWhiteSpace(caminhoConfigurado))
        {
            var pasta = Directory.Exists(caminhoConfigurado)
                ? caminhoConfigurado
                : Path.GetDirectoryName(caminhoConfigurado);

            var encontrado = pasta is null ? null : NaPasta(pasta);
            return encontrado ?? throw new MinutaException(
                $"FFmpeg não encontrado em \"{caminhoConfigurado}\" (FFMPEG_PATH). É preciso ter ffmpeg{Extensao} e ffprobe{Extensao} na mesma pasta.",
                "Corrija FFMPEG_PATH no .env ou apague a linha para procurar no PATH.");
        }

        foreach (var pasta in (variavelPath ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var encontrado = NaPasta(pasta.Trim().Trim('"'));
            if (encontrado is not null)
                return encontrado;
        }

        throw new MinutaException(
            "FFmpeg não encontrado no PATH. Ele é usado para extrair o áudio do vídeo.",
            "Instale com \"winget install Gyan.FFmpeg\" e abra um terminal novo, ou informe a pasta em FFMPEG_PATH no .env.");
    }

    private static FerramentasFfmpeg? NaPasta(string pasta)
    {
        var ffmpeg = Path.Combine(pasta, "ffmpeg" + Extensao);
        var ffprobe = Path.Combine(pasta, "ffprobe" + Extensao);
        return File.Exists(ffmpeg) && File.Exists(ffprobe) ? new FerramentasFfmpeg(ffmpeg, ffprobe) : null;
    }
}
