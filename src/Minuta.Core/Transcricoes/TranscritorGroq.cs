using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Minuta.Core.Transcricoes;

/// <summary>
/// Transcreve o áudio com o Whisper hospedado no Groq (API compatível com a da OpenAI).
/// Pede "verbose_json" para receber cada trecho com início e fim.
/// Erros temporários (rede, 429, 5xx) são tentados de novo até 3 vezes.
/// </summary>
public sealed class TranscritorGroq(HttpClient http, string apiKey, string modelo, string idioma)
{
    public const string Endereco = "https://api.groq.com/openai/v1/audio/transcriptions";
    private const int MaxTentativas = 3;

    /// <summary>Espera máxima que aceitamos de um Retry-After; acima disso é limite diário, não vale esperar.</summary>
    private static readonly TimeSpan EsperaMaxima = TimeSpan.FromSeconds(60);

    /// <summary>Espera entre tentativas quando o servidor não diz quanto esperar. Ajustável nos testes.</summary>
    internal Func<int, TimeSpan> EsperaEntreTentativas { get; init; } = tentativa => TimeSpan.FromSeconds(3 * tentativa);

    public async Task<Transcricao> TranscreverAsync(string caminhoAudio, CancellationToken cancelamento = default)
    {
        for (var tentativa = 1; ; tentativa++)
        {
            var ultima = tentativa == MaxTentativas;
            HttpResponseMessage resposta;
            try
            {
                using var requisicao = MontarRequisicao(caminhoAudio);
                resposta = await http.SendAsync(requisicao, cancelamento);
            }
            catch (HttpRequestException ex)
            {
                if (ultima)
                    throw new MinutaException($"Falha de rede ao chamar o Groq: {ex.Message}",
                        "Confira a conexão com a internet (ou proxy/firewall) e tente de novo.", ex);
                await Task.Delay(EsperaEntreTentativas(tentativa), cancelamento);
                continue;
            }
            catch (TaskCanceledException ex) when (!cancelamento.IsCancellationRequested)
            {
                if (ultima)
                    throw new MinutaException("O Groq demorou demais para responder.",
                        "Tente de novo em alguns minutos.", ex);
                await Task.Delay(EsperaEntreTentativas(tentativa), cancelamento);
                continue;
            }

            using (resposta)
            {
                var corpo = await resposta.Content.ReadAsStringAsync(cancelamento);
                if (resposta.IsSuccessStatusCode)
                    return Interpretar(corpo, modelo, idioma);

                var status = (int)resposta.StatusCode;
                var temporario = status is 429 or 498 || status >= 500;
                var espera = EsperaPedida(resposta) ?? EsperaEntreTentativas(tentativa);

                if (!temporario || ultima || espera > EsperaMaxima)
                    throw Erro(status, MensagemDaApi(corpo), EsperaPedida(resposta));

                await Task.Delay(espera, cancelamento);
            }
        }
    }

    private HttpRequestMessage MontarRequisicao(string caminhoAudio)
    {
        var arquivo = new StreamContent(File.OpenRead(caminhoAudio));
        arquivo.Headers.ContentType = new MediaTypeHeaderValue("audio/ogg");

        var formulario = new MultipartFormDataContent
        {
            { arquivo, "file", Path.GetFileName(caminhoAudio) },
            { new StringContent(modelo), "model" },
            { new StringContent(idioma), "language" },
            { new StringContent("verbose_json"), "response_format" },
            { new StringContent("segment"), "timestamp_granularities[]" },
            { new StringContent("0"), "temperature" },
        };

        var requisicao = new HttpRequestMessage(HttpMethod.Post, Endereco) { Content = formulario };
        requisicao.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return requisicao;
    }

    private static TimeSpan? EsperaPedida(HttpResponseMessage resposta) =>
        resposta.Headers.RetryAfter switch
        {
            { Delta: { } delta } => delta,
            { Date: { } data } => data - DateTimeOffset.UtcNow,
            _ => null,
        };

