using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Minuta.Core.Http;
using Minuta.Core.Midia;
using Minuta.Core.Transcricoes;

namespace Minuta.Core.Visao;

/// <summary>
/// Trilha visual, parte 2: manda os frames a um modelo de visão do OpenRouter, em lotes, e recebe de cada um
/// os textos da tela em JSON validado por schema (modo estrito). Cada lote leva um resumo da última tela do lote
/// anterior, para o modelo saber o que mudou.
/// </summary>
public sealed class DescritorFramesOpenRouter(HttpClient http, string apiKey, string modelo)
{
    /// <summary>Aumente ao mudar o prompt ou o schema: invalida as descrições salvas.</summary>
    public const int VersaoPrompt = 3;

    /// <summary>
    /// Frames por requisição. Cada frame de ~1152×720 custa ~900 tokens de entrada; 10 por lote mantém a resposta
    /// curta o bastante para não ser cortada e gasta 4 requisições (da cota de 50 por dia) num vídeo de 35 frames.
    /// </summary>
    public int FramesPorLote { get; init; } = 10;

    private const int TentativasDeFormato = 2;
    private const string VariavelModelo = "OPENROUTER_MODELO_VISAO";

    internal Func<int, TimeSpan> EsperaEntreTentativas { get; init; } = tentativa => TimeSpan.FromSeconds(3 * tentativa);

    internal const string InstrucoesDoSistema = """
        Você descreve frames de uma gravação de tela de um sistema, na ordem em que aparecem. Quem vai ler não vê as
        imagens: a descrição será usada para localizar telas, menus, campos e mensagens no sistema.

        Regras obrigatórias:
        1. Copie os textos EXATAMENTE como aparecem na tela (títulos, menus, campos, botões, mensagens): sem traduzir,
           corrigir, acentuar ou completar. Se um texto não estiver legível, não invente: escreva [ilegível].
        2. caminho_menu: só quando um menu suspenso ou submenu estiver ABERTO: o caminho do menu principal até o item
           destacado, separado por " > " (ex.: "Vendas > Pedidos > Consulta de pedidos"), um nível por item.
           A barra de menus, as abas e as listas de opções apenas visíveis NÃO são caminho: não as liste uma após a
           outra. Vazio se não houver menu aberto.
        3. campos_e_valores: "Rótulo: valor" dos campos preenchidos ou relevantes. Não liste campos vazios.
        4. mensagens: avisos, erros, confirmações e textos de status exibidos (janela de mensagem, barra de status).
        5. botoes: só botões, abas ou opções em destaque ou relevantes para a ação; não a barra de menus inteira.
        6. descricao: uma frase sobre o que a tela mostra e o que mudou em relação ao frame anterior.
        7. acao_provavel: o que o usuário provavelmente fez para chegar nesta tela ou vai fazer (ex.: clicou em
           "Salvar"). É dedução: deixe vazio se não der para saber.
        8. Ignore a barra de tarefas, o relógio e outros programas que não sejam o foco da gravação.
        Devolva um item por frame, com o número do frame informado.
        """;

    public async Task<DescricaoVisual> DescreverAsync(
        FramesExtraidos frames, IProgress<string>? progresso = null, CancellationToken cancelamento = default)
    {
        var chamador = new ChamadorComTentativas(http, "OpenRouter") { EsperaEntreTentativas = EsperaEntreTentativas };
        var lotes = frames.Frames.Chunk(FramesPorLote).ToList();
        var telas = new List<DescricaoTela>();
        string? anterior = null;

        for (var i = 0; i < lotes.Count; i++)
        {
            progresso?.Report($"Descrevendo as telas com {modelo} (lote {i + 1} de {lotes.Count})...");
            var corpo = MontarCorpo(frames.Pasta, lotes[i], anterior);

            for (var tentativa = 1; ; tentativa++)
            {
                var resposta = await chamador.EnviarAsync(
                    () => OpenRouterChat.Requisicao(apiKey, corpo),
                    falha => OpenRouterChat.Erro(falha, VariavelModelo), cancelamento);
                try
                {
                    // Na última tentativa aceita o lote incompleto: o frame que faltar fica registrado como vazio.
                    var lote = Interpretar(resposta, lotes[i], aceitarIncompleto: tentativa == TentativasDeFormato);
                    telas.AddRange(lote);
                    anterior = lote.LastOrDefault(t => !t.Vazia)?.Resumo() ?? anterior;
                    break;
                }
                catch (RespostaForaDoFormato) when (tentativa < TentativasDeFormato)
                {
                }
                catch (RespostaForaDoFormato ex)
                {
                    throw new MinutaException($"O modelo {modelo} devolveu as telas fora do formato esperado: {ex.Message}",
                        $"Tente de novo ou troque {VariavelModelo} por um modelo que aceite imagem e saída estruturada.", ex);
                }
            }
        }

        return new DescricaoVisual(modelo, VersaoPrompt, telas);
    }

