using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Minuta.Core.Midia;
using Minuta.Core.Visao;

namespace Minuta.Core.Tests;

public class DescritorFramesTests
{
    /// <summary>Pasta de frames falsa (bytes quaisquer como "JPEG"), com tempos fictícios.</summary>
    private static FramesExtraidos Frames(PastaTemporaria temp, int quantidade)
    {
        var frames = Enumerable.Range(0, quantidade)
            .Select(i => new FrameVideo($"{i + 1:000}_00-{i * 5:00}.jpg", TimeSpan.FromSeconds(i * 5), i == 0 ? "início" : "mudança de tela"))
            .ToList();
        foreach (var f in frames)
            temp.CriarArquivo(Path.Combine("v.frames", f.Arquivo), "imagem falsa " + f.Arquivo);
        return new FramesExtraidos(Path.Combine(temp.Caminho, "v.frames"), frames, Reaproveitados: false);
    }

    private static object Tela(int frame, string titulo = "Sistema Exemplo - Vendas", string menu = "", params string[] mensagens) => new
    {
        frame,
        titulo_janela = titulo,
        caminho_menu = menu,
        campos_e_valores = Array.Empty<string>(),
        mensagens,
        botoes = Array.Empty<string>(),
        descricao = $"Tela do frame {frame}.",
        acao_provavel = "",
    };

    private static Func<HttpResponseMessage> Resposta(params object[] telas) =>
        () => HandlerFalso.Json(HttpStatusCode.OK, Exemplos.RespostaChat(new { frames = telas }));

    private static DescritorFramesOpenRouter Descritor(HandlerFalso handler, int porLote = 10) =>
        new(new HttpClient(handler), "chave", "modelo/visao:free")
        {
            FramesPorLote = porLote,
            EsperaEntreTentativas = _ => TimeSpan.Zero,
        };

    [Fact]
    public async Task Descreve_e_casa_cada_tela_com_o_frame_pelo_numero()
    {
        using var temp = new PastaTemporaria();
        var frames = Frames(temp, 2);
        // O modelo devolve fora de ordem: o casamento é pelo número do frame.
        var handler = new HandlerFalso(Resposta(
            Tela(2, menu: "", mensagens: "Estoque insuficiente para o produto 77."),
            Tela(1, menu: "Vendas > Pedidos > Novo pedido")));

        var visao = await Descritor(handler).DescreverAsync(frames);

        Assert.Equal("modelo/visao:free", visao.Modelo);
        Assert.Equal("Vendas > Pedidos > Novo pedido", visao.Telas[0].CaminhoMenu);
        Assert.Equal(frames.Frames[0].Arquivo, visao.Telas[0].Arquivo);
        Assert.Equal(["Estoque insuficiente para o produto 77."], visao.Telas[1].Mensagens);
        Assert.Equal(TimeSpan.FromSeconds(5), visao.Telas[1].Tempo);

        // A requisição leva as imagens em base64, cada uma com rótulo e tempo, e o schema estrito.
        var corpo = JsonNode.Parse(handler.UltimoCorpo!)!;
        Assert.Equal("json_schema", corpo["response_format"]!["type"]!.GetValue<string>());
        var partes = corpo["messages"]![1]!["content"]!.AsArray();
        Assert.Equal(2, partes.Count(p => p!["type"]!.GetValue<string>() == "image_url"));
        Assert.StartsWith("data:image/jpeg;base64,", partes.First(p => p!["type"]!.GetValue<string>() == "image_url")!["image_url"]!["url"]!.GetValue<string>());
        Assert.Contains(partes, p => p!["text"]?.GetValue<string>() == "Frame 2 [00:05]:");
    }

    [Fact]
    public async Task Divide_em_lotes_e_passa_a_ultima_tela_adiante()
    {
        using var temp = new PastaTemporaria();
        var frames = Frames(temp, 3);
        var handler = new HandlerFalso(
            Resposta(Tela(1), Tela(2, titulo: "Consulta de pedidos")),
            Resposta(Tela(1, titulo: "Novo pedido")));

        var visao = await Descritor(handler, porLote: 2).DescreverAsync(frames);

        Assert.Equal(2, handler.Chamadas);
        Assert.Equal(3, visao.Telas.Count);
        Assert.Equal("Novo pedido", visao.Telas[2].TituloJanela);
        Assert.Equal(frames.Frames[2].Arquivo, visao.Telas[2].Arquivo);
        var textoDoLote = JsonNode.Parse(handler.UltimoCorpo!)!["messages"]![1]!["content"]![0]!["text"]!.GetValue<string>();
        Assert.Contains("Última tela antes destes frames: Consulta de pedidos", textoDoLote);
    }

