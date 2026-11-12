using Minuta.Core.Midia;

namespace Minuta.Core.Tests;

/// <summary>Teste que precisa do FFmpeg instalado; é marcado como ignorado quando ele não é encontrado.</summary>
public sealed class FactComFfmpegAttribute : FactAttribute
{
    public static readonly FerramentasFfmpeg? Ferramentas = Tentar();

    public FactComFfmpegAttribute()
    {
        if (Ferramentas is null)
            Skip = "FFmpeg não encontrado (PATH ou FFMPEG_PATH).";
    }

    private static FerramentasFfmpeg? Tentar()
    {
        try { return LocalizadorFfmpeg.Localizar(Environment.GetEnvironmentVariable("FFMPEG_PATH")); }
        catch (MinutaException) { return null; }
    }
}

public class ExtratorAudioTests
{
    [Fact]
    public void Interpreta_ffprobe_com_video_e_audio()
    {
        var info = ExtratorAudio.InterpretarFfprobe("""
            {"streams":[{"codec_type":"video"},{"codec_type":"audio"}],"format":{"duration":"612.480000"}}
            """);

        Assert.True(info.TemAudio);
        Assert.True(info.TemVideo);
        Assert.Equal(TimeSpan.FromSeconds(612.48), info.Duracao);
    }

    [Fact]
    public void Interpreta_ffprobe_sem_audio_e_sem_duracao()
    {
        var info = ExtratorAudio.InterpretarFfprobe("""{"streams":[{"codec_type":"video"}],"format":{"duration":"N/A"}}""");

        Assert.False(info.TemAudio);
        Assert.Null(info.Duracao);
    }

    [Fact]
    public void Ffprobe_com_json_invalido_gera_erro_tratado()
    {
        Assert.Throws<MinutaException>(() => ExtratorAudio.InterpretarFfprobe("isto não é json"));
    }

    [FactComFfmpeg]
    public async Task Extrai_audio_de_video_sintetico()
    {
        using var temp = new PastaTemporaria();
        var video = await GerarVideoAsync(temp, "reuniao.mp4", comAudio: true);

        var audio = await Extrator().ExtrairAsync(new FileInfo(video), Path.Combine(temp.Caminho, "saida"));

        Assert.True(File.Exists(audio.Caminho));
        Assert.EndsWith("reuniao.audio.ogg", audio.Caminho);
        Assert.InRange(audio.Bytes, 1, ExtratorAudio.LimiteUploadBytes);
        Assert.NotNull(audio.Duracao);
        Assert.InRange(audio.Duracao.Value.TotalSeconds, 2.5, 3.5);

        var infoAudio = await Extrator().InspecionarAsync(audio.Caminho);
        Assert.True(infoAudio.TemAudio);
        Assert.False(infoAudio.TemVideo);
    }

    [FactComFfmpeg]
    public async Task Video_sem_audio_gera_erro_tratado()
    {
        using var temp = new PastaTemporaria();
        var video = await GerarVideoAsync(temp, "so-tela.mp4", comAudio: false);

        var ex = await Assert.ThrowsAsync<MinutaException>(() => Extrator().ExtrairAsync(new FileInfo(video), temp.Caminho));

        Assert.Contains("trilha de áudio", ex.Message);
    }

    [FactComFfmpeg]
    public async Task Arquivo_corrompido_gera_erro_tratado()
    {
        using var temp = new PastaTemporaria();
        var falso = temp.CriarArquivo("falso.mp4", "isto não é um vídeo");

        await Assert.ThrowsAsync<MinutaException>(() => Extrator().ExtrairAsync(new FileInfo(falso), temp.Caminho));
    }

    [FactComFfmpeg]
    public async Task Cancelamento_interrompe_a_extracao()
    {
        using var temp = new PastaTemporaria();
        var video = await GerarVideoAsync(temp, "reuniao.mp4", comAudio: true);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Extrator().ExtrairAsync(new FileInfo(video), temp.Caminho, new CancellationToken(canceled: true)));
    }

    private static ExtratorAudio Extrator() => new(FactComFfmpegAttribute.Ferramentas!);

    /// <summary>Gera um vídeo de 3 s (imagem de teste + tom de 440 Hz). Nenhum dado real.</summary>
    private static async Task<string> GerarVideoAsync(PastaTemporaria temp, string nome, bool comAudio)
    {
        var destino = Path.Combine(temp.Caminho, nome);
        List<string> args = ["-hide_banner", "-v", "error", "-y", "-f", "lavfi", "-i", "testsrc=duration=3:size=320x240:rate=10"];
        if (comAudio)
            args.AddRange(["-f", "lavfi", "-i", "sine=frequency=440:duration=3", "-shortest"]);
        args.Add(destino);

        var resultado = await ExecutorProcesso.ExecutarAsync(FactComFfmpegAttribute.Ferramentas!.Ffmpeg, args, CancellationToken.None);
        Assert.True(resultado.CodigoSaida == 0, resultado.Erros);
        return destino;
    }
}