    private static MinutaException Erro(int status, string? mensagemApi, TimeSpan? espera)
    {
        var detalhe = mensagemApi is null ? "" : $" Detalhe: {mensagemApi}";
        return status switch
        {
            401 => new("A chave do Groq foi recusada (inválida ou expirada).",
                "Gere outra em https://console.groq.com/keys e atualize GROQ_API_KEY no .env."),
            403 => new("O Groq negou o acesso." + detalhe,
                "Confira as permissões da chave e do projeto no console do Groq."),
            404 => new("Modelo ou endereço não encontrado no Groq." + detalhe,
                "Confira GROQ_MODELO_TRANSCRICAO no .env (ex.: whisper-large-v3)."),
            413 => new("O áudio é grande demais para o Groq." + detalhe,
                "Corte o vídeo em partes menores."),
            429 => new("Limite de uso do Groq atingido." + detalhe,
                (espera is { } e ? $"O Groq pede para esperar {e.TotalMinutes:N0} min. " : "")
                + "Confira seus limites em https://console.groq.com/settings/limits."),
            >= 500 or 498 => new($"O Groq está indisponível no momento (HTTP {status}).",
                "Tente de novo mais tarde. Requisições com erro 5xx não são cobradas."),
            _ => new($"O Groq recusou a requisição (HTTP {status})." + detalhe),
        };
    }

    private static string? MensagemDaApi(string corpo)
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

    /// <summary>
    /// Converte o verbose_json em <see cref="Transcricao"/>. Aplica o mesmo critério do Whisper original para
    /// descartar silêncio (no_speech_prob &gt; 0,6 e avg_logprob &lt; -1), o que evita frases inventadas em
    /// trechos sem fala, e marca baixa confiança quando avg_logprob &lt; -1 ou compression_ratio &gt; 2,4.
    /// </summary>
    internal static Transcricao Interpretar(string corpo, string modelo, string idioma)
    {
        RespostaGroq? resposta;
        try
        {
            resposta = JsonSerializer.Deserialize<RespostaGroq>(corpo);
        }
        catch (JsonException ex)
        {
            throw new MinutaException("Resposta inválida do Groq (não é o JSON esperado).", causa: ex);
        }

        if (resposta is null || (resposta.Segments is null && resposta.Text is null))
            throw new MinutaException("Resposta inválida do Groq (sem texto nem trechos).");

        var duracao = resposta.Duration is { } d ? TimeSpan.FromSeconds(d) : (TimeSpan?)null;

        // Sem trechos: guarda o texto inteiro como um trecho só, a partir do início.
        if (resposta.Segments is null)
            return new Transcricao(modelo, idioma, duracao,
                [new SegmentoTranscricao(TimeSpan.Zero, duracao ?? TimeSpan.Zero, resposta.Text!.Trim())]);

        var segmentos = new List<SegmentoTranscricao>();
        var descartados = 0;
        foreach (var s in resposta.Segments)
        {
            // O Whisper às vezes cola frases ("estoque.A regra"); devolve o espaço depois da pontuação.
            var texto = FrasesColadas.Replace(s.Text?.Trim() ?? "", "$1 ");
            var silencio = s.NoSpeechProb > 0.6 && s.AvgLogprob < -1.0;
            if (texto.Length == 0 || silencio)
            {
                descartados++;
                continue;
            }

            var baixaConfianca = s.AvgLogprob < -1.0 || s.CompressionRatio > 2.4;
            segmentos.Add(new SegmentoTranscricao(
                TimeSpan.FromSeconds(s.Start), TimeSpan.FromSeconds(s.End), texto, baixaConfianca));
        }

        return new Transcricao(modelo, idioma, duracao, segmentos, descartados);
    }

    private static readonly System.Text.RegularExpressions.Regex FrasesColadas = new(@"([.!?])(?=\p{Lu})");

    private sealed record RespostaGroq(
        [property: JsonPropertyName("text")] string? Text,
        [property: JsonPropertyName("duration")] double? Duration,
        [property: JsonPropertyName("segments")] List<SegmentoGroq>? Segments);

    private sealed record SegmentoGroq(
        [property: JsonPropertyName("start")] double Start,
        [property: JsonPropertyName("end")] double End,
        [property: JsonPropertyName("text")] string? Text,
        [property: JsonPropertyName("avg_logprob")] double AvgLogprob,
        [property: JsonPropertyName("no_speech_prob")] double NoSpeechProb,
        [property: JsonPropertyName("compression_ratio")] double CompressionRatio);

    private sealed record RespostaErro([property: JsonPropertyName("error")] DetalheErro? Error);

    private sealed record DetalheErro([property: JsonPropertyName("message")] string? Message);
}
