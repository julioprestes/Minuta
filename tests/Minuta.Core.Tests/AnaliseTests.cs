using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Minuta.Core.Analises;
using Minuta.Core.Transcricoes;

namespace Minuta.Core.Tests;

internal static class Exemplos
{
    /// <summary>Transcrição fictícia (sistema de pedidos inventado).</summary>
    public static Transcricao Transcricao() => new("whisper-large-v3", "pt", TimeSpan.FromSeconds(70),
    [
        new(TimeSpan.FromSeconds(0), TimeSpan.FromSeconds(4), "Bom dia, vou mostrar a tela de cadastro de pedidos."),
        new(TimeSpan.FromSeconds(12), TimeSpan.FromSeconds(20), "Quando eu clico em Salvar, aparece a mensagem: estoque insuficiente."),
        new(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(45), "Ficou decidido que o desconto máximo por item continua sendo 10%,"),
        new(TimeSpan.FromSeconds(45), TimeSpan.FromSeconds(52), "sem mudança. Vou confirmar com o cliente a parte das filiais."),
    ]);

    /// <summary>Monta a resposta do chat completions com a análise informada como conteúdo.</summary>
    public static string RespostaChat(object analise, string finishReason = "stop") =>
        new JsonObject
        {
            ["choices"] = new JsonArray(new JsonObject
            {
                ["finish_reason"] = finishReason,
                ["message"] = new JsonObject
                {
                    ["role"] = "assistant",
                    ["content"] = analise as string ?? JsonSerializer.Serialize(analise),
                },
            }),
        }.ToJsonString();

    public static object AnaliseValida() => new
    {
        resumo = "Erro de estoque insuficiente ao salvar pedido.",
        decisoes = new[]
        {
            new { texto = "Desconto máximo por item continua 10%.", citacoes = new[] {
                new { tempo = "00:31", trecho = "o desconto máximo por item continua sendo 10%, sem mudança" } } },
        },
        regras_negocio = Array.Empty<object>(),
        duvidas_em_aberto = new[]
        {
            new { texto = "Pedidos de outras filiais consomem o mesmo estoque?", citacoes = Array.Empty<object>() },
        },
        passos_para_reproduzir = new[]
        {
            new { texto = "Clicar em Salvar.", citacoes = new[] {
                new { tempo = "00:00", trecho = "Quando eu clico em Salvar, aparece a mensagem" } } },
        },
        proximos_passos = Array.Empty<object>(),
        cards = new[]
        {
            new
            {
                titulo = "Corrigir validação de estoque",
                descricao = "A mensagem aparece com estoque disponível.",
                criterios_aceite = new[] { "Salvar sem a mensagem quando há saldo." },
                citacoes = new[] { new { tempo = "01:00", trecho = "o sistema trava quando o cliente é novo" } },
            },
        },
    };
}

public class ConferenciaCitacoesTests
{
    private readonly ConferenciaCitacoes conferencia = new(Exemplos.Transcricao());

    [Fact]
    public void Acha_trecho_ignorando_acentos_maiusculas_e_pontuacao_e_corrige_o_tempo()
    {
        var c = conferencia.Conferir("“quando eu CLICO em salvar aparece a mensagem estoque insuficiente”", "05:00");

        Assert.True(c.Conferida);
        Assert.Equal(TimeSpan.FromSeconds(12), c.Inicio);
        Assert.Equal("quando eu CLICO em salvar aparece a mensagem estoque insuficiente", c.Trecho);
    }

    [Fact]
    public void Acha_trecho_que_atravessa_dois_segmentos_e_usa_o_inicio_do_primeiro()
    {
        var c = conferencia.Conferir("continua sendo 10%, sem mudança. Vou confirmar", null);

        Assert.True(c.Conferida);
        Assert.Equal(TimeSpan.FromSeconds(30), c.Inicio);
    }

    [Fact]
    public void Trecho_inventado_nao_e_conferido_e_mantem_o_tempo_informado()
    {
        var c = conferencia.Conferir("o sistema trava quando o cliente é novo", "[01:05]");

        Assert.False(c.Conferida);
        Assert.Equal(TimeSpan.FromSeconds(65), c.Inicio);
    }

