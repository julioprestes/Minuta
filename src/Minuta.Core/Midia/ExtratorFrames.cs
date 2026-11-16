using System.Text;
using System.Text.Json;
using Minuta.Core.Transcricoes;

namespace Minuta.Core.Midia;

/// <summary>Um frame salvo: nome do arquivo (dentro da pasta de frames), momento no vídeo e motivo da escolha.</summary>
public sealed record FrameVideo(string Arquivo, TimeSpan Tempo, string Motivo);

public sealed record FramesExtraidos(string Pasta, IReadOnlyList<FrameVideo> Frames, bool Reaproveitados);

/// <summary>
/// Trilha visual, parte 1: escolhe os momentos em que a tela muda e salva esses frames em JPEG.
/// A comparação usa quadros pequenos em tons de cinza lidos direto da saída do ffmpeg (sem biblioteca de imagem);
/// depois, cada momento escolhido é extraído em boa resolução para o modelo de visão ler os textos da tela.
/// </summary>
public sealed class ExtratorFrames(FerramentasFfmpeg ferramentas)
{
    public const string SufixoPasta = ".frames";
    public const string ArquivoIndice = "indice.json";

    /// <summary>Muda quando a escolha dos frames muda; invalida as pastas de frames antigas.</summary>
    public const int VersaoSelecao = 2;

    /// <summary>Largura máxima do JPEG salvo. Telas 1080p ficam com o texto legível.</summary>
    public int LarguraMaximaJpeg { get; init; } = 1280;

    private static readonly JsonSerializerOptions Opcoes = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public async Task<FramesExtraidos> ExtrairAsync(
        FileInfo video, string pastaSaida, ParametrosSelecao? parametros = null, bool refazer = false,
        CancellationToken cancelamento = default)
    {
        parametros ??= new ParametrosSelecao();
        var pasta = Path.Combine(pastaSaida, Path.GetFileNameWithoutExtension(video.Name) + SufixoPasta);

        if (!refazer && CarregarIndice(pasta, video) is { } salvos)
            return new FramesExtraidos(pasta, salvos, Reaproveitados: true);

        var momentos = await EscolherMomentosAsync(video.FullName, parametros, cancelamento);

        Directory.CreateDirectory(pasta);
        LimparPasta(pasta);

        var frames = new List<FrameVideo>();
        for (var i = 0; i < momentos.Count; i++)
        {
            var nome = $"{i + 1:000}_{Transcricao.FormatarTempo(momentos[i].Tempo).Replace(':', '-')}.jpg";
            await ExtrairJpegAsync(video.FullName, momentos[i].TempoImagem, Path.Combine(pasta, nome), cancelamento);
            frames.Add(new FrameVideo(nome, momentos[i].Tempo, momentos[i].Motivo));
        }

        var indice = new IndiceFrames(VersaoSelecao, OrigemTranscricao.De(video), frames);
        File.WriteAllText(Path.Combine(pasta, ArquivoIndice), JsonSerializer.Serialize(indice, Opcoes), Encoding.UTF8);
        return new FramesExtraidos(pasta, frames, Reaproveitados: false);
    }

    /// <summary>Lê os quadros reduzidos do ffmpeg, um por um, e devolve os momentos escolhidos pelo detector.</summary>
    internal async Task<IReadOnlyList<MomentoEscolhido>> EscolherMomentosAsync(
        string video, ParametrosSelecao p, CancellationToken cancelamento)
    {
        var detector = new DetectorMudancas(p);
        var resultado = await ExecutorProcesso.ExecutarLendoBytesAsync(ferramentas.Ffmpeg,
        [
            "-hide_banner", "-nostdin", "-v", "error",
            "-i", video,
            "-map", "0:v:0", "-an",
            "-vf", $"fps={p.QuadrosPorSegundo},scale={p.Largura}:{p.Altura}",
            "-pix_fmt", "gray", "-f", "rawvideo", "pipe:1",
        ],
        async (saida, ct) =>
        {
            var quadro = new byte[p.BytesPorQuadro];
            while (await LerQuadroAsync(saida, quadro, ct))
                detector.Adicionar((byte[])quadro.Clone());
        },
        cancelamento);

        if (resultado.CodigoSaida != 0)
            throw new MinutaException(
                "Falha ao ler as imagens do vídeo.",
                "Detalhe do ffmpeg: " + ExecutorProcesso.Resumir(resultado.Erros));

        return detector.Concluir();
    }

    /// <summary>Lê exatamente um quadro; false no fim do fluxo (um quadro incompleto no fim é ignorado).</summary>
    private static async Task<bool> LerQuadroAsync(Stream saida, byte[] quadro, CancellationToken ct)
    {
        var lidos = 0;
        while (lidos < quadro.Length)
        {
            var n = await saida.ReadAsync(quadro.AsMemory(lidos), ct);
            if (n == 0)
                return false;
            lidos += n;
        }
        return true;
    }

    private async Task ExtrairJpegAsync(string video, TimeSpan tempo, string destino, CancellationToken cancelamento)
    {
        // Bem no fim do vídeo o ffmpeg pode não achar quadro; nesse caso tenta meio segundo antes.
        if (!await TentarExtrairJpegAsync(video, tempo, destino, cancelamento)
            && !await TentarExtrairJpegAsync(video, tempo > TimeSpan.FromSeconds(0.5) ? tempo - TimeSpan.FromSeconds(0.5) : TimeSpan.Zero, destino, cancelamento))
            throw new MinutaException(
                $"Falha ao salvar o frame de {Transcricao.FormatarTempo(tempo)}.",
                "Confira se o vídeo abre normalmente; ele pode estar incompleto.");
    }

    private async Task<bool> TentarExtrairJpegAsync(string video, TimeSpan tempo, string destino, CancellationToken cancelamento)
    {
        var resultado = await ExecutorProcesso.ExecutarAsync(ferramentas.Ffmpeg,
        [
            "-hide_banner", "-nostdin", "-v", "error", "-y",
            "-ss", tempo.TotalSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
            "-i", video,
            "-frames:v", "1",
            "-vf", $"scale='min({LarguraMaximaJpeg},iw)':-2",
            "-q:v", "3",
            destino,
        ], cancelamento);

        return resultado.CodigoSaida == 0 && File.Exists(destino) && new FileInfo(destino).Length > 0;
    }

    /// <summary>Apaga só o que o próprio Minuta gerou na pasta (frames numerados e o índice).</summary>
    private static void LimparPasta(string pasta)
    {
        foreach (var arquivo in Directory.EnumerateFiles(pasta, "???_*.jpg"))
            File.Delete(arquivo);
        File.Delete(Path.Combine(pasta, ArquivoIndice));
    }

    private static IReadOnlyList<FrameVideo>? CarregarIndice(string pasta, FileInfo video)
    {
        var caminho = Path.Combine(pasta, ArquivoIndice);
        if (!File.Exists(caminho))
            return null;
        try
        {
            var indice = JsonSerializer.Deserialize<IndiceFrames>(File.ReadAllText(caminho), Opcoes);
            var valido = indice is not null
                && indice.Versao == VersaoSelecao
                && indice.Origem == OrigemTranscricao.De(video)
                && indice.Frames.All(f => File.Exists(Path.Combine(pasta, f.Arquivo)));
            return valido ? indice!.Frames : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record IndiceFrames(int Versao, OrigemTranscricao Origem, IReadOnlyList<FrameVideo> Frames);
}
