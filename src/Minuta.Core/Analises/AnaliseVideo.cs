using System.Text;
using System.Text.Json;
using Minuta.Core.Transcricoes;

namespace Minuta.Core.Analises;

/// <summary>Trecho da linha do tempo (fala ou tela) que justifica um item.</summary>
/// <param name="Inicio">Momento no vídeo. Se conferida, vem da própria linha do tempo; senão, é o que o modelo informou.</param>
/// <param name="Conferida">O trecho foi encontrado na linha do tempo.</param>
/// <param name="DaTela">O trecho veio da descrição de uma tela, não da fala.</param>
/// <param name="Frame">Arquivo do frame (na pasta de frames) quando a citação é de uma tela.</param>
public sealed record Citacao(string Trecho, TimeSpan? Inicio, bool Conferida, bool DaTela = false, string? Frame = null);

public sealed record ItemCitado(string Texto, IReadOnlyList<Citacao> Citacoes);

public sealed record CardTarefa(
    string Titulo,
    string Descricao,
    IReadOnlyList<string> CriteriosAceite,
    IReadOnlyList<Citacao> Citacoes);

/// <summary>
/// Resultado geral da análise de um vídeo. É o mesmo para todos os perfis de documento:
/// cada perfil escolhe quais seções mostrar e como.
/// </summary>
public sealed record AnaliseVideo(
    string Resumo,
    IReadOnlyList<ItemCitado> Decisoes,
    IReadOnlyList<ItemCitado> RegrasNegocio,
    IReadOnlyList<ItemCitado> DuvidasEmAberto,
    IReadOnlyList<ItemCitado> PassosParaReproduzir,
    IReadOnlyList<ItemCitado> ProximosPassos,
    IReadOnlyList<CardTarefa> Cards)
{
    public string Modelo { get; init; } = "";
    public int VersaoPrompt { get; init; }
    public OrigemTranscricao? Origem { get; init; }

    /// <summary>Assinatura da entrada enviada ao modelo (falas, telas e contexto); ver <see cref="RedatorOpenRouter.AssinaturaDaEntrada"/>.</summary>
    public string? Assinatura { get; init; }

    public IEnumerable<Citacao> TodasAsCitacoes =>
        new[] { Decisoes, RegrasNegocio, DuvidasEmAberto, PassosParaReproduzir, ProximosPassos }
            .SelectMany(lista => lista).SelectMany(i => i.Citacoes)
            .Concat(Cards.SelectMany(c => c.Citacoes));
}

/// <summary>Grava e lê a análise (.analise.json), para refazer o documento sem chamar a API de novo.</summary>
public static class ArquivoAnalise
{
    public const string Sufixo = ".analise.json";

    private static readonly JsonSerializerOptions Opcoes = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static void Salvar(AnaliseVideo analise, string caminho) =>
        File.WriteAllText(caminho, JsonSerializer.Serialize(analise, Opcoes), Encoding.UTF8);

    /// <summary>
    /// Lê a análise salva se for do mesmo vídeo, do mesmo modelo, da mesma versão do prompt e, quando informada,
    /// da mesma entrada (assinatura); senão, null.
    /// </summary>
    public static AnaliseVideo? Carregar(string caminho, FileInfo video, string modelo, int versaoPrompt, string? assinatura = null)
    {
        if (!File.Exists(caminho))
            return null;

        try
        {
            var salva = JsonSerializer.Deserialize<AnaliseVideo>(File.ReadAllText(caminho), Opcoes);
            return salva is not null
                && salva.Origem == OrigemTranscricao.De(video)
                && salva.Modelo == modelo
                && salva.VersaoPrompt == versaoPrompt
                && (assinatura is null || salva.Assinatura == assinatura)
                ? salva
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
