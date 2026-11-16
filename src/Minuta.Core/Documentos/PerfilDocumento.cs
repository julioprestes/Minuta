namespace Minuta.Core.Documentos;

/// <summary>Partes da análise que um perfil pode mostrar.</summary>
public enum TipoSecao
{
    Resumo,
    PassosParaReproduzir,
    RegrasNegocio,
    Decisoes,
    DuvidasEmAberto,
    ProximosPassos,
    Cards,

    /// <summary>O que se pede, em uma linha por pedido: os títulos dos cards (ou as regras, se não houver cards).</summary>
    Pedidos,
}

/// <summary>Como os itens de uma seção aparecem no Markdown.</summary>
public enum EstiloLista
{
    Marcadores,
    Numerada,
    Tarefas,
}

public sealed record SecaoPerfil(TipoSecao Tipo, string Titulo, EstiloLista Estilo = EstiloLista.Marcadores);

/// <summary>
/// Perfil de documento: título e seções, na ordem. A análise é a mesma para todos os perfis;
/// o perfil só escolhe o que mostrar e como. Seções sem itens não aparecem.
/// O documento de cada perfil tem nome próprio (<c>&lt;video&gt;.&lt;id&gt;.md</c>), e trocar de perfil não chama API.
/// </summary>
public sealed record PerfilDocumento(string Id, string Titulo, IReadOnlyList<SecaoPerfil> Secoes)
{
    /// <summary>Nome do documento deste perfil para o vídeo: "video.resumo.md", "video.analise.md"...</summary>
    public string NomeArquivo(string nomeVideoSemExtensao) => $"{nomeVideoSemExtensao}.{Id}.md";
}

public static class PerfisDocumento
{
    /// <summary>Para entender o vídeo sem assistir: o que foi mostrado, o que se pede e o que ficou em aberto.</summary>
    public static readonly PerfilDocumento Resumo = new("resumo", "Resumo",
    [
        new(TipoSecao.Resumo, "O que o vídeo mostra"),
        new(TipoSecao.Pedidos, "O que se pede"),
        new(TipoSecao.PassosParaReproduzir, "Passos para reproduzir", EstiloLista.Numerada),
        new(TipoSecao.DuvidasEmAberto, "Dúvidas em aberto"),
    ]);

    /// <summary>Para o desenvolvimento: regras, cards com critérios de aceite e a origem de cada item.</summary>
    public static readonly PerfilDocumento Analise = new("analise", "Análise para desenvolvimento",
    [
        new(TipoSecao.Resumo, "Resumo"),
        new(TipoSecao.PassosParaReproduzir, "Passos para reproduzir", EstiloLista.Numerada),
        new(TipoSecao.RegrasNegocio, "Regras de negócio"),
        new(TipoSecao.Decisoes, "Decisões"),
        new(TipoSecao.DuvidasEmAberto, "Dúvidas em aberto"),
        new(TipoSecao.Cards, "Cards de tarefa"),
        new(TipoSecao.ProximosPassos, "Próximos passos", EstiloLista.Tarefas),
    ]);

    public static readonly PerfilDocumento Ata = new("ata", "Ata de reunião",
    [
        new(TipoSecao.Resumo, "Resumo"),
        new(TipoSecao.Decisoes, "Decisões"),
        new(TipoSecao.ProximosPassos, "Próximos passos", EstiloLista.Tarefas),
        new(TipoSecao.DuvidasEmAberto, "Pendências e dúvidas"),
    ]);

    public static IReadOnlyList<PerfilDocumento> Todos { get; } = [Analise, Resumo, Ata];

    public static PerfilDocumento Obter(string id) =>
        Todos.FirstOrDefault(p => p.Id.Equals(id.Trim(), StringComparison.OrdinalIgnoreCase))
        ?? throw new MinutaException(
            $"Perfil desconhecido: \"{id}\".",
            "Perfis disponíveis: " + string.Join(", ", Todos.Select(p => $"{p.Id} ({p.Titulo})")) + ".");
}
