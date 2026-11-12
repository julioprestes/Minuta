namespace Minuta.Core.Configuracao;

/// <summary>
/// Leitor mínimo de arquivo .env (CHAVE=valor por linha). Evita depender de pacote externo.
/// Suporta comentários (#), linhas em branco, prefixo "export " e valores entre aspas simples ou duplas.
/// </summary>
public static class ArquivoEnv
{
    public const string NomeArquivo = ".env";

    public static Dictionary<string, string> Interpretar(string conteudo)
    {
        var valores = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var linhaBruta in conteudo.Split('\n'))
        {
            var linha = linhaBruta.Trim();
            if (linha.Length == 0 || linha.StartsWith('#'))
                continue;

            if (linha.StartsWith("export ", StringComparison.Ordinal))
                linha = linha["export ".Length..].TrimStart();

            var igual = linha.IndexOf('=');
            if (igual <= 0)
                continue;

            var chave = linha[..igual].Trim();
            var valor = linha[(igual + 1)..].Trim();

            if (valor.Length >= 2 && (valor[0] == '"' || valor[0] == '\'') && valor[^1] == valor[0])
                valor = valor[1..^1];

            valores[chave] = valor;
        }

        return valores;
    }

    /// <summary>
    /// Procura o .env no diretório informado e nos diretórios acima dele. Retorna null se não achar.
    /// </summary>
    public static string? Localizar(string diretorioInicial)
    {
        for (var dir = new DirectoryInfo(diretorioInicial); dir is not null; dir = dir.Parent)
        {
            var caminho = Path.Combine(dir.FullName, NomeArquivo);
            if (File.Exists(caminho))
                return caminho;
        }

        return null;
    }
}
