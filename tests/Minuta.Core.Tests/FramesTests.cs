using Minuta.Core.Midia;

namespace Minuta.Core.Tests;

public class DetectorMudancasTests
{
    // Imagem de 100 × 100 = 10.000 pixels: 10 pixels = 0,1%. Dois quadros por segundo.
    private static readonly ParametrosSelecao P = new() { Largura = 100, Altura = 100 };

    private static byte[] Tela(byte fundo = 100) => Enumerable.Repeat(fundo, P.BytesPorQuadro).ToArray();

    /// <summary>Copia a tela e pinta um retângulo (pixels de 0 a 9.999, em linha).</summary>
    private static byte[] Com(byte[] tela, int inicio, int quantidade, byte cor = 250)
    {
        var copia = (byte[])tela.Clone();
        Array.Fill(copia, cor, inicio, quantidade);
        return copia;
    }

    private static IReadOnlyList<MomentoEscolhido> Rodar(IEnumerable<byte[]> quadros, ParametrosSelecao? p = null)
    {
        var detector = new DetectorMudancas(p ?? P);
        foreach (var q in quadros)
            detector.Adicionar(q);
        return detector.Concluir();
    }

    [Fact]
    public void Tela_parada_gera_so_o_frame_inicial()
    {
        var momentos = Rodar(Enumerable.Range(0, 20).Select(_ => Tela()));

        var unico = Assert.Single(momentos);
        Assert.Equal(TimeSpan.Zero, unico.Tempo);
        Assert.Equal("início", unico.Motivo);
    }

    [Fact]
    public void Cursor_andando_nao_conta_como_mudanca()
    {
        // "Cursor" de 4 pixels mudando de lugar a cada quadro: 8 pixels diferentes (0,08%).
        var momentos = Rodar(Enumerable.Range(0, 30).Select(i => Com(Tela(), 1000 + i * 37, 4, cor: 0)));

        Assert.Single(momentos);
    }

    [Fact]
    public void Escolhe_a_tela_depois_que_a_transicao_assenta()
    {
        var antes = Tela();
        var depois = Com(Tela(), 0, 3000); // janela nova cobrindo 30%
        var quadros = new List<byte[]>();
        quadros.AddRange(Enumerable.Repeat(antes, 6));             // 0,0 s a 2,5 s
        quadros.Add(Com(Tela(), 0, 1000));                          // 3,0 s: animação
        quadros.Add(Com(Tela(), 0, 2000));                          // 3,5 s: animação
        quadros.AddRange(Enumerable.Repeat(depois, 6));             // 4,0 s em diante: parada

        var momentos = Rodar(quadros);

        Assert.Equal(2, momentos.Count);
        Assert.Equal(TimeSpan.FromSeconds(4), momentos[1].Tempo);
        Assert.Equal("mudança de tela", momentos[1].Motivo);
        // A imagem sai do meio do trecho parado, não da borda.
        Assert.Equal(TimeSpan.FromSeconds(4.25), momentos[1].TempoImagem);
    }

    [Fact]
    public void Mudanca_pequena_e_lenta_entra_pelo_intervalo()
    {
        // Algo pequeno muda aos 5 s (como uma digitação): 0,07%, abaixo do limiar de tela nova.
        var quadros = Enumerable.Range(0, 40).Select(i => i < 10 ? Tela() : Com(Tela(), 5000, 7)).ToList();

        var momentos = Rodar(quadros);

        Assert.Equal(2, momentos.Count);
        Assert.Equal("intervalo", momentos[1].Motivo);
        Assert.Equal(P.IntervaloMaximo, momentos[1].Tempo);
    }

    [Fact]
    public void Ultimo_quadro_diferente_entra_como_fim()
    {
        var quadros = Enumerable.Repeat(Tela(), 10).Append(Com(Tela(), 0, 2000)).ToList();

        var momentos = Rodar(quadros);

        Assert.Equal("fim", momentos[^1].Motivo);
        Assert.Equal(TimeSpan.FromSeconds(5), momentos[^1].Tempo);
    }