    [Fact]
    public void Trecho_curto_demais_nao_prova_nada()
    {
        Assert.False(conferencia.Conferir("Bom", "00:00").Conferida);
    }

    [Theory]
    [InlineData("01:05", 65)]
    [InlineData("[1:05]", 65)]
    [InlineData("1:02:03", 3723)]
    public void Le_tempos_em_varios_formatos(string tempo, int segundos)
    {
        Assert.Equal(TimeSpan.FromSeconds(segundos), ConferenciaCitacoes.LerTempo(tempo));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ontem")]
    public void Tempo_ilegivel_vira_nulo(string? tempo)
    {
        Assert.Null(ConferenciaCitacoes.LerTempo(tempo));
    }
}

public class RedatorOpenRouterTests
{
    private static (RedatorOpenRouter, HandlerFalso) Criar(params Func<HttpResponseMessage>[] respostas)
    {
        var handler = new HandlerFalso(respostas);
        var redator = new RedatorOpenRouter(new HttpClient(handler), "chave-teste", "modelo/teste:free")
        {
            EsperaEntreTentativas = _ => TimeSpan.Zero,
        };
        return (redator, handler);
    }

    [Fact]
    public async Task Analisa_confere_citacoes_e_envia_schema_estrito()
    {
        var (redator, handler) = Criar(() => HandlerFalso.Json(HttpStatusCode.OK, Exemplos.RespostaChat(Exemplos.AnaliseValida())));

        var a = await redator.AnalisarAsync(Exemplos.Transcricao());

        Assert.Equal("modelo/teste:free", a.Modelo);
        Assert.Equal(RedatorOpenRouter.VersaoPrompt, a.VersaoPrompt);
        Assert.Single(a.Decisoes);
        Assert.True(a.Decisoes[0].Citacoes[0].Conferida);
        Assert.Equal(TimeSpan.FromSeconds(30), a.Decisoes[0].Citacoes[0].Inicio);
        Assert.Equal(TimeSpan.FromSeconds(12), a.PassosParaReproduzir[0].Citacoes[0].Inicio); // tempo corrigido
        Assert.Empty(a.DuvidasEmAberto[0].Citacoes);
        Assert.False(a.Cards[0].Citacoes[0].Conferida); // citação inventada
        Assert.Equal(3, a.TodasAsCitacoes.Count()); // a dúvida não tem citação

        var corpo = JsonNode.Parse(handler.UltimoCorpo!)!;
        Assert.Equal("json_schema", (string?)corpo["response_format"]!["type"]);
        Assert.True((bool)corpo["response_format"]!["json_schema"]!["strict"]!);
        Assert.True((bool)corpo["provider"]!["require_parameters"]!);
        Assert.Contains("[00:12] fala: Quando eu clico em Salvar", (string?)corpo["messages"]![1]!["content"]);
    }

    [Fact]
    public void Schema_estrito_exige_todos_os_campos_e_proibe_extras()
    {
        var schema = RedatorOpenRouter.Schema();

        Assert.Equal(7, schema["required"]!.AsArray().Count);
        Assert.False((bool)schema["additionalProperties"]!);
        var card = schema["properties"]!["cards"]!["items"]!;
        Assert.False((bool)card["additionalProperties"]!);
        Assert.Contains("criterios_aceite", card["required"]!.AsArray().Select(n => (string?)n));
    }

    [Fact]
    public async Task Aceita_json_dentro_de_cerca_de_codigo()
    {
        var conteudo = "```json\n" + JsonSerializer.Serialize(Exemplos.AnaliseValida()) + "\n```";
        var (redator, _) = Criar(() => HandlerFalso.Json(HttpStatusCode.OK, Exemplos.RespostaChat(conteudo)));

        var a = await redator.AnalisarAsync(Exemplos.Transcricao());

        Assert.Single(a.Cards);
    }

    [Fact]
    public async Task Json_invalido_pede_de_novo_uma_vez()
    {
        var (redator, handler) = Criar(
            () => HandlerFalso.Json(HttpStatusCode.OK, Exemplos.RespostaChat("isto não é json")),
            () => HandlerFalso.Json(HttpStatusCode.OK, Exemplos.RespostaChat(Exemplos.AnaliseValida())));

        var a = await redator.AnalisarAsync(Exemplos.Transcricao());

        Assert.Equal(2, handler.Chamadas);
        Assert.NotEmpty(a.Resumo);
    }

