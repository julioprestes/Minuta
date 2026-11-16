using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Minuta.Core.Analises;
using Minuta.Core.Transcricoes;
using Minuta.Core.Visao;

namespace Minuta.Core.Documentos;

/// <summary>O que, além da análise, pode aparecer no documento. Tudo opcional.</summary>
public sealed record ExtrasDocumento
{
    /// <summary>Nome do .txt da transcrição, citado no rodapé.</summary>
    public string? ArquivoTranscricao { get; init; }

    /// <summary>Telas descritas: contadas no cabeçalho (a lista fica em <see cref="ArquivoTelas"/>).</summary>
    public DescricaoVisual? Visao { get; init; }

    /// <summary>Nome da pasta de frames, relativo ao .md (fica ao lado dele), para os links das imagens.</summary>
    public string? PastaFrames { get; init; }

    /// <summary>Nome do .telas.md com a lista das telas, citado no cabeçalho e no rodapé.</summary>
    public string? ArquivoTelas { get; init; }

    /// <summary>Contexto informado por quem enviou o vídeo.</summary>
    public string? Contexto { get; init; }
}

/// <summary>
/// Monta o documento .md a partir da análise, no formato do perfil. Não chama nenhuma API:
/// trocar o perfil ou ajustar o formato não gasta cota.
/// </summary>
public static class GeradorDocumento
{
    private static readonly Regex Espacos = new(@"\s+");

    /// <param name="nomeVideo">Nome do arquivo do vídeo, para o título e o cabeçalho.</param>
    public static string Gerar(
        AnaliseVideo analise,
        Transcricao transcricao,
        PerfilDocumento perfil,
        string nomeVideo,
        DateTime geradoEm,
        ExtrasDocumento? extras = null)
    {
        extras ??= new ExtrasDocumento();
        var md = new StringBuilder();
        md.AppendLine($"# {Path.GetFileNameWithoutExtension(nomeVideo)} — {perfil.Titulo}");
        md.AppendLine();

        var citacoes = analise.TodasAsCitacoes.ToList();
        var duracao = transcricao.Duracao is { } d ? $" ({Transcricao.FormatarTempo(d)})" : "";
        md.AppendLine($"- **Vídeo:** {nomeVideo}{duracao}");
        md.AppendLine($"- **Gerado em:** {geradoEm.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture)}, pelo Minuta");
        var modelos = $"transcrição {transcricao.Modelo}";
        if (extras.Visao is { } v)
            modelos += $"; telas {v.Modelo}";
        md.AppendLine($"- **Modelos:** {modelos}; análise {analise.Modelo}");
        if (extras.Visao is { } visao)
        {
            var onde = new List<string>();
            if (extras.ArquivoTelas is not null) onde.Add($"lista em `{extras.ArquivoTelas}`");
            if (extras.PastaFrames is not null) onde.Add($"imagens em `{extras.PastaFrames}/`");
            md.AppendLine($"- **Telas descritas:** {visao.TelasDistintas().Count}"
                + (onde.Count == 0 ? "" : $" ({string.Join("; ", onde)})"));
        }
        if (!string.IsNullOrWhiteSpace(extras.Contexto))
            md.AppendLine($"- **Contexto informado (não vem do vídeo):** {Limpar(extras.Contexto)}");
        if (citacoes.Count > 0)
            md.AppendLine($"- **Citações conferidas no vídeo:** {citacoes.Count(c => c.Conferida)} de {citacoes.Count}");

        foreach (var secao in perfil.Secoes)
            EscreverSecao(md, secao, analise, extras);

        md.AppendLine();
        md.AppendLine("---");
        md.AppendLine();
        md.AppendLine("Os tempos `[mm:ss]` indicam onde o trecho aparece no vídeo; *(tela)* marca o que foi lido na tela"
            + " por um modelo de visão, e não dito. ⚠ marca citação não encontrada no vídeo: confira antes de usar.");
        if (extras.ArquivoTranscricao is not null)
        {
            md.AppendLine();
            md.AppendLine($"Transcrição completa: `{extras.ArquivoTranscricao}`.");
        }
        if (extras.ArquivoTelas is not null)
        {
            md.AppendLine();
            md.AppendLine($"Telas do vídeo: `{extras.ArquivoTelas}`.");
        }

        return md.ToString();
    }