    private string MontarCorpo(string pasta, IReadOnlyList<FrameVideo> lote, string? anterior)
    {
        var lista = string.Join("\n", lote.Select((f, i) => $"Frame {i + 1}: [{Transcricao.FormatarTempo(f.Tempo)}] ({f.Motivo})"));
        var conteudo = new JsonArray(new JsonObject
        {
            ["type"] = "text",
            ["text"] = $"""
                Descreva os {lote.Count} frames abaixo, na ordem.
                {(anterior is null ? "Estes são os primeiros frames do vídeo." : $"Última tela antes destes frames: {anterior}")}

                {lista}
                """,
        });

        for (var i = 0; i < lote.Count; i++)
        {
            var bytes = File.ReadAllBytes(Path.Combine(pasta, lote[i].Arquivo));
            conteudo.Add(new JsonObject { ["type"] = "text", ["text"] = $"Frame {i + 1} [{Transcricao.FormatarTempo(lote[i].Tempo)}]:" });
            conteudo.Add(new JsonObject
            {
                ["type"] = "image_url",
                ["image_url"] = new JsonObject { ["url"] = "data:image/jpeg;base64," + Convert.ToBase64String(bytes) },
            });
        }

        // ~700 tokens de saída por frame, com folga.
        return OpenRouterChat.Corpo(modelo, InstrucoesDoSistema, conteudo, "telas", Schema(), maxTokens: 1600 * lote.Count + 1000)
            .ToJsonString();
    }

    internal static JsonObject Schema()
    {
        var item = OpenRouterChat.Objeto(new()
        {
            ["frame"] = new JsonObject { ["type"] = "integer", ["description"] = "Número do frame informado." },
            ["titulo_janela"] = OpenRouterChat.Texto("Título da janela ou da tela ativa, copiado da tela."),
            ["caminho_menu"] = OpenRouterChat.Texto("Caminho do menu aberto até o item destacado, com \" > \"; vazio se não houver."),
            ["campos_e_valores"] = OpenRouterChat.ListaDeTextos("\"Rótulo: valor\" dos campos preenchidos ou relevantes."),
            ["mensagens"] = OpenRouterChat.ListaDeTextos("Avisos, erros e textos de status, copiados da tela."),
            ["botoes"] = OpenRouterChat.ListaDeTextos("Botões, abas ou opções em destaque ou relevantes."),
            ["descricao"] = OpenRouterChat.Texto("Uma frase: o que a tela mostra e o que mudou."),
            ["acao_provavel"] = OpenRouterChat.Texto("Ação provável do usuário; vazio se não der para saber."),
        });
        return OpenRouterChat.Objeto(new() { ["frames"] = new JsonObject { ["type"] = "array", ["items"] = item } });
    }

    /// <summary>Lê a resposta e casa cada item com o frame pelo número (1 = primeiro frame do lote).</summary>
    internal static IReadOnlyList<DescricaoTela> Interpretar(string corpo, IReadOnlyList<FrameVideo> lote, bool aceitarIncompleto)
    {
        var conteudo = OpenRouterChat.LerConteudo(corpo);

        RespostaTelas? resposta;
        try
        {
            resposta = JsonSerializer.Deserialize<RespostaTelas>(conteudo);
        }
        catch (JsonException ex)
        {
            throw new RespostaForaDoFormato("o conteúdo não é um JSON válido", ex);
        }

        var porNumero = new Dictionary<int, TelaBruta>();
        foreach (var t in resposta?.Frames ?? [])
            if (t.Frame is >= 1 && t.Frame <= lote.Count)
                porNumero.TryAdd(t.Frame.Value, t);

        if (porNumero.Count < lote.Count && !aceitarIncompleto)
            throw new RespostaForaDoFormato($"vieram {porNumero.Count} de {lote.Count} frames");

        // Documentos e contatos copiados da tela não passam daqui (nem para o cache).
        static string Limpo(string? s) => DadosPessoais.Mascarar(s?.Trim() ?? "");
        static IReadOnlyList<string> Lista(List<string>? l) =>
            (l ?? []).Select(x => DadosPessoais.Mascarar(x.Trim())).Where(x => x.Length > 0).ToList();

        return lote.Select((frame, i) => porNumero.TryGetValue(i + 1, out var t)
                ? new DescricaoTela(frame.Arquivo, frame.Tempo, Limpo(t.TituloJanela), Limpo(t.CaminhoMenu),
                    Lista(t.CamposEValores), Lista(t.Mensagens), Lista(t.Botoes), Limpo(t.Descricao), Limpo(t.AcaoProvavel))
                : new DescricaoTela(frame.Arquivo, frame.Tempo, "", "", [], [], [], "", ""))
            .ToList();
    }

    private sealed record RespostaTelas([property: JsonPropertyName("frames")] List<TelaBruta>? Frames);

    private sealed record TelaBruta(
        [property: JsonPropertyName("frame")] int? Frame,
        [property: JsonPropertyName("titulo_janela")] string? TituloJanela,
        [property: JsonPropertyName("caminho_menu")] string? CaminhoMenu,
        [property: JsonPropertyName("campos_e_valores")] List<string>? CamposEValores,
        [property: JsonPropertyName("mensagens")] List<string>? Mensagens,
        [property: JsonPropertyName("botoes")] List<string>? Botoes,
        [property: JsonPropertyName("descricao")] string? Descricao,
        [property: JsonPropertyName("acao_provavel")] string? AcaoProvavel);
}
