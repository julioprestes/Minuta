using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Minuta.Core.Http;
using Minuta.Core.Transcricoes;

namespace Minuta.Core.Analises;

/// <summary>
/// Envia a transcrição a um modelo do OpenRouter e pede a análise em JSON validado por schema (modo estrito).
/// As citações devolvidas são conferidas contra a transcrição antes de virar <see cref="AnaliseVideo"/>.
/// </summary>
public sealed class RedatorOpenRouter(HttpClient http, string apiKey, string modelo)
{
    public const string Endereco = "https://openrouter.ai/api/v1/chat/completions";

    /// <summary>Aumente ao mudar o prompt ou o schema: invalida as análises salvas.</summary>
    public const int VersaoPrompt = 1;

    /// <summary>Respostas fora do formato são pedidas de novo uma vez.</summary>
    private const int TentativasDeFormato = 2;

    internal Func<int, TimeSpan> EsperaEntreTentativas { get; init; } = tentativa => TimeSpan.FromSeconds(3 * tentativa);

    internal const string InstrucoesDoSistema = """
        Você analisa a transcrição de um vídeo em que uma pessoa explica um fluxo de sistema, relata um erro
        ou conduz uma reunião. O resultado será lido por uma equipe de desenvolvimento.

        Regras obrigatórias:
        1. Use somente o que está na transcrição. Não deduza, não complete e não invente nomes, telas, campos,
           números, mensagens ou regras.
        2. Cada decisão, regra de negócio, passo, próximo passo e card precisa de pelo menos uma citação:
           "trecho" é uma cópia LITERAL de palavras consecutivas da transcrição (de 5 a 30 palavras, sem resumir
           nem corrigir) e "tempo" é o [mm:ss] da linha de onde o trecho saiu.
        3. O que ficou incerto, contraditório ou não foi dito, mas seria preciso saber para implementar, vira dúvida
           em aberto. Cite o trecho que levantou a dúvida, se houver; se não houver, deixe "citacoes" vazio.
        4. Seção sem conteúdo no vídeo fica com lista vazia. Não preencha por preencher.
        5. Escreva em português do Brasil, com frases curtas e objetivas.

        Definições:
        - decisao: algo que foi definido ou combinado ("ficou decidido", "vamos manter").
        - regra_negocio: como o sistema deve se comportar, escrita como "O sistema deve ...".
        - passo_para_reproduzir: só quando o vídeo mostra ou relata um erro; um passo por item, na ordem em que acontece.
        - proximo_passo: ação que alguém disse que vai fazer ou que precisa ser feita (inclua quem e quando, se ditos).
        - card: tarefa concreta de desenvolvimento que decorre do vídeo, com critérios de aceite verificáveis tirados
          do que foi dito.
        Linhas terminadas em "(?)" tiveram baixa confiança na transcrição: use com cuidado.
        """;

    public async Task<AnaliseVideo> AnalisarAsync(Transcricao transcricao, CancellationToken cancelamento = default)
    {
        var chamador = new ChamadorComTentativas(http, "OpenRouter") { EsperaEntreTentativas = EsperaEntreTentativas };
        var corpoRequisicao = MontarCorpo(transcricao);

        for (var tentativa = 1; ; tentativa++)
        {
            var resposta = await chamador.EnviarAsync(() => MontarRequisicao(corpoRequisicao), Erro, cancelamento);
            try
            {
                return Interpretar(resposta, transcricao) with { Modelo = modelo, VersaoPrompt = VersaoPrompt };
            }
            catch (RespostaForaDoFormato) when (tentativa < TentativasDeFormato)
            {
                // O modelo devolveu algo fora do schema; pede de novo uma vez.
            }
            catch (RespostaForaDoFormato ex)
            {
                throw new MinutaException($"O modelo {modelo} devolveu uma análise fora do formato esperado: {ex.Message}",
                    "Tente de novo ou troque OPENROUTER_MODELO_ANALISE por um modelo que aceite saída estruturada.", ex);
            }
        }
    }

