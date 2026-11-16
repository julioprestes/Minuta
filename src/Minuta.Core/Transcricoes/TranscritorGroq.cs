using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Minuta.Core.Http;

namespace Minuta.Core.Transcricoes;

/// <summary>
/// Transcreve o áudio com o Whisper hospedado no Groq (API compatível com a da OpenAI).
/// Pede "verbose_json" para receber cada trecho com início e fim.
/// Erros temporários (rede, 429, 5xx) são tentados de novo até 3 vezes.
/// </summary>
/// <param name="prompt">Texto do glossário, para o Whisper acertar a grafia de termos. Opcional.</param>
public sealed class TranscritorGroq(HttpClient http, string apiKey, string modelo, string idioma, string? prompt = null)
{
    public const string Endereco = "https://api.groq.com/openai/v1/audio/transcriptions";

    private static readonly Regex FrasesColadas = new(@"([.!?])(?=\p{Lu})");

    /// <summary>Espera entre tentativas quando o servidor não diz quanto esperar. Ajustável nos testes.</summary>
    internal Func<int, TimeSpan> EsperaEntreTentativas { get; init; } = tentativa => TimeSpan.FromSeconds(3 * tentativa);

    /// <param name="caminhoRespostaBruta">
    /// Se informado, grava a resposta do Groq como chegou, antes de interpretar (mesmo que seja inválida).
    /// Serve para diagnosticar erros de transcrição: mostra se o texto já veio errado da API.
    /// </param>
    public async Task<Transcricao> TranscreverAsync(
        string caminhoAudio, string? caminhoRespostaBruta = null, CancellationToken cancelamento = default)
    {
        var chamador = new ChamadorComTentativas(http, "Groq") { EsperaEntreTentativas = EsperaEntreTentativas };
        var corpo = await chamador.EnviarAsync(() => MontarRequisicao(caminhoAudio), Erro, cancelamento);
        if (caminhoRespostaBruta is not null)
            await File.WriteAllTextAsync(caminhoRespostaBruta, corpo, new System.Text.UTF8Encoding(false), cancelamento);
        return Interpretar(corpo, modelo, idioma) with { Glossario = prompt };
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
        if (prompt is not null)
            formulario.Add(new StringContent(prompt), "prompt");

        var requisicao = new HttpRequestMessage(HttpMethod.Post, Endereco) { Content = formulario };
        requisicao.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return requisicao;
    }

    private static MinutaException Erro(FalhaHttp falha) => falha.Status switch
    {
        401 => new("A chave do Groq foi recusada (inválida ou expirada).",
            "Gere outra em https://console.groq.com/keys e atualize GROQ_API_KEY no .env."),
        403 => new("O Groq negou o acesso." + falha.Detalhe,
            "Confira as permissões da chave e do projeto no console do Groq."),
        404 => new("Modelo ou endereço não encontrado no Groq." + falha.Detalhe,
            "Confira GROQ_MODELO_TRANSCRICAO no .env (ex.: whisper-large-v3)."),
        413 => new("O áudio é grande demais para o Groq." + falha.Detalhe,
            "Corte o vídeo em partes menores."),
        429 => new("Limite de uso do Groq atingido." + falha.Detalhe,
            (falha.EsperaPedida is { } e ? $"O Groq pede para esperar {e.TotalMinutes:N0} min. " : "")
            + "Confira seus limites em https://console.groq.com/settings/limits."),
        >= 500 or 498 => new($"O Groq está indisponível no momento (HTTP {falha.Status}).",
            "Tente de novo mais tarde. Requisições com erro 5xx não são cobradas."),
        _ => new($"O Groq recusou a requisição (HTTP {falha.Status})." + falha.Detalhe),
    };

    /// <summary>
    /// Converte o verbose_json em <see cref="Transcricao"/>. Aplica o mesmo critério do Whisper original para
    /// descartar silêncio (no_speech_prob &gt; 0,6 e avg_logprob &lt; -1), o que evita frases inventadas em
    /// trechos sem fala, e marca baixa confiança quando avg_logprob &lt; -1 ou compression_ratio &gt; 2,4.
    /// Também descarta trechos que são quase só uma frase curta repetida (alucinação com ruído) e marca
    /// como baixa confiança os que têm uma repetição dessas no meio de fala real.
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
            var repeticao = CoberturaDeRepeticao(texto);
            if (texto.Length == 0 || silencio || repeticao >= CoberturaParaDescartar)
            {
                descartados++;
                continue;
            }

            var baixaConfianca = s.AvgLogprob < -1.0 || s.CompressionRatio > 2.4 || repeticao > 0;
            segmentos.Add(new SegmentoTranscricao(
                TimeSpan.FromSeconds(s.Start), TimeSpan.FromSeconds(s.End), texto, baixaConfianca));
        }

        return new Transcricao(modelo, idioma, duracao, segmentos, descartados);
    }

    /// <summary>Mínimo de repetições seguidas de uma mesma frase curta para contar como alucinação.</summary>
    internal const int RepeticoesMinimas = 4;

    /// <summary>Trecho em que a repetição cobre ao menos esta fração das palavras é descartado como ruído.</summary>
    internal const double CoberturaParaDescartar = 0.8;

    private const int PalavrasMaximasDaFrase = 4;

    /// <summary>
    /// Com ruído sem fala (teclado, cliques), o Whisper às vezes repete uma frase curta ("E aí E aí E aí...").
    /// Procura a maior sequência de uma frase de 1 a 4 palavras repetida ao menos <see cref="RepeticoesMinimas"/>
    /// vezes seguidas e devolve a fração das palavras do trecho que ela ocupa (0 = sem repetição suspeita).
    /// Acentos, maiúsculas e pontuação são ignorados.
    /// </summary>
    internal static double CoberturaDeRepeticao(string texto)
    {
        var palavras = Analises.ConferenciaCitacoes.Normalizar(texto).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var maior = 0;

        for (var n = 1; n <= PalavrasMaximasDaFrase; n++)
        {
            for (var inicio = 0; inicio + n * RepeticoesMinimas <= palavras.Length; inicio++)
            {
                var repeticoes = 1;
                while (inicio + (repeticoes + 1) * n <= palavras.Length
                       && palavras.AsSpan(inicio, n).SequenceEqual(palavras.AsSpan(inicio + repeticoes * n, n)))
                    repeticoes++;

                if (repeticoes >= RepeticoesMinimas)
                    maior = Math.Max(maior, repeticoes * n);
            }
        }

        return palavras.Length == 0 ? 0 : (double)maior / palavras.Length;
    }

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
}
