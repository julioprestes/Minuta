using System.Net;
using System.Text;
using Minuta.Core.Transcricoes;

namespace Minuta.Core.Tests;

/// <summary>Servidor falso: devolve as respostas da fila, em ordem, e conta as chamadas.</summary>
internal sealed class HandlerFalso(params Func<HttpResponseMessage>[] respostas) : HttpMessageHandler
{
    private readonly Queue<Func<HttpResponseMessage>> fila = new(respostas);
    public int Chamadas { get; private set; }
    public HttpRequestMessage? UltimaRequisicao { get; private set; }
    public string? UltimoCorpo { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage requisicao, CancellationToken cancelamento)
    {
        Chamadas++;
        UltimaRequisicao = requisicao;
        UltimoCorpo = requisicao.Content is null ? null : await requisicao.Content.ReadAsStringAsync(cancelamento);
        return fila.Dequeue()();
    }

    public static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

public class TranscritorGroqTests
{
    private const string RespostaOk = """
        {
          "text": " Olá, vou mostrar o cadastro de pedidos. Aqui o botão salvar não funciona.",
          "duration": 12.5,
          "segments": [
            {"id":0,"start":0.0,"end":4.2,"text":" Olá, vou mostrar o cadastro de pedidos.","avg_logprob":-0.2,"no_speech_prob":0.01,"compression_ratio":1.3},
            {"id":1,"start":4.2,"end":9.0,"text":" Aqui o botão salvar não funciona.","avg_logprob":-1.4,"no_speech_prob":0.1,"compression_ratio":1.2},
            {"id":2,"start":9.0,"end":12.5,"text":" Legendas pela comunidade.","avg_logprob":-1.6,"no_speech_prob":0.95,"compression_ratio":1.0}
          ]
        }
        """;

    private static (TranscritorGroq, HandlerFalso) Criar(params Func<HttpResponseMessage>[] respostas)
    {
        var handler = new HandlerFalso(respostas);
        var transcritor = new TranscritorGroq(new HttpClient(handler), "chave-teste", "whisper-large-v3", "pt")
        {
            EsperaEntreTentativas = _ => TimeSpan.Zero,
        };
        return (transcritor, handler);
    }

    private static string AudioFalso(PastaTemporaria temp) => temp.CriarArquivo("reuniao.audio.ogg", "áudio falso");

    [Fact]
    public async Task Transcreve_com_tempos_descarta_silencio_e_marca_baixa_confianca()
    {
        using var temp = new PastaTemporaria();
        var (transcritor, handler) = Criar(() => HandlerFalso.Json(HttpStatusCode.OK, RespostaOk));

        var t = await transcritor.TranscreverAsync(AudioFalso(temp));

        Assert.Equal(2, t.Segmentos.Count);
        Assert.Equal(1, t.SegmentosDescartados);
        Assert.Equal("Olá, vou mostrar o cadastro de pedidos.", t.Segmentos[0].Texto);
        Assert.False(t.Segmentos[0].BaixaConfianca);
        Assert.True(t.Segmentos[1].BaixaConfianca);
        Assert.Equal(TimeSpan.FromSeconds(4.2), t.Segmentos[1].Inicio);
        Assert.Equal(TimeSpan.FromSeconds(12.5), t.Duracao);

        Assert.Equal("Bearer", handler.UltimaRequisicao!.Headers.Authorization!.Scheme);
        Assert.Contains("verbose_json", handler.UltimoCorpo);
        Assert.Contains("whisper-large-v3", handler.UltimoCorpo);
    }

    [Fact]
    public async Task Chave_recusada_gera_erro_claro_sem_nova_tentativa()
    {
        using var temp = new PastaTemporaria();
        var (transcritor, handler) = Criar(
            () => HandlerFalso.Json(HttpStatusCode.Unauthorized, """{"error":{"message":"Invalid API Key","type":"invalid_request_error"}}"""));

        var ex = await Assert.ThrowsAsync<MinutaException>(() => transcritor.TranscreverAsync(AudioFalso(temp)));

        Assert.Contains("chave", ex.Message);
        Assert.Equal(1, handler.Chamadas);
    }

    [Fact]
    public async Task Limite_429_tenta_de_novo_e_conclui()
    {
        using var temp = new PastaTemporaria();
        var (transcritor, handler) = Criar(
            () => HandlerFalso.Json(HttpStatusCode.TooManyRequests, """{"error":{"message":"Rate limit"}}"""),
            () => HandlerFalso.Json(HttpStatusCode.OK, RespostaOk));

        var t = await transcritor.TranscreverAsync(AudioFalso(temp));

        Assert.Equal(2, handler.Chamadas);
        Assert.Equal(2, t.Segmentos.Count);
    }

    [Fact]
    public async Task Retry_after_longo_desiste_na_hora()
    {
        using var temp = new PastaTemporaria();
        var (transcritor, handler) = Criar(() =>
        {
            var r = HandlerFalso.Json(HttpStatusCode.TooManyRequests, """{"error":{"message":"daily limit"}}""");
            r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(30));
            return r;
        });

