using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Minuta.Core.Http;

/// <summary>Partes comuns das chamadas ao chat completions do OpenRouter (análise e descrição dos frames).</summary>
internal static class OpenRouterChat
{
    public const string Endereco = "https://openrouter.ai/api/v1/chat/completions";

    public static HttpRequestMessage Requisicao(string apiKey, string corpo)
    {
        var requisicao = new HttpRequestMessage(HttpMethod.Post, Endereco)
        {
            Content = new StringContent(corpo, Encoding.UTF8, "application/json"),
        };
        requisicao.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        requisicao.Headers.Add("X-Title", "Minuta");
        return requisicao;
    }

    /// <summary>Corpo com saída estruturada estrita, só em provedores que a respeitam, sem raciocínio na resposta.</summary>
    public static JsonObject Corpo(string modelo, JsonNode instrucoes, JsonNode conteudoUsuario, string nomeSchema, JsonObject schema, int maxTokens) =>
        new()
        {
            ["model"] = modelo,
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = instrucoes },
                new JsonObject { ["role"] = "user", ["content"] = conteudoUsuario }),
            ["response_format"] = new JsonObject
            {
                ["type"] = "json_schema",
                ["json_schema"] = new JsonObject { ["name"] = nomeSchema, ["strict"] = true, ["schema"] = schema },
            },
            ["provider"] = new JsonObject { ["require_parameters"] = true },
            ["temperature"] = 0,
            ["max_tokens"] = maxTokens,
            ["reasoning"] = new JsonObject { ["exclude"] = true },
        };

    /// <param name="variavelModelo">Variável do .env com o modelo, citada nas dicas.</param>
    public static MinutaException Erro(FalhaHttp falha, string variavelModelo) => falha.Status switch
    {
        400 => new("O OpenRouter recusou a requisição." + falha.Detalhe,
            $"Confira se {variavelModelo} aceita saída estruturada (response_format / JSON Schema)."),
        401 => new("A chave do OpenRouter foi recusada (inválida ou expirada).",
            "Gere outra em https://openrouter.ai/settings/keys e atualize OPENROUTER_API_KEY no .env."),
        402 => new("O OpenRouter recusou por saldo." + falha.Detalhe,
            "Com saldo negativo, até os modelos gratuitos falham. Confira em https://openrouter.ai/settings/credits."),
        403 => new("O OpenRouter negou a requisição." + falha.Detalhe,
            $"Alguns modelos gratuitos só funcionam em ferramentas específicas; troque {variavelModelo}."),
        404 => new("Modelo não encontrado ou sem provedor disponível no OpenRouter." + falha.Detalhe,
            $"Confira {variavelModelo} (a lista de modelos gratuitos muda) e a privacidade em "
            + "https://openrouter.ai/settings/privacy: modelos gratuitos podem exigir permitir provedores que registram os dados."),
        429 => new("Limite de uso do OpenRouter atingido." + falha.Detalhe,
            (falha.EsperaPedida is { } e ? $"O OpenRouter pede para esperar {e.TotalMinutes:N0} min. " : "")
            + "Modelos gratuitos: 20 por minuto e 50 por dia sem créditos comprados."),
        >= 500 => new($"O OpenRouter ou o provedor do modelo está indisponível (HTTP {falha.Status}).",
            "Tente de novo mais tarde ou troque de modelo."),
        _ => new($"O OpenRouter recusou a requisição (HTTP {falha.Status})." + falha.Detalhe),
    };

    /// <summary>Extrai o texto da resposta (sem cerca de código). Erro dentro de um 200 vira exceção clara.</summary>
    public static string LerConteudo(string corpo)
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
        return SemCercaDeCodigo(conteudo);
    }

    /// <summary>Alguns modelos embrulham o JSON em ```json ... ``` mesmo com schema.</summary>
    public static string SemCercaDeCodigo(string conteudo)
    {
        var texto = conteudo.Trim();
        if (!texto.StartsWith("```"))
            return texto;
        var inicio = texto.IndexOf('\n');
        var fim = texto.LastIndexOf("```", StringComparison.Ordinal);
        return inicio >= 0 && fim > inicio ? texto[(inicio + 1)..fim].Trim() : texto;
    }

    // Ajudantes do JSON Schema estrito. Sem $ref: nem todo provedor aceita, então cada objeto é escrito por extenso.
    public static JsonObject Texto(string descricao) => new() { ["type"] = "string", ["description"] = descricao };

    public static JsonObject ListaDeTextos(string descricao) =>
        new() { ["type"] = "array", ["description"] = descricao, ["items"] = new JsonObject { ["type"] = "string" } };

    public static JsonObject Objeto(JsonObject propriedades) => new()
    {
        ["type"] = "object",
        ["properties"] = propriedades,
        ["required"] = new JsonArray(propriedades.Select(p => (JsonNode)JsonValue.Create(p.Key)!).ToArray()),
        ["additionalProperties"] = false,
    };
}

/// <summary>O modelo respondeu, mas fora do formato pedido; vale pedir de novo.</summary>
internal sealed class RespostaForaDoFormato(string mensagem, Exception? causa = null) : Exception(mensagem, causa);
