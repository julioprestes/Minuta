using System.Text.Json;
using System.Text.Json.Serialization;

namespace Minuta.Core.Http;

/// <summary>
/// Envia uma requisição HTTP e tenta de novo em erros temporários (rede, timeout, 408, 429, 5xx),
/// até 3 vezes, respeitando o Retry-After. Usado pelos clientes do Groq e do OpenRouter.
/// </summary>
internal sealed class ChamadorComTentativas(HttpClient http, string servico)
{
    public const int MaxTentativas = 3;

    /// <summary>Espera máxima aceita de um Retry-After; acima disso costuma ser limite diário, não vale esperar.</summary>
    public static readonly TimeSpan EsperaMaxima = TimeSpan.FromSeconds(60);

    public Func<int, TimeSpan> EsperaEntreTentativas { get; init; } = tentativa => TimeSpan.FromSeconds(3 * tentativa);

    /// <param name="montar">Cria a requisição (chamado a cada tentativa: o conteúdo não pode ser reenviado).</param>
    /// <param name="erroHttp">Converte um status de erro definitivo em mensagem para o usuário.</param>
    /// <returns>O corpo da resposta de sucesso.</returns>
    public async Task<string> EnviarAsync(
        Func<HttpRequestMessage> montar,
        Func<FalhaHttp, MinutaException> erroHttp,
        CancellationToken cancelamento)
    {
        for (var tentativa = 1; ; tentativa++)
        {
            var ultima = tentativa == MaxTentativas;
            HttpResponseMessage resposta;
            try
            {
                using var requisicao = montar();
                resposta = await http.SendAsync(requisicao, cancelamento);
            }
            catch (HttpRequestException ex)
            {
                if (ultima)
                    throw new MinutaException($"Falha de rede ao chamar o {servico}: {ex.Message}",
                        "Confira a conexão com a internet (ou proxy/firewall) e tente de novo.", ex);
                await Task.Delay(EsperaEntreTentativas(tentativa), cancelamento);
                continue;
            }
            catch (TaskCanceledException ex) when (!cancelamento.IsCancellationRequested)
            {
                if (ultima)
                    throw new MinutaException($"O {servico} demorou demais para responder.",
                        "Tente de novo em alguns minutos.", ex);
                await Task.Delay(EsperaEntreTentativas(tentativa), cancelamento);
                continue;
            }

            using (resposta)
            {
                var corpo = await resposta.Content.ReadAsStringAsync(cancelamento);
                if (resposta.IsSuccessStatusCode)
                    return corpo;

                var status = (int)resposta.StatusCode;
                var pedida = EsperaPedida(resposta);
                var temporario = status is 408 or 429 or 498 || status >= 500;
                var espera = pedida ?? EsperaEntreTentativas(tentativa);

                if (!temporario || ultima || espera > EsperaMaxima)
                    throw erroHttp(new FalhaHttp(status, MensagemDaApi(corpo), pedida));

                await Task.Delay(espera, cancelamento);
            }
        }
    }

    private static TimeSpan? EsperaPedida(HttpResponseMessage resposta) =>
        resposta.Headers.RetryAfter switch
        {
            { Delta: { } delta } => delta,
            { Date: { } data } => data - DateTimeOffset.UtcNow,
            _ => null,
        };

    /// <summary>Groq e OpenRouter usam o mesmo formato de erro: {"error": {"message": "..."}}.</summary>
    internal static string? MensagemDaApi(string corpo)
    {
        try
        {
            return JsonSerializer.Deserialize<RespostaErro>(corpo)?.Error?.Message;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record RespostaErro([property: JsonPropertyName("error")] DetalheErro? Error);

    private sealed record DetalheErro([property: JsonPropertyName("message")] string? Message);
}

/// <summary>Erro HTTP definitivo: status, mensagem da API (se houver) e quanto o servidor pediu para esperar.</summary>
internal sealed record FalhaHttp(int Status, string? MensagemApi, TimeSpan? EsperaPedida)
{
    public string Detalhe => MensagemApi is null ? "" : $" Detalhe: {MensagemApi}";
}