        var ex = await Assert.ThrowsAsync<MinutaException>(() => transcritor.TranscreverAsync(AudioFalso(temp)));

        Assert.Contains("Limite", ex.Message);
        Assert.Contains("30 min", ex.Dica);
        Assert.Equal(1, handler.Chamadas);
    }

    [Fact]
    public async Task Erro_do_servidor_persistente_desiste_apos_tres_tentativas()
    {
        using var temp = new PastaTemporaria();
        var (transcritor, handler) = Criar(
            () => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            () => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            () => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        var ex = await Assert.ThrowsAsync<MinutaException>(() => transcritor.TranscreverAsync(AudioFalso(temp)));

        Assert.Contains("indisponível", ex.Message);
        Assert.Equal(3, handler.Chamadas);
    }

    [Fact]
    public async Task Falha_de_rede_tenta_de_novo_e_depois_explica()
    {
        using var temp = new PastaTemporaria();
        Func<HttpResponseMessage> semRede = () => throw new HttpRequestException("Nenhuma conexão");
        var (transcritor, handler) = Criar(semRede, semRede, semRede);

        var ex = await Assert.ThrowsAsync<MinutaException>(() => transcritor.TranscreverAsync(AudioFalso(temp)));

        Assert.Contains("rede", ex.Message);
        Assert.Equal(3, handler.Chamadas);
    }

    [Theory]
    [InlineData("isto não é json")]
    [InlineData("{}")]
    public async Task Resposta_invalida_gera_erro_tratado(string corpo)
    {
        using var temp = new PastaTemporaria();
        var (transcritor, _) = Criar(() => HandlerFalso.Json(HttpStatusCode.OK, corpo));

        var ex = await Assert.ThrowsAsync<MinutaException>(() => transcritor.TranscreverAsync(AudioFalso(temp)));

        Assert.Contains("Resposta inválida", ex.Message);
    }

    [Fact]
    public void Separa_frases_coladas_sem_mexer_em_numeros()
    {
        var t = TranscritorGroq.Interpretar("""
            {"segments":[{"start":0,"end":5,"text":" Tem 100 unidades.A regra é 10.5 por cento!Certo?Sim","avg_logprob":-0.1,"no_speech_prob":0,"compression_ratio":1}]}
            """, "m", "pt");

        Assert.Equal("Tem 100 unidades. A regra é 10.5 por cento! Certo? Sim", t.Segmentos[0].Texto);
    }

    [Fact]
    public void Resposta_sem_trechos_vira_um_trecho_unico()
    {
        var t = TranscritorGroq.Interpretar("""{"text":" Tudo certo.","duration":3}""", "m", "pt");

        Assert.Single(t.Segmentos);
        Assert.Equal("Tudo certo.", t.Segmentos[0].Texto);
    }
}

public class ArquivoTranscricaoTests
{
    private static Transcricao Exemplo(FileInfo video) =>
        new Transcricao("whisper-large-v3", "pt", TimeSpan.FromSeconds(75),
        [
            new SegmentoTranscricao(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(8), "Vou abrir a tela de pedidos."),
            new SegmentoTranscricao(TimeSpan.FromSeconds(65), TimeSpan.FromSeconds(70), "Aqui dá erro ao salvar.", BaixaConfianca: true),
        ])
        { Origem = OrigemTranscricao.De(video) };

    [Fact]
    public void Texto_tem_tempo_em_cada_linha_e_marca_baixa_confianca()
    {
        using var temp = new PastaTemporaria();
        var texto = Exemplo(new FileInfo(temp.CriarArquivo("v.mp4"))).ParaTextoComTempos();

        Assert.Equal(
            "[00:03] Vou abrir a tela de pedidos." + Environment.NewLine +
            "[01:05] Aqui dá erro ao salvar. (?)" + Environment.NewLine,
            texto);
    }

    [Fact]
    public void Formata_tempo_acima_de_uma_hora()
    {
        Assert.Equal("1:02:03", Transcricao.FormatarTempo(new TimeSpan(1, 2, 3)));
    }

    [Fact]
    public void Salva_e_reaproveita_a_transcricao_do_mesmo_video()
    {
        using var temp = new PastaTemporaria();
        var video = new FileInfo(temp.CriarArquivo("reuniao.mp4", "conteúdo"));
        var json = Path.Combine(temp.Caminho, "reuniao" + ArquivoTranscricao.SufixoJson);
        var txt = Path.Combine(temp.Caminho, "reuniao" + ArquivoTranscricao.SufixoTexto);

        ArquivoTranscricao.Salvar(Exemplo(video), json, txt);
        var lida = ArquivoTranscricao.Carregar(json, video);

        Assert.NotNull(lida);
        Assert.Equal(Exemplo(video).Segmentos, lida.Segmentos);
        Assert.Contains("Aqui dá erro ao salvar.", File.ReadAllText(json)); // acentos legíveis no arquivo
        Assert.True(File.Exists(txt));
    }

