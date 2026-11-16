using System.Text;
using System.Text.Json;
using Minuta.Core.Midia;
using Minuta.Core.Transcricoes;

namespace Minuta.Core.Visao;

/// <summary>
/// O que o modelo de visão leu em um frame. Os textos devem ser cópia literal da tela, para servirem de chave
/// de busca no sistema; <see cref="AcaoProvavel"/> é dedução e deve ser tratada como tal.
/// </summary>
public sealed record DescricaoTela(
    string Arquivo,
    TimeSpan Tempo,
    string TituloJanela,
    string CaminhoMenu,
    IReadOnlyList<string> CamposEValores,
    IReadOnlyList<string> Mensagens,
    IReadOnlyList<string> Botoes,
    string Descricao,
    string AcaoProvavel)
{
    /// <summary>O modelo não devolveu este frame (fica registrado, sem inventar conteúdo).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool Vazia => Descricao.Length == 0 && TituloJanela.Length == 0 && CaminhoMenu.Length == 0
        && Mensagens.Count == 0 && CamposEValores.Count == 0;

    /// <summary>Resumo de uma linha: janela, menu e mensagens.</summary>
    public string Resumo()
    {
        var partes = new List<string>();
        if (TituloJanela.Length > 0) partes.Add(TituloJanela);
        if (CaminhoMenu.Length > 0) partes.Add("menu: " + CaminhoMenu);
        if (Mensagens.Count > 0) partes.Add("mensagem: " + string.Join(" / ", Mensagens.Select(m => $"\"{m}\"")));
        if (partes.Count == 0 && Descricao.Length > 0) partes.Add(Descricao);
        return partes.Count == 0 ? "(sem descrição)" : string.Join(" | ", partes);
    }
}

/// <summary>Descrições de todos os frames de um vídeo, com o que é preciso para saber se ainda valem.</summary>
public sealed record DescricaoVisual(string Modelo, int VersaoPrompt, IReadOnlyList<DescricaoTela> Telas)
{
    public OrigemTranscricao? Origem { get; init; }
}

/// <summary>Grava e lê <c>&lt;video&gt;.visao.json</c>, para não gastar cota descrevendo os mesmos frames de novo.</summary>
public static class ArquivoVisao
{
    public const string Sufixo = ".visao.json";

    private static readonly JsonSerializerOptions Opcoes = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static void Salvar(DescricaoVisual visual, string caminho) =>
        File.WriteAllText(caminho, JsonSerializer.Serialize(visual, Opcoes), Encoding.UTF8);

    /// <summary>
    /// Lê as descrições salvas se forem do mesmo vídeo, modelo e versão do prompt, e dos mesmos frames
    /// (mesmos arquivos e tempos). Caso contrário, null.
    /// </summary>
    public static DescricaoVisual? Carregar(
        string caminho, FileInfo video, string modelo, int versaoPrompt, IReadOnlyList<FrameVideo> frames)
    {
        if (!File.Exists(caminho))
            return null;
        try
        {
            var salva = JsonSerializer.Deserialize<DescricaoVisual>(File.ReadAllText(caminho), Opcoes);
            return salva is not null
                && salva.Modelo == modelo
                && salva.VersaoPrompt == versaoPrompt
                && salva.Origem == OrigemTranscricao.De(video)
                && salva.Telas.Select(t => (t.Arquivo, t.Tempo)).SequenceEqual(frames.Select(f => (f.Arquivo, f.Tempo)))
                ? salva
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
