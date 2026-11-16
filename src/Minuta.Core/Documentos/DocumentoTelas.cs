using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Minuta.Core.Transcricoes;
using Minuta.Core.Visao;

namespace Minuta.Core.Documentos;

/// <summary>
/// Monta <c>&lt;video&gt;.telas.md</c>: a lista das telas do vídeo, com as repetidas juntadas, para quem precisa do
/// detalhe (ex.: anexar a um chat que conhece o sistema). Fica fora do documento principal para não poluí-lo.
/// </summary>
public static class DocumentoTelas
{
    public const string Sufixo = ".telas.md";

    private static readonly Regex Espacos = new(@"\s+");

    /// <param name="pastaFrames">Nome da pasta de frames, relativo ao .md, para os links das imagens.</param>
    public static string Gerar(DescricaoVisual visao, string nomeVideo, string? pastaFrames, DateTime geradoEm)
    {
        var grupos = visao.TelasDistintas();
        var md = new StringBuilder();
        md.AppendLine($"# {Path.GetFileNameWithoutExtension(nomeVideo)} — Telas do vídeo");
        md.AppendLine();
        md.AppendLine($"- **Vídeo:** {nomeVideo}");
        md.AppendLine($"- **Gerado em:** {geradoEm.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture)}, pelo Minuta");
        md.AppendLine($"- **Modelo de visão:** {visao.Modelo}");
        md.AppendLine($"- **Telas:** {grupos.Count} distintas, de {visao.Telas.Count} frames");
        md.AppendLine();
        md.AppendLine("Os textos foram lidos da tela por um modelo de visão e podem ter erros: confira na imagem antes de usar."
            + " Telas iguais em sequência aparecem uma vez, com o intervalo `[início–fim]`.");
        md.AppendLine();

        foreach (var g in grupos)
        {
            var t = g.Tela;
            var tempo = Transcricao.FormatarTempo(t.Tempo)
                + (g.Frames > 1 ? "–" + Transcricao.FormatarTempo(g.Fim) : "");
            var link = pastaFrames is null ? "" : $" — [imagem](<{pastaFrames}/{t.Arquivo}>)";
            md.AppendLine($"- [{tempo}] {Limpar(t.Resumo())}{link}");
            if (t.CamposEValores.Count > 0)
                md.AppendLine($"  - campos: {Limpar(string.Join("; ", t.CamposEValores))}");
            if (t.Descricao.Length > 0 && (t.TituloJanela.Length > 0 || t.CaminhoMenu.Length > 0 || t.Mensagens.Count > 0))
                md.AppendLine($"  - {Limpar(t.Descricao)}"); // sem textos da tela, o Resumo() já é a descrição
        }

        return md.ToString();
    }

    private static string Limpar(string texto) => Espacos.Replace(texto, " ").Trim();
}
