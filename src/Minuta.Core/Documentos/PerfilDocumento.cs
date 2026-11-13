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
/// </summary>
public sealed record PerfilDocumento(string Id, string Titulo, IReadOnlyList<SecaoPerfil> Secoes);

public static class PerfisDocumento
{
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

    public static IReadOnlyList<PerfilDocumento> Todos { get; } = [Analise, Ata];

    public static PerfilDocumento Obter(string id) =>
        Todos.FirstOrDefault(p => p.Id.Equals(id.Trim(), StringComparison.OrdinalIgnoreCase))
        ?? throw new MinutaException(
            $"Perfil desconhecido: \"{id}\".",
            "Perfis disponíveis: " + string.Join(", ", Todos.Select(p => $"{p.Id} ({p.Titulo})")) + ".");
}