    private string MontarCorpo(Transcricao transcricao)
    {
        var usuario = $"""
            Transcrição do vídeo (duração {(transcricao.Duracao is { } d ? Transcricao.FormatarTempo(d) : "desconhecida")}),
            uma linha por trecho, no formato [mm:ss] texto:

            {transcricao.ParaTextoComTempos()}
            """;

        var corpo = new JsonObject
        {
            ["model"] = modelo,
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = InstrucoesDoSistema },
                new JsonObject { ["role"] = "user", ["content"] = usuario }),
            ["response_format"] = new JsonObject
            {
                ["type"] = "json_schema",
                ["json_schema"] = new JsonObject
                {
                    ["name"] = "analise_video",
                    ["strict"] = true,
                    ["schema"] = Schema(),
                },
            },
            // Só usa provedores que respeitam o response_format.
            ["provider"] = new JsonObject { ["require_parameters"] = true },
            ["temperature"] = 0,
            ["max_tokens"] = 20000,
            ["reasoning"] = new JsonObject { ["exclude"] = true },
        };
        return corpo.ToJsonString();
    }

    private HttpRequestMessage MontarRequisicao(string corpo)
    {
        var requisicao = new HttpRequestMessage(HttpMethod.Post, Endereco)
        {
            Content = new StringContent(corpo, Encoding.UTF8, "application/json"),
        };
        requisicao.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        requisicao.Headers.Add("X-Title", "Minuta");
        return requisicao;
    }

    /// <summary>Schema estrito. Sem $ref: nem todo provedor aceita, então cada objeto é escrito por extenso.</summary>
    internal static JsonObject Schema()
    {
        static JsonObject Texto(string descricao) => new() { ["type"] = "string", ["description"] = descricao };

        static JsonObject Citacoes() => new()
        {
            ["type"] = "array",
            ["items"] = Objeto(new()
            {
                ["tempo"] = Texto("Tempo da linha citada, no formato mm:ss."),
                ["trecho"] = Texto("Cópia literal de 5 a 30 palavras consecutivas da transcrição."),
            }),
        };

        static JsonObject Lista(string descricao) => new()
        {
            ["type"] = "array",
            ["description"] = descricao,
            ["items"] = Objeto(new() { ["texto"] = Texto("O item, em uma frase."), ["citacoes"] = Citacoes() }),
        };

        static JsonObject Objeto(JsonObject propriedades) => new()
        {
            ["type"] = "object",
            ["properties"] = propriedades,
            ["required"] = new JsonArray(propriedades.Select(p => (JsonNode)JsonValue.Create(p.Key)!).ToArray()),
            ["additionalProperties"] = false,
        };

        return Objeto(new()
        {
            ["resumo"] = Texto("Resumo de 2 a 5 frases do que o vídeo trata e do que se espera."),
            ["decisoes"] = Lista("Decisões tomadas ou combinadas."),
            ["regras_negocio"] = Lista("Regras de comportamento do sistema, como \"O sistema deve ...\"."),
            ["duvidas_em_aberto"] = Lista("O que não foi dito ou ficou incerto e precisa ser confirmado."),
            ["passos_para_reproduzir"] = Lista("Passos de um erro mostrado ou relatado, em ordem. Vazio se não houver erro."),
            ["proximos_passos"] = Lista("Ações a fazer, com responsável e prazo se ditos."),
            ["cards"] = new JsonObject
            {
                ["type"] = "array",
                ["description"] = "Tarefas de desenvolvimento que decorrem do vídeo.",
                ["items"] = Objeto(new()
                {
                    ["titulo"] = Texto("Título curto da tarefa, começando com verbo."),
                    ["descricao"] = Texto("O que precisa ser feito e por quê."),
                    ["criterios_aceite"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["items"] = Texto("Critério verificável tirado do que foi dito."),
                    },
                    ["citacoes"] = Citacoes(),
                }),
            },
        });
    }

    private static MinutaException Erro(FalhaHttp falha) => falha.Status switch
    {
        400 => new("O OpenRouter recusou a requisição." + falha.Detalhe,
            "Confira se OPENROUTER_MODELO_ANALISE aceita saída estruturada (response_format / JSON Schema)."),
        401 => new("A chave do OpenRouter foi recusada (inválida ou expirada).",
            "Gere outra em https://openrouter.ai/settings/keys e atualize OPENROUTER_API_KEY no .env."),
        402 => new("O OpenRouter recusou por saldo." + falha.Detalhe,
            "Com saldo negativo, até os modelos gratuitos falham. Confira em https://openrouter.ai/settings/credits."),
        403 => new("O OpenRouter negou a requisição." + falha.Detalhe),
        404 => new("Modelo não encontrado ou sem provedor disponível no OpenRouter." + falha.Detalhe,
            "Confira OPENROUTER_MODELO_ANALISE (a lista de modelos gratuitos muda) e a privacidade em "
            + "https://openrouter.ai/settings/privacy: modelos gratuitos podem exigir permitir provedores que registram os dados."),
        429 => new("Limite de uso do OpenRouter atingido." + falha.Detalhe,
            (falha.EsperaPedida is { } e ? $"O OpenRouter pede para esperar {e.TotalMinutes:N0} min. " : "")
            + "Modelos gratuitos: 20 por minuto e 50 por dia sem créditos comprados."),
        >= 500 => new($"O OpenRouter ou o provedor do modelo está indisponível (HTTP {falha.Status}).",
            "Tente de novo mais tarde ou troque de modelo."),
        _ => new($"O OpenRouter recusou a requisição (HTTP {falha.Status})." + falha.Detalhe),
    };

    /// <summary>Lê a resposta do chat completions, valida o JSON da análise e confere as citações.</summary>
    internal static AnaliseVideo Interpretar(string corpo, Transcricao transcricao)
    {
        string conteudo;
        try
        {
            using var doc = JsonDocument.Parse(corpo);
            var raiz = doc.RootElement;

            if (raiz.TryGetProperty("error", out var erro))
                throw new MinutaException("O OpenRouter devolveu um erro: "
                    + (erro.TryGetProperty("message", out var m) ? m.GetString() : erro.ToString()));

            var escolha = raiz.GetProperty("choices")[0];
            if (escolha.TryGetProperty("finish_reason", out var fim) && fim.GetString() == "length")
                throw new RespostaForaDoFormato("a resposta foi cortada por tamanho");

            conteudo = escolha.GetProperty("message").GetProperty("content").GetString() ?? "";
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or IndexOutOfRangeException or InvalidOperationException)
        {
            throw new RespostaForaDoFormato("a resposta do OpenRouter não tem o formato de chat completions", ex);
        }

        AnaliseBruta? bruta;
        try
        {
            bruta = JsonSerializer.Deserialize<AnaliseBruta>(SemCercaDeCodigo(conteudo));
        }
        catch (JsonException ex)
        {
            throw new RespostaForaDoFormato("o conteúdo não é um JSON válido", ex);
        }

        if (bruta is null || string.IsNullOrWhiteSpace(bruta.Resumo))
            throw new RespostaForaDoFormato("a análise veio sem resumo");

        var conferencia = new ConferenciaCitacoes(transcricao);
        IReadOnlyList<Citacao> Conferir(List<CitacaoBruta>? citacoes) =>
            (citacoes ?? []).Where(c => !string.IsNullOrWhiteSpace(c.Trecho))
                .Select(c => conferencia.Conferir(c.Trecho!, c.Tempo)).ToList();

        IReadOnlyList<ItemCitado> Itens(List<ItemBruto>? itens) =>
            (itens ?? []).Where(i => !string.IsNullOrWhiteSpace(i.Texto))
                .Select(i => new ItemCitado(i.Texto!.Trim(), Conferir(i.Citacoes))).ToList();

        return new AnaliseVideo(
            bruta.Resumo.Trim(),
            Itens(bruta.Decisoes),
            Itens(bruta.RegrasNegocio),
            Itens(bruta.DuvidasEmAberto),
            Itens(bruta.PassosParaReproduzir),
            Itens(bruta.ProximosPassos),
            (bruta.Cards ?? []).Where(c => !string.IsNullOrWhiteSpace(c.Titulo))
                .Select(c => new CardTarefa(
                    c.Titulo!.Trim(),
                    c.Descricao?.Trim() ?? "",
                    (c.CriteriosAceite ?? []).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).ToList(),
                    Conferir(c.Citacoes)))
                .ToList())
        {
            Origem = transcricao.Origem,
        };
    }

    /// <summary>Alguns modelos embrulham o JSON em ```json ... ``` mesmo com schema.</summary>
    private static string SemCercaDeCodigo(string conteudo)
    {
        var texto = conteudo.Trim();
        if (!texto.StartsWith("```"))
            return texto;
        var inicio = texto.IndexOf('\n');
        var fim = texto.LastIndexOf("```", StringComparison.Ordinal);
        return inicio >= 0 && fim > inicio ? texto[(inicio + 1)..fim].Trim() : texto;
    }

    internal sealed class RespostaForaDoFormato(string mensagem, Exception? causa = null) : Exception(mensagem, causa);

    private sealed record AnaliseBruta(
        [property: JsonPropertyName("resumo")] string? Resumo,
        [property: JsonPropertyName("decisoes")] List<ItemBruto>? Decisoes,
        [property: JsonPropertyName("regras_negocio")] List<ItemBruto>? RegrasNegocio,
        [property: JsonPropertyName("duvidas_em_aberto")] List<ItemBruto>? DuvidasEmAberto,
        [property: JsonPropertyName("passos_para_reproduzir")] List<ItemBruto>? PassosParaReproduzir,
        [property: JsonPropertyName("proximos_passos")] List<ItemBruto>? ProximosPassos,
        [property: JsonPropertyName("cards")] List<CardBruto>? Cards);

    private sealed record ItemBruto(
        [property: JsonPropertyName("texto")] string? Texto,
        [property: JsonPropertyName("citacoes")] List<CitacaoBruta>? Citacoes);

    private sealed record CardBruto(
        [property: JsonPropertyName("titulo")] string? Titulo,
        [property: JsonPropertyName("descricao")] string? Descricao,
        [property: JsonPropertyName("criterios_aceite")] List<string>? CriteriosAceite,
        [property: JsonPropertyName("citacoes")] List<CitacaoBruta>? Citacoes);

    private sealed record CitacaoBruta(
        [property: JsonPropertyName("tempo")] string? Tempo,
        [property: JsonPropertyName("trecho")] string? Trecho);
}
