using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Minuta.Core.Http;
using Minuta.Core.Transcricoes;
using Minuta.Core.Visao;

namespace Minuta.Core.Analises;

/// <summary>
/// Envia a linha do tempo do vídeo (falas e telas) a um modelo do OpenRouter e pede a análise em JSON validado
/// por schema (modo estrito). As citações devolvidas são conferidas contra a linha do tempo antes de virar
/// <see cref="AnaliseVideo"/>.
/// </summary>
public sealed class RedatorOpenRouter(HttpClient http, string apiKey, string modelo)
{
    public const string Endereco = OpenRouterChat.Endereco;

    /// <summary>Aumente ao mudar o prompt ou o schema: invalida as análises salvas.</summary>
    public const int VersaoPrompt = 5;

    /// <summary>Respostas fora do formato são pedidas de novo uma vez.</summary>
    private const int TentativasDeFormato = 2;

    internal Func<int, TimeSpan> EsperaEntreTentativas { get; init; } = tentativa => TimeSpan.FromSeconds(3 * tentativa);

    internal const string InstrucoesDoSistema = """
        Você analisa um vídeo em que uma pessoa mostra um fluxo de sistema na tela e fala sobre ele (análise de
        requisito, relato de erro) ou conduz uma reunião. O resultado será lido por uma equipe de desenvolvimento
        que conhece o sistema, mas não viu o vídeo.

        Você recebe a linha do tempo do vídeo, em ordem, no formato [mm:ss] tipo: texto.
        - "fala:" é a transcrição do que foi dito. Linhas terminadas em "(?)" tiveram baixa confiança.
        - "tela:" é o que apareceu na tela, descrito por um modelo de visão: janela, menu aberto, mensagens (e, em
          vídeo sem fala, também campos e uma descrição). Os textos entre aspas foram copiados da tela.
          "ação provável (dedução)" é palpite desse modelo, não fato. "tela (até mm:ss)" é uma tela que ficou igual
          até esse tempo.
        - A transcrição quebra as frases em linhas: uma frase que parece cortada costuma continuar na fala seguinte.
          Leia as falas seguidas antes de concluir que algo ficou incompleto.
        Pode não haver falas (vídeo sem narração) ou não haver telas.

        Regras obrigatórias:
        1. Use somente o que está na linha do tempo. Não deduza, não complete e não invente nomes, telas, campos,
           números, mensagens ou regras.
        2. A FALA é a fonte principal; a tela complementa. Todo pedido, requisito ou problema dito vira regra de
           negócio, card ou passo, mesmo que a tela mostre outro nome ou não mostre nada daquilo. Uma divergência
           com a tela NUNCA elimina o item: escreva o item como foi dito e registre a divergência como dúvida.
           Antes de responder, confira se cada pedido dito aparece em alguma regra ou card.
           PEDIDO é o que se quer mudar ou acrescentar ("o cliente deseja", "gostaria", "que conste", "seja
           informado"). Frase que só situa o fluxo ("quando o cliente for cadastrar...", "aqui temos...", "ao clicar
           em...") é contexto, não pedido; e o que a tela já mostra funcionando não vira regra nem card.
        3. Cada decisão, regra de negócio, passo, próximo passo e card precisa de pelo menos uma citação:
           "trecho" é uma cópia LITERAL de palavras consecutivas de UMA linha da linha do tempo (de 5 a 30 palavras,
           sem resumir nem corrigir; de uma tela, copie o caminho do menu, a mensagem ou o campo) e "tempo" é o
           [mm:ss] dessa linha.
        4. Use os nomes exatos da tela (menus, janelas, campos, botões, mensagens) nos passos e nos cards; eles
           servem para localizar o problema no sistema. Se a fala estiver confusa (palavras cortadas ou trocadas),
           prefira os nomes da tela.
        5. O que ficou incerto, contraditório ou não foi dito, mas seria preciso saber para implementar ou para
           reproduzir, vira dúvida em aberto. Se a fala e a tela discordarem (ex.: a fala cita um menu e a tela mostra
           outra janela), isso é uma dúvida que cita as duas. Cite o trecho que levantou a dúvida, se houver; se não
           houver, deixe "citacoes" vazio. NÃO pergunte sobre pessoas, nomes, saudações ou frases soltas que não
           mudam o que deve ser feito: poucas dúvidas, todas úteis.
        6. A tela mostra o que o sistema FEZ, não o que ele DEVERIA fazer. Regras de negócio, cards e o que é
           erro vêm do que foi dito (ou do contexto informado). Se ninguém disse qual é o problema ou o esperado
           (ex.: vídeo sem fala), não crie regras nem cards a partir só da tela: descreva os passos vistos e registre
           como dúvida qual é o problema e qual seria o comportamento esperado.
        7. Seção sem conteúdo no vídeo fica com lista vazia. Não preencha por preencher.
        8. Escreva em português do Brasil, com frases curtas e objetivas. Para citar nomes da tela dentro dos seus
           textos, use aspas simples ('Salvar', 'Novo pedido'), nunca aspas duplas. Cada item é uma frase completa.

        Definições:
        - resumo: a explicação que permite entender o caso sem ver o vídeo, em texto corrido (3 a 6 frases): o que
          foi mostrado e em que tela ou menu, qual é o problema ou o pedido, e o que ficou em aberto. Só com o que
          está na linha do tempo; não repita citações nem liste os itens das outras seções.
        - decisao: só o que foi definido ou combinado ("ficou decidido", "vamos manter"). Sugestão ou pedido ("poderia
          ter", "talvez", "o cliente gostaria") NÃO é decisão: vira regra de negócio ou card, se for um pedido claro,
          ou dúvida, se ainda precisar ser confirmado.
        - regra_negocio: como o sistema deve se comportar, escrita como "O sistema deve ...".
        - passo_para_reproduzir: SÓ quando o vídeo mostra ou relata um erro, ou quando não há fala e o vídeo só
          mostra um fluxo; uma ação por item, na ordem em que acontece (abrir menu, preencher campo, clicar).
          Em vídeo de requisito ou pedido de melhoria, sem erro, deixe vazio: o caminho até a tela entra na regra ou
          no card. Comentário ou observação de quem fala não é passo.
        - proximo_passo: ação que alguém disse que vai fazer ou que precisa ser feita (inclua quem e quando, se ditos).
        - card: tarefa concreta de desenvolvimento que decorre de um pedido dito, com critérios de aceite verificáveis
          tirados do que foi dito ou mostrado e o caminho até a tela, se aparecer. Algo a confirmar com o cliente
          é dúvida, não card.

        Se vier um "Contexto informado por quem enviou o vídeo": ele não faz parte do vídeo. Use para entender o
        objetivo, mas não o cite. Se o vídeo não confirmar ou contradisser o contexto, registre como dúvida.
        """;

