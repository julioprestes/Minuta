namespace Minuta.Core.Midia;

/// <summary>
/// Ajustes da escolha de frames. Os padrões foram pensados para gravação de tela em 1080p:
/// o cursor andando não conta como mudança, mas uma janela, um aviso ou uma tela nova contam.
/// As frações são da área da imagem reduzida (<see cref="Largura"/> × <see cref="Altura"/>).
/// </summary>
public sealed record ParametrosSelecao
{
    /// <summary>Quadros analisados por segundo.</summary>
    public int QuadrosPorSegundo { get; init; } = 2;

    /// <summary>Tamanho da imagem usada só para comparar (tons de cinza).</summary>
    public int Largura { get; init; } = 320;
    public int Altura { get; init; } = 180;

    /// <summary>Diferença de brilho (0–255) para um pixel contar como mudado; abaixo disso é ruído de compressão.</summary>
    public int DiferencaPixel { get; init; } = 24;

    /// <summary>Até esta fração de pixels mudados entre dois quadros seguidos, a tela está parada (o cursor fica abaixo).</summary>
    public double LimiarEstavel { get; init; } = 0.001;

    /// <summary>Quadros parados seguidos para considerar que a tela "assentou" depois de uma mudança.</summary>
    public int QuadrosParaEstabilizar { get; init; } = 2;

    /// <summary>Fração mínima de diferença em relação ao último frame escolhido para valer como tela nova.</summary>
    public double LimiarNovaTela { get; init; } = 0.002;

    /// <summary>Tela parada por muito tempo: escolhe um frame a cada intervalo, se algo mudou (ex.: digitação).</summary>
    public TimeSpan IntervaloMaximo { get; init; } = TimeSpan.FromSeconds(15);
    public double LimiarIntervalo { get; init; } = 0.0005;

    /// <summary>Teto por vídeo; acima dele ficam o primeiro, o último e as maiores mudanças.</summary>
    public int MaximoFrames { get; init; } = 60;

    public int BytesPorQuadro => Largura * Altura;
}

/// <summary>Momento escolhido e por quê ("início", "mudança de tela", "intervalo", "fim").</summary>
/// <param name="Tempo">Quando a tela passou a ser essa (vai para a linha do tempo).</param>
/// <param name="TempoImagem">
/// De onde tirar a imagem: no meio do trecho parado, e não na borda. Na borda, uma diferença mínima de alinhamento
/// entre a amostragem e a extração pega o quadro de antes da mudança (ex.: o menu ainda fechado).
/// </param>
public sealed record MomentoEscolhido(TimeSpan Tempo, string Motivo, TimeSpan TempoImagem);

/// <summary>
/// Recebe os quadros reduzidos, em ordem, e escolhe os momentos que merecem um frame: cada vez que a tela
/// muda e assenta (o frame é o do começo da tela parada, não o do meio da transição), a cada intervalo
/// longo se algo mudou e o último quadro, que costuma mostrar o resultado. Não decodifica vídeo: só compara bytes.
/// </summary>
internal sealed class DetectorMudancas(ParametrosSelecao p)
{
    private const string Inicio = "início", MudancaDeTela = "mudança de tela", Intervalo = "intervalo", Fim = "fim";

    private sealed record Candidato(int Indice, string Motivo, double Mudanca)
    {
        /// <summary>Telas que assentaram ficam paradas por pelo menos dois quadros: a imagem sai do meio.</summary>
        public bool TemTrechoParado => Motivo is Inicio or MudancaDeTela;
    }

    private readonly List<Candidato> escolhidos = [];
    private byte[]? anterior, ultimoEscolhido, inicioParado;
    private int indice = -1, indiceInicioParado, quadrosParados, indiceUltimoEscolhido = -1;

    public void Adicionar(byte[] quadro)
    {
        if (quadro.Length != p.BytesPorQuadro)
            throw new ArgumentException($"Quadro com {quadro.Length} bytes; esperado {p.BytesPorQuadro}.", nameof(quadro));

        indice++;
        if (anterior is null || Diferenca(anterior, quadro) >= p.LimiarEstavel)
        {
            quadrosParados = 1;
            indiceInicioParado = indice;
            inicioParado = quadro;
        }
        else
        {
            quadrosParados++;
        }
        anterior = quadro;

        if (quadrosParados == p.QuadrosParaEstabilizar)
        {
            // A tela acabou de assentar: vale se for diferente do último frame escolhido.
            var mudanca = ultimoEscolhido is null ? 1.0 : Diferenca(ultimoEscolhido, inicioParado!);
            if (mudanca >= p.LimiarNovaTela)
                Escolher(indiceInicioParado, inicioParado!, ultimoEscolhido is null ? Inicio : MudancaDeTela, mudanca);
        }
        else if (ultimoEscolhido is not null && Tempo(indice) - Tempo(indiceUltimoEscolhido) >= p.IntervaloMaximo)
        {
            var mudanca = Diferenca(ultimoEscolhido, quadro);
            if (mudanca >= p.LimiarIntervalo)
                Escolher(indice, quadro, Intervalo, mudanca);
        }
    }

    public IReadOnlyList<MomentoEscolhido> Concluir()
    {
        if (anterior is not null && indice != indiceUltimoEscolhido)
        {
            var mudanca = ultimoEscolhido is null ? 1.0 : Diferenca(ultimoEscolhido, anterior);
            if (mudanca >= p.LimiarNovaTela)
                Escolher(indice, anterior, Fim, mudanca);
        }

        var lista = escolhidos;
        if (lista.Count > p.MaximoFrames)
        {
            var maiores = lista.Skip(1).SkipLast(1).OrderByDescending(c => c.Mudanca).Take(p.MaximoFrames - 2);
            lista = [lista[0], .. maiores, lista[^1]];
        }

        var meioQuadro = TimeSpan.FromSeconds(0.5 / p.QuadrosPorSegundo);
        return lista.OrderBy(c => c.Indice)
            .Select(c => new MomentoEscolhido(
                Tempo(c.Indice), c.Motivo, c.TemTrechoParado ? Tempo(c.Indice) + meioQuadro : Tempo(c.Indice)))
            .ToList();
    }

    private void Escolher(int indiceQuadro, byte[] quadro, string motivo, double mudanca)
    {
        escolhidos.Add(new Candidato(indiceQuadro, motivo, mudanca));
        ultimoEscolhido = quadro;
        indiceUltimoEscolhido = indiceQuadro;
    }

    private TimeSpan Tempo(int indiceQuadro) => TimeSpan.FromSeconds((double)indiceQuadro / p.QuadrosPorSegundo);

    /// <summary>Fração de pixels cuja diferença de brilho passa de <see cref="ParametrosSelecao.DiferencaPixel"/>.</summary>
    private double Diferenca(byte[] a, byte[] b)
    {
        var mudados = 0;
        for (var i = 0; i < a.Length; i++)
            if (Math.Abs(a[i] - b[i]) > p.DiferencaPixel)
                mudados++;
        return (double)mudados / a.Length;
    }
}
