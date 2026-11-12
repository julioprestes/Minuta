namespace Minuta.Core;

/// <summary>
/// Erro esperado do pipeline (arquivo inválido, FFmpeg ausente, falha de rede, resposta inválida da API...).
/// A mensagem é para o usuário final; a dica diz o que ele pode fazer para resolver.
/// </summary>
public class MinutaException(string mensagem, string? dica = null, Exception? causa = null)
    : Exception(mensagem, causa)
{
    public string? Dica { get; } = dica;
}