    [Fact]
    public async Task Json_invalido_duas_vezes_gera_erro_tratado()
    {
        var (redator, handler) = Criar(
            () => HandlerFalso.Json(HttpStatusCode.OK, Exemplos.RespostaChat("{\"resumo\": \"\"}")),
            () => HandlerFalso.Json(HttpStatusCode.OK, Exemplos.RespostaChat("[]")));

        var ex = await Assert.ThrowsAsync<MinutaException>(() => redator.AnalisarAsync(Exemplos.Transcricao()));

        Assert.Contains("fora do formato", ex.Message);
        Assert.Equal(2, handler.Chamadas);
    }

    [Fact]
    public async Task Resposta_cortada_por_tamanho_e_tratada()
    {
        var (redator, _) = Criar(
            () => HandlerFalso.Json(HttpStatusCode.OK, Exemplos.RespostaChat("{\"resumo\":", "length")),
            () => HandlerFalso.Json(HttpStatusCode.OK, Exemplos.RespostaChat("{\"resumo\":", "length")));

        var ex = await Assert.ThrowsAsync<MinutaException>(() => redator.AnalisarAsync(Exemplos.Transcricao()));

        Assert.Contains("cortada", ex.Message);
    }

    [Fact]
    public async Task Modelo_sem_provedor_explica_privacidade()
    {
        var (redator, _) = Criar(
            () => HandlerFalso.Json(HttpStatusCode.NotFound, """{"error":{"code":404,"message":"No endpoints found matching your data policy"}}"""));

        var ex = await Assert.ThrowsAsync<MinutaException>(() => redator.AnalisarAsync(Exemplos.Transcricao()));

        Assert.Contains("data policy", ex.Message);
        Assert.Contains("privacy", ex.Dica);
    }

    [Fact]
    public async Task Erro_dentro_de_resposta_200_e_tratado()
    {
        var (redator, _) = Criar(
            () => HandlerFalso.Json(HttpStatusCode.OK, """{"error":{"message":"Provider returned error"}}"""));

        var ex = await Assert.ThrowsAsync<MinutaException>(() => redator.AnalisarAsync(Exemplos.Transcricao()));

        Assert.Contains("Provider returned error", ex.Message);
    }

    [Fact]
    public async Task Limite_429_tenta_de_novo()
    {
        var (redator, handler) = Criar(
            () => HandlerFalso.Json(HttpStatusCode.TooManyRequests, """{"error":{"message":"Rate limit exceeded"}}"""),
            () => HandlerFalso.Json(HttpStatusCode.OK, Exemplos.RespostaChat(Exemplos.AnaliseValida())));

        await redator.AnalisarAsync(Exemplos.Transcricao());

        Assert.Equal(2, handler.Chamadas);
    }
}

public class ArquivoAnaliseTests
{
    [Fact]
    public async Task Reaproveita_so_com_mesmo_video_modelo_e_versao_do_prompt()
    {
        using var temp = new PastaTemporaria();
        var video = new FileInfo(temp.CriarArquivo("reuniao.mp4", "conteúdo"));
        var transcricao = Exemplos.Transcricao() with { Origem = OrigemTranscricao.De(video) };
        var handler = new HandlerFalso(() => HandlerFalso.Json(HttpStatusCode.OK, Exemplos.RespostaChat(Exemplos.AnaliseValida())));
        var analise = await new RedatorOpenRouter(new HttpClient(handler), "k", "m:free").AnalisarAsync(transcricao);
        var caminho = Path.Combine(temp.Caminho, "reuniao" + ArquivoAnalise.Sufixo);

        ArquivoAnalise.Salvar(analise, caminho);

        var lida = ArquivoAnalise.Carregar(caminho, video, "m:free", RedatorOpenRouter.VersaoPrompt);
        Assert.NotNull(lida);
        Assert.Equal(analise.Cards[0].Citacoes, lida.Cards[0].Citacoes);
        Assert.Null(ArquivoAnalise.Carregar(caminho, video, "outro:free", RedatorOpenRouter.VersaoPrompt));
        Assert.Null(ArquivoAnalise.Carregar(caminho, video, "m:free", RedatorOpenRouter.VersaoPrompt + 1));
    }
}
