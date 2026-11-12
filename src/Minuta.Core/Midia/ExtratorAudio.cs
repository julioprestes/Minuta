using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Minuta.Core.Midia;

/// <summary>O que o ffprobe encontrou no arquivo.</summary>
public sealed record InfoMidia(TimeSpan? Duracao, bool TemAudio, bool TemVideo);

/// <summary>Áudio extraído, pronto para a transcrição.</summary>
public sealed record AudioExtraido(string Caminho, long Bytes, TimeSpan? Duracao);

/// <summary>
/// Trilha de áudio do pipeline: confere o arquivo com o ffprobe e extrai o áudio com o ffmpeg
/// em Opus, mono, 16 kHz, 32 kbps. O Whisper reduz tudo para 16 kHz mono de qualquer forma,
/// e assim 10 minutos de reunião ficam em torno de 2,5 MB.
/// </summary>
public sealed class ExtratorAudio(FerramentasFfmpeg ferramentas)
{
    /// <summary>Limite de upload do Groq no plano gratuito (25 MB). Com 32 kbps, dá cerca de 1h40 de áudio.</summary>
    public const long LimiteUploadBytes = 25L * 1000 * 1000;

    public const string SufixoArquivo = ".audio.ogg";

    public async Task<InfoMidia> InspecionarAsync(string arquivo, CancellationToken cancelamento = default)
    {
        var resultado = await ExecutorProcesso.ExecutarAsync(ferramentas.Ffprobe,
            ["-v", "error", "-show_entries", "format=duration:stream=codec_type", "-of", "json", arquivo],
            cancelamento);

        if (resultado.CodigoSaida != 0)
            throw new MinutaException(
                $"O arquivo não pôde ser lido como vídeo ou áudio: {Path.GetFileName(arquivo)}",
                "Confira se o arquivo não está corrompido ou incompleto. Detalhe do ffprobe: "
                    + ExecutorProcesso.Resumir(resultado.Erros));

        return InterpretarFfprobe(resultado.Saida);
    }

    public async Task<AudioExtraido> ExtrairAsync(FileInfo video, string pastaSaida, CancellationToken cancelamento = default)
    {
        var info = await InspecionarAsync(video.FullName, cancelamento);
        if (!info.TemAudio)
            throw new MinutaException(
                $"O arquivo não tem trilha de áudio: {video.Name}",
                "Vídeos sem áudio (só a tela) serão tratados na Fase 2.");

        Directory.CreateDirectory(pastaSaida);
        var destino = Path.Combine(pastaSaida, Path.GetFileNameWithoutExtension(video.Name) + SufixoArquivo);

        var resultado = await ExecutorProcesso.ExecutarAsync(ferramentas.Ffmpeg,
        [
            "-hide_banner", "-nostdin", "-v", "error", "-y",
            "-i", video.FullName,
            "-map", "0:a:0", "-vn",
            "-ac", "1", "-ar", "16000",
            "-c:a", "libopus", "-b:a", "32k", "-application", "voip",
            destino,
        ], cancelamento);

        var gerado = new FileInfo(destino);
        if (resultado.CodigoSaida != 0 || !gerado.Exists || gerado.Length == 0)
            throw new MinutaException(
                $"Falha ao extrair o áudio de {video.Name}.",
                "Detalhe do ffmpeg: " + ExecutorProcesso.Resumir(resultado.Erros));

        if (gerado.Length > LimiteUploadBytes)
            throw new MinutaException(
                $"O áudio extraído tem {gerado.Length / 1_000_000.0:N1} MB, acima do limite de 25 MB do Groq.",
                "Corte o vídeo em partes menores (até cerca de 1h30 cada). A divisão automática fica para depois.");

        return new AudioExtraido(gerado.FullName, gerado.Length, info.Duracao);
    }

    internal static InfoMidia InterpretarFfprobe(string json)
    {
        SaidaFfprobe? saida;
        try
        {
            saida = JsonSerializer.Deserialize<SaidaFfprobe>(json);
        }
        catch (JsonException ex)
        {
            throw new MinutaException("Resposta inesperada do ffprobe.", causa: ex);
        }

        var tipos = saida?.Streams?.Select(s => s.CodecType).ToList() ?? [];
        TimeSpan? duracao = double.TryParse(saida?.Format?.Duration, NumberStyles.Float, CultureInfo.InvariantCulture, out var segundos)
            ? TimeSpan.FromSeconds(segundos)
            : null;

        return new InfoMidia(duracao, tipos.Contains("audio"), tipos.Contains("video"));
    }

    private sealed record SaidaFfprobe(
        [property: JsonPropertyName("streams")] List<StreamFfprobe>? Streams,
        [property: JsonPropertyName("format")] FormatoFfprobe? Format);

    private sealed record StreamFfprobe([property: JsonPropertyName("codec_type")] string? CodecType);

    private sealed record FormatoFfprobe([property: JsonPropertyName("duration")] string? Duration);
}