    [Fact]
    public async Task Lote_incompleto_pede_de_novo_e_depois_aceita_com_frame_vazio()
    {
        using var temp = new PastaTemporaria();
        var frames = Frames(temp, 2);
        var handler = new HandlerFalso(Resposta(Tela(1)), Resposta(Tela(1)));

        var visao = await Descritor(handler).DescreverAsync(frames);

        Assert.Equal(2, handler.Chamadas);
        Assert.False(visao.Telas[0].Vazia);
        Assert.True(visao.Telas[1].Vazia); // registrado sem inventar conteúdo
        Assert.Equal("(sem descrição)", visao.Telas[1].Resumo());
    }

    [Fact]
    public async Task Json_invalido_duas_vezes_gera_erro_tratado()
    {
        using var temp = new PastaTemporaria();
        var invalido = () => HandlerFalso.Json(HttpStatusCode.OK, Exemplos.RespostaChat("não é json"));
        var handler = new HandlerFalso(invalido, invalido);

        var ex = await Assert.ThrowsAsync<MinutaException>(() => Descritor(handler).DescreverAsync(Frames(temp, 1)));

        Assert.Contains("fora do formato", ex.Message);
        Assert.Contains("OPENROUTER_MODELO_VISAO", ex.Dica);
    }

    [Fact]
    public async Task Modelo_bloqueado_para_apps_gera_erro_com_dica()
    {
        using var temp = new PastaTemporaria();
        var handler = new HandlerFalso(() => HandlerFalso.Json(HttpStatusCode.Forbidden,
            """{"error":{"message":"only available on agentic harnesses","code":403}}"""));

        var ex = await Assert.ThrowsAsync<MinutaException>(() => Descritor(handler).DescreverAsync(Frames(temp, 1)));

        Assert.Contains("agentic harnesses", ex.Message);
        Assert.Contains("OPENROUTER_MODELO_VISAO", ex.Dica);
    }

    [Fact]
    public void Resumo_mostra_janela_menu_e_mensagens()
    {
        var tela = new DescricaoTela("001_00-00.jpg", TimeSpan.Zero, "Novo pedido", "Vendas > Pedidos", [], ["Erro X"], [], "", "");

        Assert.Equal("Novo pedido | menu: Vendas > Pedidos | mensagem: \"Erro X\"", tela.Resumo());
    }
}

public class ArquivoVisaoTests
{
    [Fact]
    public void Reaproveita_so_com_mesmo_video_modelo_versao_e_frames()
    {
        using var temp = new PastaTemporaria();
        var video = new FileInfo(temp.CriarArquivo("v.mp4", "vídeo"));
        var frames = new List<FrameVideo> { new("001_00-00.jpg", TimeSpan.Zero, "início"), new("002_00-04.jpg", TimeSpan.FromSeconds(4), "mudança de tela") };
        var telas = frames.Select(f => new DescricaoTela(f.Arquivo, f.Tempo, "Tela", "", [], [], [], "Descrição.", "")).ToList();
        var caminho = Path.Combine(temp.Caminho, "v" + ArquivoVisao.Sufixo);
        ArquivoVisao.Salvar(new DescricaoVisual("m:free", 1, telas) { Origem = Transcricoes.OrigemTranscricao.De(video) }, caminho);

        Assert.NotNull(ArquivoVisao.Carregar(caminho, video, "m:free", 1, frames));
        Assert.Null(ArquivoVisao.Carregar(caminho, video, "outro:free", 1, frames));
        Assert.Null(ArquivoVisao.Carregar(caminho, video, "m:free", 2, frames));
        Assert.Null(ArquivoVisao.Carregar(caminho, video, "m:free", 1, frames.Take(1).ToList()));
        Assert.Contains("Descrição.", File.ReadAllText(caminho)); // acentos legíveis
    }
}
