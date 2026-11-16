using System.Text;
using Minuta.Core.Transcricoes;
using Minuta.Core.Visao;

namespace Minuta.Core.Analises;

/// <summary>Uma linha da linha do tempo: um trecho de fala ou a descrição de uma tela.</summary>
/// <param name="Frame">Arquivo do frame, quando é uma tela.</param>
/// <param name="Fim">Até quando a mesma tela ficou parada, quando ela se repetiu em mais de um frame.</param>
public sealed record EntradaLinhaDoTempo(
    TimeSpan Inicio, string Texto, bool DaTela, string? Frame = null, bool BaixaConfianca = false, TimeSpan? Fim = null);

/// <summary>
/// Junta as falas transcritas e as telas descritas, em ordem de tempo. É o que o redator recebe e o que
/// a conferência de citações usa: cada citação tem de existir numa fala ou numa tela.
/// Para não abafar a fala, telas iguais em sequência entram uma vez só e, quando há fala, a tela entra enxuta
/// (só janela, menu e mensagens: os nomes que servem para localizar no sistema). Sem fala, a tela vai completa,
/// porque é a única fonte.
/// </summary>
public static class LinhaDoTempo
{
    public static IReadOnlyList<EntradaLinhaDoTempo> Montar(Transcricao transcricao, DescricaoVisual? visao = null)
    {
        var falas = transcricao.Segmentos
            .Where(s => !string.IsNullOrWhiteSpace(s.Texto))
            .Select(s => new EntradaLinhaDoTempo(s.Inicio, s.Texto.Trim(), DaTela: false, BaixaConfianca: s.BaixaConfianca))
            .ToList();
        var enxuta = falas.Count > 0;

        var telas = new List<EntradaLinhaDoTempo>();
        foreach (var g in visao?.TelasDistintas() ?? [])
        {
            var texto = enxuta ? TextoEnxutoDaTela(g.Tela) : TextoDaTela(g.Tela);
            if (texto.Length == 0)
                continue; // sem janela, menu nem mensagem, a tela não acrescenta nome nenhum à fala
            var fim = g.Frames > 1 ? g.Fim : (TimeSpan?)null;
            // Enxutas, telas diferentes podem virar o mesmo texto (só os campos mudavam): junta de novo.
            if (telas.Count > 0 && telas[^1].Texto == texto)
                telas[^1] = telas[^1] with { Fim = fim ?? g.Tela.Tempo };
            else
                telas.Add(new EntradaLinhaDoTempo(g.Tela.Tempo, texto, DaTela: true, Frame: g.Tela.Arquivo, Fim: fim));
        }

        // Na mesma marca de tempo, a tela vem antes da fala (a fala costuma comentar o que já está na tela).
        return telas.Concat(falas).OrderBy(e => e.Inicio).ThenBy(e => e.DaTela ? 0 : 1).ToList();
    }

    /// <summary>Só os nomes copiados da tela: janela, menu aberto e mensagens.</summary>
    public static string TextoEnxutoDaTela(DescricaoTela t)
    {
        var partes = new List<string>();
        if (t.TituloJanela.Length > 0) partes.Add($"janela \"{t.TituloJanela}\"");
        if (t.CaminhoMenu.Length > 0) partes.Add($"menu aberto: {t.CaminhoMenu}");
        if (t.Mensagens.Count > 0) partes.Add("mensagem: " + string.Join(" / ", t.Mensagens.Select(m => $"\"{m}\"")));
        return string.Join(" | ", partes);
    }

    /// <summary>Uma linha por entrada: "[mm:ss] fala: ...", "[mm:ss] tela: ..." ou "[mm:ss] tela (até mm:ss): ...".</summary>
    public static string ParaTexto(IEnumerable<EntradaLinhaDoTempo> entradas)
    {
        var texto = new StringBuilder();
        foreach (var e in entradas)
            texto.Append('[').Append(Transcricao.FormatarTempo(e.Inicio)).Append("] ")
                 .Append(!e.DaTela ? "fala: " : e.Fim is { } fim ? $"tela (até {Transcricao.FormatarTempo(fim)}): " : "tela: ")
                 .Append(e.Texto)
                 .AppendLine(e.BaixaConfianca ? " (?)" : "");
        return texto.ToString();
    }

    /// <summary>A tela em uma linha, com os textos copiados da tela entre aspas onde fazem diferença.</summary>
    public static string TextoDaTela(DescricaoTela t)
    {
        var partes = new List<string>();
        if (t.TituloJanela.Length > 0) partes.Add($"janela \"{t.TituloJanela}\"");
        if (t.CaminhoMenu.Length > 0) partes.Add($"menu aberto: {t.CaminhoMenu}");
        if (t.CamposEValores.Count > 0) partes.Add("campos: " + string.Join("; ", t.CamposEValores));
        if (t.Mensagens.Count > 0) partes.Add("mensagem: " + string.Join(" / ", t.Mensagens.Select(m => $"\"{m}\"")));
        if (t.Botoes.Count > 0) partes.Add("botões: " + string.Join(", ", t.Botoes));
        if (t.Descricao.Length > 0) partes.Add(t.Descricao.TrimEnd('.'));
        if (t.AcaoProvavel.Length > 0) partes.Add($"ação provável (dedução): {t.AcaoProvavel.TrimEnd('.')}");
        return string.Join(" | ", partes);
    }
}
