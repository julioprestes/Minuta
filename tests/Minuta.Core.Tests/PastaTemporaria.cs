namespace Minuta.Core.Tests;

/// <summary>Pasta temporária apagada ao fim do teste.</summary>
internal sealed class PastaTemporaria : IDisposable
{
    public string Caminho { get; } = Directory.CreateTempSubdirectory("minuta-teste-").FullName;

    public string CriarArquivo(string nome, string conteudo = "x")
    {
        var caminho = Path.Combine(Caminho, nome);
        Directory.CreateDirectory(Path.GetDirectoryName(caminho)!);
        File.WriteAllText(caminho, conteudo);
        return caminho;
    }

    public void Dispose()
    {
        try { Directory.Delete(Caminho, recursive: true); } catch (IOException) { }
    }
}
