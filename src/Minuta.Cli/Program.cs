using System.Text;
using Minuta.Core;
using Minuta.Core.Configuracao;
using Minuta.Core.Midia;

Console.OutputEncoding = Encoding.UTF8;

using var cancelamento = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true; // deixa o programa encerrar limpo (mata o ffmpeg, se estiver rodando)
    cancelamento.Cancel();
};

try
{
    return await ExecutarAsync(args, cancelamento.Token);
}
catch (MinutaException ex)
{
    Console.Error.WriteLine($"Erro: {ex.Message}");
    if (ex.Dica is not null)
        Console.Error.WriteLine($"Dica: {ex.Dica}");
    return 1;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Cancelado.");
    return 130;
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
{
    Console.Error.WriteLine($"Erro de arquivo: {ex.Message}");
    return 1;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Erro inesperado: {ex.Message}");
    return 2;
}

static async Task<int> ExecutarAsync(string[] args, CancellationToken cancelamento)
{
    if (args.Length == 0 || args[0] is "-h" or "--ajuda" or "/?")
    {
        MostrarAjuda();
        return args.Length == 0 ? 1 : 0;
    }

    if (args[0] == "--checar")
        return Checar();

    var video = ArquivoDeEntrada.Validar(args[0]);
    var pastaSaida = Path.GetFullPath(LerOpcao(args, "--saida") ?? video.DirectoryName!);
    var config = ConfiguracaoMinuta.Carregar();
    var ffmpeg = LocalizadorFfmpeg.Localizar(config.FfmpegPath);

    Console.WriteLine($"Vídeo: {video.FullName} ({Megabytes(video.Length)})");
    Console.WriteLine($"Saída: {pastaSaida}");
    Console.WriteLine();

    Console.WriteLine("[1/3] Extraindo o áudio...");
    var audio = await new ExtratorAudio(ffmpeg).ExtrairAsync(video, pastaSaida, cancelamento);
    Console.WriteLine($"      {audio.Caminho}");
    Console.WriteLine($"      {Megabytes(audio.Bytes)}, duração {FormatarDuracao(audio.Duracao)}");

    Console.WriteLine("[2/3] Transcrição: ainda não implementada (Fase 1, etapa 3).");
    Console.WriteLine("[3/3] Análise e documento: ainda não implementados (Fase 1, etapas 4 e 5).");

    var pendencias = config.Pendencias();
    if (pendencias.Count > 0)
        Console.WriteLine($"\nAviso: a configuração tem {pendencias.Count} pendência(s) para as próximas etapas. Rode \"minuta --checar\".");

    return 0;
}

static int Checar()
{
    var config = ConfiguracaoMinuta.Carregar();

    Console.WriteLine("Configuração do Minuta");
    Console.WriteLine($"  Arquivo .env:          {config.ArquivoEnvUsado ?? "(não encontrado; usando só variáveis de ambiente)"}");
    Console.WriteLine($"  {ConfiguracaoMinuta.VarGroqApiKey,-26}{Status(config.GroqApiKey)}");
    Console.WriteLine($"  {ConfiguracaoMinuta.VarModeloTranscricao,-26}{config.ModeloTranscricao}");
    Console.WriteLine($"  {ConfiguracaoMinuta.VarOpenRouterApiKey,-26}{Status(config.OpenRouterApiKey)}");
    Console.WriteLine($"  {ConfiguracaoMinuta.VarModeloAnalise,-26}{config.ModeloAnalise ?? "(não definido)"}");
    Console.WriteLine($"  {ConfiguracaoMinuta.VarIdioma,-26}{config.Idioma}");

    var ok = true;
    try
    {
        var ffmpeg = LocalizadorFfmpeg.Localizar(config.FfmpegPath);
        Console.WriteLine($"  {"FFmpeg",-26}{ffmpeg.Ffmpeg}");
    }
    catch (MinutaException ex)
    {
        ok = false;
        Console.WriteLine($"  {"FFmpeg",-26}NÃO ENCONTRADO — {ex.Message}");
        if (ex.Dica is not null)
            Console.WriteLine($"  {"",-26}{ex.Dica}");
    }

    var pendencias = config.Pendencias();
    Console.WriteLine();
    if (pendencias.Count > 0)
    {
        ok = false;
        Console.WriteLine("Pendências:");
        foreach (var p in pendencias)
            Console.WriteLine($"  - {p}");
    }

    Console.WriteLine(ok ? "Tudo pronto." : "Há pendências (veja acima).");
    return ok ? 0 : 1;
}

// Nunca exibe a chave, nem parte dela.
static string Status(string? chave) => chave is null ? "(não definida)" : "definida";

static string Megabytes(long bytes) => $"{bytes / 1_000_000.0:N1} MB";

static string FormatarDuracao(TimeSpan? duracao) =>
    duracao is null ? "desconhecida" : duracao.Value.ToString(duracao.Value.TotalHours >= 1 ? @"h\:mm\:ss" : @"mm\:ss");

static string? LerOpcao(string[] args, string nome)
{
    var i = Array.IndexOf(args, nome);
    if (i < 0)
        return null;
    if (i + 1 >= args.Length || args[i + 1].StartsWith("--"))
        throw new MinutaException($"A opção {nome} precisa de um valor.");
    return args[i + 1];
}

static void MostrarAjuda()
{
    Console.WriteLine("""
        Minuta — transforma vídeos de reunião em documento .md para o desenvolvimento.

        Uso:
          minuta <video> [--saida <pasta>]   processa o vídeo (padrão: saída na pasta do vídeo)
          minuta --checar                    confere chaves, modelos e FFmpeg
          minuta --ajuda                     mostra esta ajuda
        """);
}
