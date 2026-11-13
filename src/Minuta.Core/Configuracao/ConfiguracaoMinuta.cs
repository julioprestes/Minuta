namespace Minuta.Core.Configuracao;

/// <summary>
/// Configuração do Minuta. Vem das variáveis de ambiente e, para o que não estiver nelas, do arquivo .env.
/// Variável de ambiente sempre tem prioridade sobre o .env.
/// </summary>
public sealed record ConfiguracaoMinuta
{
    public const string VarGroqApiKey = "GROQ_API_KEY";
    public const string VarModeloTranscricao = "GROQ_MODELO_TRANSCRICAO";
    public const string VarOpenRouterApiKey = "OPENROUTER_API_KEY";
    public const string VarModeloAnalise = "OPENROUTER_MODELO_ANALISE";
    public const string VarIdioma = "MINUTA_IDIOMA";
    public const string VarFfmpegPath = "FFMPEG_PATH";
    public const string VarGlossario = "MINUTA_GLOSSARIO";

    public const string ModeloTranscricaoPadrao = "whisper-large-v3";
    public const string IdiomaPadrao = "pt";

    public string? GroqApiKey { get; init; }
    public string ModeloTranscricao { get; init; } = ModeloTranscricaoPadrao;
    public string? OpenRouterApiKey { get; init; }

    /// <summary>Sem padrão de propósito: a lista de modelos gratuitos muda com frequência.</summary>
    public string? ModeloAnalise { get; init; }

    /// <summary>Código ISO-639-1 enviado ao Whisper.</summary>
    public string Idioma { get; init; } = IdiomaPadrao;

    /// <summary>Caminho do ffmpeg.exe ou da pasta que o contém. Vazio = procurar no PATH.</summary>
    public string? FfmpegPath { get; init; }

    /// <summary>
    /// Caminho do glossário do Whisper. Vem de MINUTA_GLOSSARIO (relativo à pasta do .env) ou, sem ela,
    /// do glossario.txt ao lado do .env, se existir. Null = sem glossário.
    /// </summary>
    public string? CaminhoGlossario { get; init; }

    /// <summary>Caminho do .env usado, ou null se nenhum foi encontrado.</summary>
    public string? ArquivoEnvUsado { get; init; }

    public static ConfiguracaoMinuta Carregar(string? diretorioInicial = null) =>
        Carregar(diretorioInicial ?? Directory.GetCurrentDirectory(), Environment.GetEnvironmentVariable);

    public static ConfiguracaoMinuta Carregar(string diretorioInicial, Func<string, string?> lerVariavel)
    {
        var caminhoEnv = ArquivoEnv.Localizar(diretorioInicial);
        var doArquivo = caminhoEnv is null
            ? new Dictionary<string, string>()
            : ArquivoEnv.Interpretar(File.ReadAllText(caminhoEnv));

        string? Ler(string nome)
        {
            var valor = lerVariavel(nome);
            if (string.IsNullOrWhiteSpace(valor))
                doArquivo.TryGetValue(nome, out valor);
            return string.IsNullOrWhiteSpace(valor) ? null : valor.Trim();
        }

        var pastaBase = caminhoEnv is null ? diretorioInicial : Path.GetDirectoryName(caminhoEnv)!;
        var glossarioPadrao = Path.Combine(pastaBase, Transcricoes.Glossario.NomeArquivoPadrao);
        var caminhoGlossario = Ler(VarGlossario) is { } g
            ? Path.GetFullPath(g.Trim('"'), pastaBase)
            : File.Exists(glossarioPadrao) ? glossarioPadrao : null;

        return new ConfiguracaoMinuta
        {
            GroqApiKey = Ler(VarGroqApiKey),
            ModeloTranscricao = Ler(VarModeloTranscricao) ?? ModeloTranscricaoPadrao,
            OpenRouterApiKey = Ler(VarOpenRouterApiKey),
            ModeloAnalise = Ler(VarModeloAnalise),
            Idioma = Ler(VarIdioma) ?? IdiomaPadrao,
            FfmpegPath = Ler(VarFfmpegPath),
            CaminhoGlossario = caminhoGlossario,
            ArquivoEnvUsado = caminhoEnv,
        };
    }

    /// <summary>O que falta para rodar a Fase 1. Lista vazia = pronto.</summary>
    public IReadOnlyList<string> Pendencias()
    {
        var pendencias = new List<string>();
        if (GroqApiKey is null)
            pendencias.Add($"{VarGroqApiKey} não definida (necessária para a transcrição).");
        if (OpenRouterApiKey is null)
            pendencias.Add($"{VarOpenRouterApiKey} não definida (necessária para a análise).");
        if (ModeloAnalise is null)
            pendencias.Add($"{VarModeloAnalise} não definido (ex.: nvidia/nemotron-3.5-super-vl:free).");
        return pendencias;
    }
}
