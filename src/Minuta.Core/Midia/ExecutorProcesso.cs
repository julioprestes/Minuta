using System.ComponentModel;
using System.Diagnostics;

namespace Minuta.Core.Midia;

internal sealed record ResultadoProcesso(int CodigoSaida, string Saida, string Erros);

/// <summary>Roda um executável externo (ffmpeg/ffprobe) e captura a saída. Cancelar mata o processo.</summary>
internal static class ExecutorProcesso
{
    public static Task<ResultadoProcesso> ExecutarAsync(
        string executavel, IEnumerable<string> argumentos, CancellationToken cancelamento) =>
        ExecutarAsync(executavel, argumentos, async (saida, ct) => await saida.ReadToEndAsync(ct), cancelamento);

    /// <summary>
    /// Variante que entrega a saída padrão como fluxo de bytes (ex.: quadros crus do ffmpeg), sem guardar tudo
    /// na memória. O texto de <see cref="ResultadoProcesso.Saida"/> fica vazio.
    /// </summary>
    public static Task<ResultadoProcesso> ExecutarLendoBytesAsync(
        string executavel, IEnumerable<string> argumentos, Func<Stream, CancellationToken, Task> lerSaida,
        CancellationToken cancelamento) =>
        ExecutarAsync(executavel, argumentos, async (saida, ct) =>
        {
            await lerSaida(saida.BaseStream, ct);
            return "";
        }, cancelamento);

    private static async Task<ResultadoProcesso> ExecutarAsync(
        string executavel, IEnumerable<string> argumentos, Func<StreamReader, CancellationToken, Task<string>> lerSaida,
        CancellationToken cancelamento)
    {
        var inicio = new ProcessStartInfo(executavel)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argumento in argumentos)
            inicio.ArgumentList.Add(argumento);

        using var processo = new Process { StartInfo = inicio };
        try
        {
            processo.Start();
        }
        catch (Win32Exception ex)
        {
            throw new MinutaException(
                $"Não foi possível executar \"{executavel}\": {ex.Message}",
                "Confira a instalação do FFmpeg com \"minuta --checar\".", ex);
        }

        var erros = processo.StandardError.ReadToEndAsync(cancelamento);
        try
        {
            var saida = await lerSaida(processo.StandardOutput, cancelamento);
            await processo.WaitForExitAsync(cancelamento);
            return new ResultadoProcesso(processo.ExitCode, saida, await erros);
        }
        catch
        {
            // Cancelado ou falha ao ler a saída: não deixa o ffmpeg rodando (ou travado escrevendo) em segundo plano.
            try { processo.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }
    }

    /// <summary>Últimas linhas do stderr, para mensagens de erro curtas.</summary>
    public static string Resumir(string texto, int linhas = 3) =>
        string.Join(" | ", texto.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).TakeLast(linhas));
}