    [Fact]
    public void Respeita_o_teto_mantendo_primeiro_e_ultimo()
    {
        var p = P with { MaximoFrames = 5 };
        // 12 telas diferentes, cada uma parada por 2 quadros.
        var quadros = Enumerable.Range(0, 12).SelectMany(i => Enumerable.Repeat(Com(Tela(), i * 500, 400 + i * 10), 2));

        var momentos = Rodar(quadros, p);

        Assert.Equal(5, momentos.Count);
        Assert.Equal(TimeSpan.Zero, momentos[0].Tempo);
        Assert.Equal(TimeSpan.FromSeconds(11), momentos[^1].Tempo);
        Assert.Equal(momentos.OrderBy(m => m.Tempo), momentos); // em ordem de tempo
    }

    [Fact]
    public void Quadro_com_tamanho_errado_e_recusado()
    {
        Assert.Throws<ArgumentException>(() => new DetectorMudancas(P).Adicionar(new byte[10]));
    }
}

public class ExtratorFramesTests
{
    /// <summary>
    /// "Gravação de tela" sintética de 12 s: fundo cinza, um cursor andando devagar, uma janela que abre aos 4 s
    /// e uma faixa vermelha (como uma mensagem) que aparece aos 8 s. Nenhum dado real.
    /// </summary>
    private static async Task<string> GerarTelaAsync(PastaTemporaria temp)
    {
        var destino = Path.Combine(temp.Caminho, "tela.mp4");
        var resultado = await ExecutorProcesso.ExecutarAsync(FactComFfmpegAttribute.Ferramentas!.Ffmpeg,
        [
            "-hide_banner", "-v", "error", "-y",
            "-f", "lavfi", "-i", "color=c=gray:s=640x360:r=10:d=12",
            "-f", "lavfi", "-i", "color=c=black:s=8x12:r=10:d=12",
            "-filter_complex",
            "[0][1]overlay=x='100+10*t':y=100," +
            "drawbox=x=200:y=120:w=240:h=120:color=white:t=fill:enable='gte(t,4)'," +
            "drawbox=x=220:y=140:w=80:h=10:color=red:t=fill:enable='gte(t,8)'",
            "-pix_fmt", "yuv420p", destino,
        ], CancellationToken.None);
        Assert.True(resultado.CodigoSaida == 0, resultado.Erros);
        return destino;
    }

    [FactComFfmpeg]
    public async Task Escolhe_as_mudancas_de_tela_e_ignora_o_cursor()
    {
        using var temp = new PastaTemporaria();
        var video = new FileInfo(await GerarTelaAsync(temp));
        var extrator = new ExtratorFrames(FactComFfmpegAttribute.Ferramentas!);

        var frames = await extrator.ExtrairAsync(video, temp.Caminho);

        Assert.Equal(3, frames.Frames.Count);
        Assert.Equal(["início", "mudança de tela", "mudança de tela"], frames.Frames.Select(f => f.Motivo));
        Assert.InRange(frames.Frames[1].Tempo.TotalSeconds, 4, 4.5);
        Assert.InRange(frames.Frames[2].Tempo.TotalSeconds, 8, 8.5);
        Assert.All(frames.Frames, f => Assert.True(File.Exists(Path.Combine(frames.Pasta, f.Arquivo))));
        Assert.Equal("001_00-00.jpg", frames.Frames[0].Arquivo);
        Assert.False(frames.Reaproveitados);
    }

    [FactComFfmpeg]
    public async Task Reaproveita_os_frames_do_mesmo_video()
    {
        using var temp = new PastaTemporaria();
        var video = new FileInfo(await GerarTelaAsync(temp));
        var extrator = new ExtratorFrames(FactComFfmpegAttribute.Ferramentas!);

        var primeira = await extrator.ExtrairAsync(video, temp.Caminho);
        var segunda = await extrator.ExtrairAsync(video, temp.Caminho);
        var refeita = await extrator.ExtrairAsync(video, temp.Caminho, refazer: true);

        Assert.True(segunda.Reaproveitados);
        Assert.Equal(primeira.Frames, segunda.Frames);
        Assert.False(refeita.Reaproveitados);
    }
}
