using Minuta.Core.Midia;

namespace Minuta.Core.Tests;

public class LocalizadorFfmpegTests
{
    private static readonly string Ext = OperatingSystem.IsWindows() ? ".exe" : "";

    [Fact]
    public void Encontra_pela_pasta_configurada()
    {
        using var temp = new PastaTemporaria();
        temp.CriarArquivo("ffmpeg" + Ext);
        temp.CriarArquivo("ffprobe" + Ext);

        var ferramentas = LocalizadorFfmpeg.Localizar(temp.Caminho, variavelPath: null);

        Assert.Equal(Path.Combine(temp.Caminho, "ffprobe" + Ext), ferramentas.Ffprobe);
    }

    [Fact]
    public void Encontra_pelo_executavel_configurado()
    {
        using var temp = new PastaTemporaria();
        var ffmpeg = temp.CriarArquivo("ffmpeg" + Ext);
        temp.CriarArquivo("ffprobe" + Ext);

        Assert.Equal(ffmpeg, LocalizadorFfmpeg.Localizar(ffmpeg, variavelPath: null).Ffmpeg);
    }

    [Fact]
    public void Encontra_no_PATH()
    {
        using var temp = new PastaTemporaria();
        temp.CriarArquivo(Path.Combine("bin", "ffmpeg" + Ext));
        temp.CriarArquivo(Path.Combine("bin", "ffprobe" + Ext));
        var path = string.Join(Path.PathSeparator, Path.Combine(temp.Caminho, "nada"), Path.Combine(temp.Caminho, "bin"));

        var ferramentas = LocalizadorFfmpeg.Localizar(null, path);

        Assert.Equal(Path.Combine(temp.Caminho, "bin", "ffmpeg" + Ext), ferramentas.Ffmpeg);
    }

    [Fact]
    public void Sem_ffprobe_ao_lado_falha()
    {
        using var temp = new PastaTemporaria();
        temp.CriarArquivo("ffmpeg" + Ext);

        Assert.Throws<MinutaException>(() => LocalizadorFfmpeg.Localizar(temp.Caminho, variavelPath: null));
    }

    [Fact]
    public void Ausente_no_PATH_falha_com_dica()
    {
        using var temp = new PastaTemporaria();

        var ex = Assert.Throws<MinutaException>(() => LocalizadorFfmpeg.Localizar(null, temp.Caminho));

        Assert.NotNull(ex.Dica);
    }
}

public class ArquivoDeEntradaTests
{
    [Fact]
    public void Aceita_video_existente()
    {
        using var temp = new PastaTemporaria();
        var video = temp.CriarArquivo("reuniao.MP4");

        Assert.Equal(video, ArquivoDeEntrada.Validar(video).FullName);
    }

    [Fact]
    public void Aceita_caminho_entre_aspas()
    {
        using var temp = new PastaTemporaria();
        var video = temp.CriarArquivo("reuniao com espaco.mp4");

        Assert.Equal(video, ArquivoDeEntrada.Validar($"\"{video}\"").FullName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nao-existe.mp4")]
    public void Recusa_caminho_vazio_ou_inexistente(string? caminho)
    {
        Assert.Throws<MinutaException>(() => ArquivoDeEntrada.Validar(caminho));
    }

    [Fact]
    public void Recusa_extensao_desconhecida()
    {
        using var temp = new PastaTemporaria();
        var arquivo = temp.CriarArquivo("documento.pdf");

        var ex = Assert.Throws<MinutaException>(() => ArquivoDeEntrada.Validar(arquivo));

        Assert.Contains(".mp4", ex.Dica);
    }

    [Fact]
    public void Recusa_arquivo_vazio()
    {
        using var temp = new PastaTemporaria();
        var video = temp.CriarArquivo("vazio.mp4", conteudo: "");

        Assert.Throws<MinutaException>(() => ArquivoDeEntrada.Validar(video));
    }
}
