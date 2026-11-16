using System.Text.RegularExpressions;

namespace Minuta.Core.Visao;

/// <summary>
/// Esconde documentos e contatos que o modelo de visão copia da tela (CPF, CNPJ, telefone, e-mail), para que não
/// cheguem à análise, ao <c>.visao.json</c> nem ao <c>.telas.md</c>. Nomes e endereços não têm formato fixo e
/// continuam: as imagens dos frames também guardam tudo o que estava na tela.
/// </summary>
public static class DadosPessoais
{
    public const string Oculto = "[oculto]";

    private static readonly Regex[] Padroes =
    [
        new(@"[\w.+-]+@[\w-]+(\.[\w-]+)+"),                              // e-mail
        new(@"\b\d{2}\.\d{3}\.\d{3}/\d{4}-\d{2}\b"),                     // CNPJ formatado
        new(@"\b\d{3}\.\d{3}\.\d{3}-\d{1,2}\b"),                         // CPF formatado (o modelo às vezes corta o fim)
        // Telefone formatado: com DDD entre parênteses, ou celular (9xxxx-xxxx). "2025-2026" (safra) não é telefone.
        new(@"\(\d{2}\)\s?9?\d{4}-\d{4}\b"),
        new(@"\b9\d{4}-\d{4}\b"),
        new(@"(?<![\d,.])\d{10,14}(?![\d,.])"),                          // CPF, CNPJ ou telefone só com dígitos
    ];

    public static string Mascarar(string texto)
    {
        foreach (var padrao in Padroes)
            texto = padrao.Replace(texto, Oculto);
        return texto;
    }
}
