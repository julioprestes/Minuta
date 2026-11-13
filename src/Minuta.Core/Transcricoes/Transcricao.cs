using System.Text;
using System.Text.Json;

namespace Minuta.Core.Transcricoes;

/// <summary>Um trecho de fala com o momento em que aparece no vídeo.</summary>
/// <param name="BaixaConfianca">O Whisper indicou pouca certeza (fala confusa, ruído, repetição).</param>
public sealed record SegmentoTranscricao(TimeSpan Inicio, TimeSpan Fim, string Texto, bool BaixaConfianca = false);

/// <summary>Identifica o vídeo transcrito, para saber se uma transcrição salva ainda vale.</summary>
public sealed record OrigemTranscricao(string Arquivo, long Bytes, DateTime ModificadoEmUtc)
{
    public static OrigemTranscricao De(FileInfo video) => new(video.Name, video.Length, video.LastWriteTimeUtc);
}

/// <param name="SegmentosDescartados">Trechos que o Whisper marcou como silêncio/ruído e foram retirados.</param>
public sealed record Transcricao(
    string Modelo,
    string Idioma,
    TimeSpan? Duracao,
    IReadOnlyList<SegmentoTranscricao> Segmentos,
    int SegmentosDescartados = 0)
{
    public OrigemTranscricao? Origem { get; init; }

    /// <summary>Prompt do glossário usado na transcrição (null = sem glossário). Mudou o glossário, transcreve de novo.</summary>
    public string? Glossario { get; init; }

    public bool Vazia => Segmentos.All(s => string.IsNullOrWhiteSpace(s.Texto));

    /// <summary>Uma linha por trecho: "[mm:ss] texto". Trechos de baixa confiança terminam com "(?)".</summary>
    public string ParaTextoComTempos()
    {
        var texto = new StringBuilder();
        foreach (var s in Segmentos)
            texto.Append('[').Append(FormatarTempo(s.Inicio)).Append("] ")
                 .Append(s.Texto)
                 .AppendLine(s.BaixaConfianca ? " (?)" : "");
        return texto.ToString();
    }

    public static string FormatarTempo(TimeSpan tempo) =>
        tempo.TotalHours >= 1 ? tempo.ToString(@"h\:mm\:ss") : tempo.ToString(@"mm\:ss");
}

/// <summary>Grava e lê a transcrição ao lado da saída: .json (para o programa) e .txt (para pessoas).</summary>
public static class ArquivoTranscricao
{
    public const string SufixoJson = ".transcricao.json";
    public const string SufixoTexto = ".transcricao.txt";

    private static readonly JsonSerializerOptions Opcoes = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // acentos legíveis
    };

    public static void Salvar(Transcricao transcricao, string caminhoJson, string caminhoTexto)
    {
        File.WriteAllText(caminhoJson, JsonSerializer.Serialize(transcricao, Opcoes), Encoding.UTF8);
        File.WriteAllText(caminhoTexto, transcricao.ParaTextoComTempos(), Encoding.UTF8);
    }

    /// <summary>
    /// Lê a transcrição salva, se ela existir, estiver íntegra, for do mesmo vídeo (nome, tamanho e data)
    /// e tiver usado o mesmo glossário. Caso contrário retorna null e o vídeo é transcrito de novo.
    /// </summary>
    public static Transcricao? Carregar(string caminhoJson, FileInfo video, string? promptGlossario = null)
    {
        if (!File.Exists(caminhoJson))
            return null;

        try
        {
            var salva = JsonSerializer.Deserialize<Transcricao>(File.ReadAllText(caminhoJson), Opcoes);
            return salva?.Origem == OrigemTranscricao.De(video)
                && salva.Glossario == promptGlossario
                && salva.Segmentos is not null ? salva : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
