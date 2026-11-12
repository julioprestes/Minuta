using System.ComponentModel;
using System.Diagnostics;

namespace Minuta.Core.Midia;

internal sealed record ResultadoProcesso(int CodigoSaida, string Saida, string Erros);

/// <summary>Roda um executável externo (ffmpeg/ffprobe) e captura a saída. Cancelar mata o processo.</summary>
internal static class ExecutorProcesso
{
    public static async Task<ResultadoProcesso> ExecutarAsync(
        string executavel, IEnumerable<string> argumentos, CancellationToken cancelamento)
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

        var saida = processo.StandardOutput.ReadToEndAsync(cancelamento);
        var erros = processo.StandardError.ReadToEndAsync(cancelamento);
        try
        {
            await processo.WaitForExitAsync(cancelamento);
            return new ResultadoProcesso(processo.ExitCode, await saida, await erros);
        }
        catch (OperationCanceledException)
        {
            try { processo.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }
    }

    /// <summary>Últimas linhas do stderr, para mensagens de erro curtas.</summary>
    public static string Resumir(string texto, int linhas = 3) =>
        string.Join(" | ", texto.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).TakeLast(linhas));
}