    /// <param name="visao">Telas descritas (Fase 2); null se o vídeo não tem imagem ou a trilha visual foi pulada.</param>
    /// <param name="contexto">Texto livre de quem enviou o vídeo (ex.: a descrição do chamado). Não é citável.</param>
    public async Task<AnaliseVideo> AnalisarAsync(
        Transcricao transcricao, DescricaoVisual? visao = null, string? contexto = null, CancellationToken cancelamento = default)
    {
        var chamador = new ChamadorComTentativas(http, "OpenRouter") { EsperaEntreTentativas = EsperaEntreTentativas };
        var linha = LinhaDoTempo.Montar(transcricao, visao);
        var mensagem = MensagemDoUsuario(transcricao, linha, contexto);
        var corpoRequisicao = OpenRouterChat.Corpo(modelo, InstrucoesDoSistema, mensagem, "analise_video", Schema(), maxTokens: 20000)
            .ToJsonString();

        for (var tentativa = 1; ; tentativa++)
        {
            var resposta = await chamador.EnviarAsync(
                () => OpenRouterChat.Requisicao(apiKey, corpoRequisicao),
                falha => OpenRouterChat.Erro(falha, "OPENROUTER_MODELO_ANALISE"), cancelamento);
            try
            {
                return Interpretar(resposta, linha) with
                {
                    Modelo = modelo,
                    VersaoPrompt = VersaoPrompt,
                    Origem = transcricao.Origem,
                    Assinatura = Assinar(mensagem),
                };
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

    /// <summary>
    /// Assinatura do que seria enviado ao modelo para estes dados. A análise salva só é reaproveitada se a
    /// assinatura for a mesma: mudou a transcrição, as telas ou o contexto, analisa de novo.
    /// </summary>
    public static string AssinaturaDaEntrada(Transcricao transcricao, DescricaoVisual? visao, string? contexto) =>
        Assinar(MensagemDoUsuario(transcricao, LinhaDoTempo.Montar(transcricao, visao), contexto));

    private static string Assinar(string mensagem) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(mensagem)))[..16];

    private static string MensagemDoUsuario(Transcricao transcricao, IReadOnlyList<EntradaLinhaDoTempo> linha, string? contexto)
    {
        var duracao = transcricao.Duracao is { } d ? Transcricao.FormatarTempo(d) : "desconhecida";
        var cabecalhoContexto = string.IsNullOrWhiteSpace(contexto)
            ? ""
            : $"Contexto informado por quem enviou o vídeo (não faz parte do vídeo; não cite):\n{contexto.Trim()}\n\n";
        var tipos = (linha.Any(e => !e.DaTela), linha.Any(e => e.DaTela)) switch
        {
            (true, true) => "falas e telas",
            (true, false) => "só falas (sem descrição das telas)",
            (false, true) => "só telas (o vídeo não tem fala reconhecida)",
            _ => "vazia",
        };
        return $"""
            {cabecalhoContexto}Linha do tempo do vídeo (duração {duracao}; {tipos}):

            {LinhaDoTempo.ParaTexto(linha)}
            """;
    }

    /// <summary>Schema estrito. Sem $ref: nem todo provedor aceita, então cada objeto é escrito por extenso.</summary>
    internal static JsonObject Schema()
    {
        static JsonObject Texto(string descricao) => OpenRouterChat.Texto(descricao);
        static JsonObject Objeto(JsonObject propriedades) => OpenRouterChat.Objeto(propriedades);

        static JsonObject Citacoes() => new()
        {
            ["type"] = "array",
            ["items"] = Objeto(new()
            {
                ["tempo"] = Texto("Tempo da linha citada, no formato mm:ss."),
                ["trecho"] = Texto("Cópia literal de 5 a 30 palavras consecutivas de uma linha (fala ou tela)."),
            }),
        };

        static JsonObject Lista(string descricao) => new()
        {
            ["type"] = "array",
            ["description"] = descricao,
            ["items"] = Objeto(new() { ["texto"] = Texto("O item, em uma frase."), ["citacoes"] = Citacoes() }),
        };

        return Objeto(new()
        {
            ["resumo"] = Texto("De 3 a 6 frases, para quem não viu o vídeo: o que foi mostrado e em que tela, o que está errado ou o que se pede, e o que ficou em aberto."),
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
                        ["items"] = Texto("Critério verificável tirado do que foi dito ou mostrado."),
                    },
                    ["citacoes"] = Citacoes(),
                }),
            },
        });
    }

    /// <summary>Lê a resposta do chat completions, valida o JSON da análise e confere as citações.</summary>
    internal static AnaliseVideo Interpretar(string corpo, IReadOnlyList<EntradaLinhaDoTempo> linhaDoTempo)
    {
        var conteudo = OpenRouterChat.LerConteudo(corpo);

        AnaliseBruta? bruta;
        try
        {
            bruta = JsonSerializer.Deserialize<AnaliseBruta>(conteudo);
        }
        catch (JsonException ex)
        {
            throw new RespostaForaDoFormato("o conteúdo não é um JSON válido", ex);
        }

        if (bruta is null || string.IsNullOrWhiteSpace(bruta.Resumo))
            throw new RespostaForaDoFormato("a análise veio sem resumo");

        var conferencia = new ConferenciaCitacoes(linhaDoTempo);
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
                .ToList());
    }

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
