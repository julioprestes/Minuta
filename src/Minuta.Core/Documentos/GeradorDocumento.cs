using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Minuta.Core.Analises;
using Minuta.Core.Transcricoes;

namespace Minuta.Core.Documentos;

/// <summary>
/// Monta o documento .md a partir da análise, no formato do perfil. Não chama nenhuma API:
/// trocar o perfil ou ajustar o formato não gasta cota.
/// </summary>
public static class GeradorDocumento
{
    public const string Sufixo = ".minuta.md";

    private static readonly Regex Espacos = new(@"\s+");

    /// <param name="nomeVideo">Nome do arquivo do vídeo, para o título e o cabeçalho.</param>
    /// <param name="arquivoTranscricao">Nome do .txt da transcrição, citado no rodapé. Opcional.</param>
    public static string Gerar(
        AnaliseVideo analise,
        Transcricao transcricao,
        PerfilDocumento perfil,
        string nomeVideo,
        DateTime geradoEm,
        string? arquivoTranscricao = null)
    {
        var md = new StringBuilder();
        md.AppendLine($"# {Path.GetFileNameWithoutExtension(nomeVideo)} — {perfil.Titulo}");
        md.AppendLine();

        var citacoes = analise.TodasAsCitacoes.ToList();
        var duracao = transcricao.Duracao is { } d ? $" ({Transcricao.FormatarTempo(d)})" : "";
        md.AppendLine($"- **Vídeo:** {nomeVideo}{duracao}");
        md.AppendLine($"- **Gerado em:** {geradoEm.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture)}, pelo Minuta");
        md.AppendLine($"- **Modelos:** transcrição {transcricao.Modelo}; análise {analise.Modelo}");
        if (citacoes.Count > 0)
            md.AppendLine($"- **Citações conferidas na transcrição:** {citacoes.Count(c => c.Conferida)} de {citacoes.Count}");

        foreach (var secao in perfil.Secoes)
            EscreverSecao(md, secao, analise);

        md.AppendLine();
        md.AppendLine("---");
        md.AppendLine();
        md.AppendLine("Os tempos `[mm:ss]` indicam onde o trecho aparece no vídeo."
            + " ⚠ marca citação não encontrada na transcrição: confira no vídeo antes de usar.");
        if (arquivoTranscricao is not null)
        {
            md.AppendLine();
            md.AppendLine($"Transcrição completa: `{arquivoTranscricao}`.");
        }

        return md.ToString();
    }

    private static void EscreverSecao(StringBuilder md, SecaoPerfil secao, AnaliseVideo a)
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
                EscreverCards(md, secao.Titulo, a.Cards);
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

    private static void EscreverCards(StringBuilder md, string titulo, IReadOnlyList<CardTarefa> cards)
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
                md.AppendLine($"> {Tempo(c)} \"{Limpar(c.Trecho)}\"" + (c.Conferida ? "" : " ⚠") + "  ");
        }
    }

    private static void Titulo(StringBuilder md, string titulo)
    {
        md.AppendLine();
        md.AppendLine($"## {titulo}");
        md.AppendLine();
    }

    /// <summary>" [00:12, 01:05]" depois do item; ⚠ se alguma citação não foi conferida.</summary>
    private static string Tempos(IReadOnlyList<Citacao> citacoes, bool citacaoObrigatoria)
    {
        if (citacoes.Count == 0)
            return citacaoObrigatoria ? " ⚠ sem citação" : " *(não dito no vídeo)*";
        var tempos = string.Join(", ", citacoes.Select(c => Tempo(c).Trim('[', ']')));
        return $" [{tempos}]" + (citacoes.Any(c => !c.Conferida) ? " ⚠" : "");
    }

    private static string Tempo(Citacao c) => $"[{(c.Inicio is { } i ? Transcricao.FormatarTempo(i) : "?")}]";

    /// <summary>Uma linha só, sem espaços sobrando (texto do modelo pode vir com quebras).</summary>
    private static string Limpar(string texto) => Espacos.Replace(texto, " ").Trim();
}
