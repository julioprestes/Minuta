namespace Minuta.Core.Midia;

/// <summary>
/// Validação inicial do arquivo escolhido pelo usuário (existe, não está vazio, extensão conhecida).
/// A confirmação de que há trilha de áudio é feita com o ffprobe, na extração.
/// </summary>
public static class ArquivoDeEntrada
{
    public static readonly IReadOnlySet<string> ExtensoesAceitas = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        // vídeo
        ".mp4", ".mkv", ".webm", ".mov", ".avi", ".wmv", ".m4v",
        // áudio (útil para testar só a transcrição)
        ".mp3", ".m4a", ".wav", ".ogg", ".flac",
    };

    public static FileInfo Validar(string? caminho)
    {
        if (string.IsNullOrWhiteSpace(caminho))
            throw new MinutaException("Nenhum arquivo informado.", "Uso: minuta <caminho-do-video>");

        var arquivo = new FileInfo(caminho.Trim().Trim('"'));

        if (!arquivo.Exists)
            throw new MinutaException($"Arquivo não encontrado: {arquivo.FullName}");

        if (!ExtensoesAceitas.Contains(arquivo.Extension))
            throw new MinutaException(
                $"Formato não suportado: \"{arquivo.Extension}\".",
                "Formatos aceitos: " + string.Join(", ", ExtensoesAceitas.Order()));

        if (arquivo.Length == 0)
            throw new MinutaException($"O arquivo está vazio: {arquivo.FullName}");

        return arquivo;
    }
}