    [Fact]
    public void Nao_reaproveita_se_o_video_mudou()
    {
        using var temp = new PastaTemporaria();
        var caminhoVideo = temp.CriarArquivo("reuniao.mp4", "conteúdo");
        var json = Path.Combine(temp.Caminho, "reuniao" + ArquivoTranscricao.SufixoJson);
        ArquivoTranscricao.Salvar(Exemplo(new FileInfo(caminhoVideo)), json, Path.Combine(temp.Caminho, "r.txt"));

        File.WriteAllText(caminhoVideo, "outro vídeo, com outro tamanho");

        Assert.Null(ArquivoTranscricao.Carregar(json, new FileInfo(caminhoVideo)));
    }

    [Fact]
    public void Arquivo_corrompido_nao_e_reaproveitado()
    {
        using var temp = new PastaTemporaria();
        var video = new FileInfo(temp.CriarArquivo("reuniao.mp4"));
        var json = temp.CriarArquivo("reuniao.transcricao.json", "{ quebrado");

        Assert.Null(ArquivoTranscricao.Carregar(json, video));
    }
}

public class RepeticaoAlucinadaTests
{
    [Theory]
    [InlineData("E aí E aí E aí E aí E aí E aí E aí E aí E aí E aí E aí E aí")]
    [InlineData("Obrigado. Obrigado. Obrigado. Obrigado. Obrigado.")]
    [InlineData("Legendas pela comunidade, legendas pela comunidade, legendas pela comunidade, legendas pela comunidade")]
    public void Frase_curta_repetida_ocupa_o_trecho_todo(string texto)
    {
        Assert.True(TranscritorGroq.CoberturaDeRepeticao(texto) >= TranscritorGroq.CoberturaParaDescartar);
    }

    [Theory]
    [InlineData("Não, não, não, aqui é outra tela.")]
    [InlineData("Quando eu clico em salvar, aparece a mensagem de estoque insuficiente.")]
    [InlineData("")]
    public void Fala_normal_nao_conta_como_repeticao(string texto)
    {
        Assert.Equal(0, TranscritorGroq.CoberturaDeRepeticao(texto));
    }

    [Fact]
    public void Descarta_trecho_repetido_e_marca_o_que_mistura_fala_real()
    {
        const string corpo = """
            {"text":"...","duration":30.0,"segments":[
              {"start":0,"end":8,"text":" Então o relatório não abriu, vou tentar de novo.","avg_logprob":-0.2,"no_speech_prob":0.01,"compression_ratio":1.2},
              {"start":8,"end":20,"text":" E aí E aí E aí E aí E aí E aí E aí E aí","avg_logprob":-0.3,"no_speech_prob":0.2,"compression_ratio":1.9},
              {"start":20,"end":30,"text":" Bom, por algum motivo o relatório da tela de pedidos não está abrindo de jeito nenhum, sim sim sim sim.","avg_logprob":-0.2,"no_speech_prob":0.01,"compression_ratio":1.3}
            ]}
            """;

        var t = TranscritorGroq.Interpretar(corpo, "whisper-large-v3", "pt");

        Assert.Equal(2, t.Segmentos.Count);
        Assert.Equal(1, t.SegmentosDescartados);
        Assert.False(t.Segmentos[0].BaixaConfianca);
        Assert.True(t.Segmentos[1].BaixaConfianca);
    }
}

public class RespostaBrutaTests
{
    [Fact]
    public async Task Grava_a_resposta_como_chegou()
    {
        using var temp = new PastaTemporaria();
        const string corpo = """{"text":"Até então.","duration":1.0,"segments":[{"start":0,"end":1,"text":"Até então.","avg_logprob":-0.1,"no_speech_prob":0.01,"compression_ratio":1.0}]}""";
        var handler = new HandlerFalso(() => HandlerFalso.Json(System.Net.HttpStatusCode.OK, corpo));
        var bruta = Path.Combine(temp.Caminho, "v" + ArquivoTranscricao.SufixoRespostaBruta);

        await new TranscritorGroq(new HttpClient(handler), "chave", "whisper-large-v3", "pt")
            .TranscreverAsync(temp.CriarArquivo("v.audio.ogg"), bruta);

        Assert.Equal(corpo, File.ReadAllText(bruta)); // sem reformatar: os é mostram o que a API mandou
    }

    [Fact]
    public async Task Grava_mesmo_quando_a_resposta_e_invalida()
    {
        using var temp = new PastaTemporaria();
        var handler = new HandlerFalso(() => HandlerFalso.Json(System.Net.HttpStatusCode.OK, "{ quebrado"));
        var bruta = Path.Combine(temp.Caminho, "v" + ArquivoTranscricao.SufixoRespostaBruta);

        await Assert.ThrowsAsync<MinutaException>(() =>
            new TranscritorGroq(new HttpClient(handler), "chave", "whisper-large-v3", "pt")
                .TranscreverAsync(temp.CriarArquivo("v.audio.ogg"), bruta));

        Assert.Equal("{ quebrado", File.ReadAllText(bruta));
    }
}