    private static void EscreverSecao(StringBuilder md, SecaoPerfil secao, AnaliseVideo a, ExtrasDocumento extras)
    {
        switch (secao.Tipo)
        {
            case TipoSecao.Resumo:
                if (string.IsNullOrWhiteSpace(a.Resumo))
                    return;
                Titulo(md, secao.Titulo);
                md.AppendLine(a.Resumo.Trim()); // o resumo pode ter parágrafos
                return;

            case TipoSecao.Cards:
                EscreverCards(md, secao.Titulo, a.Cards, extras);
                return;

            case TipoSecao.Pedidos:
                // Os cards já são os pedidos em uma frase; sem cards, as regras dizem o que se espera do sistema.
                var pedidos = a.Cards.Count > 0
                    ? a.Cards.Select(c => new ItemCitado(c.Titulo, c.Citacoes)).ToList()
                    : a.RegrasNegocio;
                EscreverLista(md, secao, pedidos, citacaoObrigatoria: true);
                return;

            default:
                var (itens, citacaoObrigatoria) = secao.Tipo switch
                {
                    TipoSecao.PassosParaReproduzir => (a.PassosParaReproduzir, true),
                    TipoSecao.RegrasNegocio => (a.RegrasNegocio, true),
                    TipoSecao.Decisoes => (a.Decisoes, true),
                    // Dúvida muitas vezes é justamente o que não foi dito: a citação é opcional.
                    TipoSecao.DuvidasEmAberto => (a.DuvidasEmAberto, false),
                    TipoSecao.ProximosPassos => (a.ProximosPassos, true),
                    _ => throw new ArgumentOutOfRangeException(nameof(secao), secao.Tipo, null),
                };
                EscreverLista(md, secao, itens, citacaoObrigatoria);
                return;
        }
    }

    private static void EscreverLista(StringBuilder md, SecaoPerfil secao, IReadOnlyList<ItemCitado> itens, bool citacaoObrigatoria)
    {
        if (itens.Count == 0)
            return;

        Titulo(md, secao.Titulo);
        for (var i = 0; i < itens.Count; i++)
        {
            var marcador = secao.Estilo switch
            {
                EstiloLista.Numerada => $"{i + 1}.",
                EstiloLista.Tarefas => "- [ ]",
                _ => "-",
            };
            md.AppendLine($"{marcador} {Limpar(itens[i].Texto)}{Tempos(itens[i].Citacoes, citacaoObrigatoria)}");
        }
    }

    private static void EscreverCards(StringBuilder md, string titulo, IReadOnlyList<CardTarefa> cards, ExtrasDocumento extras)
    {
        if (cards.Count == 0)
            return;

        Titulo(md, titulo);
        for (var i = 0; i < cards.Count; i++)
        {
            var card = cards[i];
            if (i > 0)
                md.AppendLine();
            md.AppendLine($"### {i + 1}. {Limpar(card.Titulo)}");
            if (!string.IsNullOrWhiteSpace(card.Descricao))
            {
                md.AppendLine();
                md.AppendLine(Limpar(card.Descricao));
            }

            if (card.CriteriosAceite.Count > 0)
            {
                md.AppendLine();
                md.AppendLine("**Critérios de aceite**");
                md.AppendLine();
                foreach (var criterio in card.CriteriosAceite)
                    md.AppendLine($"- [ ] {Limpar(criterio)}");
            }

            md.AppendLine();
            if (card.Citacoes.Count == 0)
            {
                md.AppendLine("**Origem no vídeo:** ⚠ sem citação");
                continue;
            }

            md.AppendLine("**Origem no vídeo**");
            md.AppendLine();
            foreach (var c in card.Citacoes)
            {
                var link = c.DaTela && c.Frame is not null && extras.PastaFrames is not null
                    ? $" — {LinkImagem(extras.PastaFrames, c.Frame, "ver tela")}"
                    : "";
                md.AppendLine($"> {Tempo(c)}{(c.DaTela ? " *(tela)*" : "")} \"{Limpar(c.Trecho)}\"" + (c.Conferida ? "" : " ⚠") + link + "  ");
            }
        }
    }

    /// <summary>Link relativo; os sinais de menor e maior permitem espaços no caminho (nome do vídeo).</summary>
    private static string LinkImagem(string pasta, string arquivo, string texto) => $"[{texto}](<{pasta}/{arquivo}>)";

    private static void Titulo(StringBuilder md, string titulo)
    {
        md.AppendLine();
        md.AppendLine($"## {titulo}");
        md.AppendLine();
    }

    /// <summary>" [00:12, 01:05 (tela)]" depois do item; ⚠ se alguma citação não foi conferida.</summary>
    private static string Tempos(IReadOnlyList<Citacao> citacoes, bool citacaoObrigatoria)
    {
        if (citacoes.Count == 0)
            return citacaoObrigatoria ? " ⚠ sem citação" : " *(não dito no vídeo)*";
        var tempos = string.Join(", ", citacoes.Select(c => Tempo(c).Trim('[', ']') + (c.DaTela ? " (tela)" : "")).Distinct());
        return $" [{tempos}]" + (citacoes.Any(c => !c.Conferida) ? " ⚠" : "");
    }

    private static string Tempo(Citacao c) => $"[{(c.Inicio is { } i ? Transcricao.FormatarTempo(i) : "?")}]";

    /// <summary>Uma linha só, sem espaços sobrando (texto do modelo pode vir com quebras).</summary>
    private static string Limpar(string texto) => Espacos.Replace(texto, " ").Trim();
}
