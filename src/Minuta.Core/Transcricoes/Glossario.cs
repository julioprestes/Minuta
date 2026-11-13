using System.Text;

namespace Minuta.Core.Transcricoes;

/// <summary>
/// Termos que o Whisper costuma errar (siglas, nomes de telas, jargão), enviados no campo "prompt" da
/// transcrição para indicar a grafia certa. O arquivo é local e fica fora do git: um termo por linha,
/// linhas com # são comentários.
/// </summary>
public sealed record Glossario(IReadOnlyList<string> Termos, IReadOnlyList<string> TermosIgnorados)
{
    public const string NomeArquivoPadrao = "glossario.txt";

    /// <summary>
    /// O prompt do Whisper aceita até 224 tokens. Em português, isso dá uns 600 a 700 caracteres;
    /// o limite fica abaixo disso por segurança, porque o tokenizador não está disponível aqui.
    /// </summary>
    public const int LimiteCaracteres = 500;

    private const string Inicio = "Glossário: ";

    /// <summary>Texto enviado ao Whisper, ou null se não houver termos.</summary>
    public string? Prompt => Termos.Count == 0 ? null : Inicio + string.Join(", ", Termos) + ".";

    public static Glossario Carregar(string caminho)
    {
        if (!File.Exists(caminho))
            throw new MinutaException(
                $"Glossário não encontrado: {caminho}",
                $"Crie o arquivo (um termo por linha) ou ajuste {Configuracao.ConfiguracaoMinuta.VarGlossario} no .env.");
        return Interpretar(File.ReadAllText(caminho, Encoding.UTF8));
    }

    /// <summary>Lê os termos na ordem do arquivo, sem repetidos. O que não cabe no limite fica de fora.</summary>
    public static Glossario Interpretar(string conteudo)
    {
        var termos = new List<string>();
        var ignorados = new List<string>();
        var vistos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tamanho = Inicio.Length + 1; // + ponto final

        foreach (var linha in conteudo.Split('\n'))
        {
            var termo = linha.Trim();
            if (termo.Length == 0 || termo.StartsWith('#') || !vistos.Add(termo))
                continue;

            var acrescimo = termo.Length + (termos.Count > 0 ? 2 : 0); // ", "
            if (tamanho + acrescimo > LimiteCaracteres)
            {
                ignorados.Add(termo);
                continue;
            }

            termos.Add(termo);
            tamanho += acrescimo;
        }

        return new Glossario(termos, ignorados);
    }
}
