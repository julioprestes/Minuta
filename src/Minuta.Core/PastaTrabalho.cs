using Minuta.Core.Analises;
using Minuta.Core.Documentos;
using Minuta.Core.Midia;
using Minuta.Core.Transcricoes;
using Minuta.Core.Visao;

namespace Minuta.Core;

/// <summary>
/// Pasta com o que o Minuta gera para chegar ao documento (áudio, transcrição, telas, frames e caches), para que ao
/// lado do vídeo fiquem só os documentos. Uma por vídeo: <c>&lt;base&gt;/&lt;video&gt;.minuta/</c>.
/// </summary>
public static class PastaTrabalho
{
    public const string Sufixo = ".minuta";

    /// <summary>Arquivos que versões anteriores gravavam ao lado do documento.</summary>
    private static readonly string[] SufixosArquivos =
    [
        ArquivoTranscricao.SufixoJson, ArquivoTranscricao.SufixoTexto, ArquivoTranscricao.SufixoRespostaBruta,
        ExtratorAudio.SufixoArquivo, ArquivoAnalise.Sufixo, ArquivoVisao.Sufixo, DocumentoTelas.Sufixo,
    ];

    /// <param name="base">Onde criar a pasta (ex.: a pasta do documento ou uma pasta temporária).</param>
    public static string Caminho(string @base, string nomeVideoSemExtensao) =>
        Path.Combine(@base, nomeVideoSemExtensao + Sufixo);

    /// <summary>
    /// Move para a pasta de trabalho os arquivos de trabalho deixados ao lado do documento por versões anteriores,
    /// para reaproveitar a transcrição e as telas sem gastar cota. Não sobrescreve nada que já esteja lá.
    /// </summary>
    /// <returns>Quantos arquivos ou pastas foram movidos.</returns>
    public static int MoverArquivosAntigos(string pastaAntiga, string pastaTrabalho, string nomeVideoSemExtensao)
    {
        if (Path.GetFullPath(pastaAntiga) == Path.GetFullPath(pastaTrabalho))
            return 0;

        var movidos = 0;
        foreach (var sufixo in SufixosArquivos)
        {
            var origem = Path.Combine(pastaAntiga, nomeVideoSemExtensao + sufixo);
            var destino = Path.Combine(pastaTrabalho, nomeVideoSemExtensao + sufixo);
            if (File.Exists(origem) && !File.Exists(destino))
            {
                Directory.CreateDirectory(pastaTrabalho);
                File.Move(origem, destino);
                movidos++;
            }
        }

        var frames = Path.Combine(pastaAntiga, nomeVideoSemExtensao + ExtratorFrames.SufixoPasta);
        var framesDestino = Path.Combine(pastaTrabalho, nomeVideoSemExtensao + ExtratorFrames.SufixoPasta);
        if (Directory.Exists(frames) && !Directory.Exists(framesDestino))
        {
            Directory.CreateDirectory(pastaTrabalho);
            MoverPasta(frames, framesDestino);
            movidos++;
        }

        return movidos;
    }

    /// <summary>Caminho de <paramref name="caminho"/> visto de <paramref name="pasta"/>, com "/" (para links no Markdown).</summary>
    public static string Relativo(string pasta, string caminho) =>
        Path.GetRelativePath(pasta, caminho).Replace('\\', '/');

    /// <summary>Directory.Move não atravessa volumes (outra unidade); aí copia e apaga.</summary>
    private static void MoverPasta(string origem, string destino)
    {
        if (string.Equals(Path.GetPathRoot(Path.GetFullPath(origem)), Path.GetPathRoot(Path.GetFullPath(destino)),
                StringComparison.OrdinalIgnoreCase))
        {
            Directory.Move(origem, destino);
            return;
        }

        Directory.CreateDirectory(destino);
        foreach (var arquivo in Directory.GetFiles(origem))
            File.Copy(arquivo, Path.Combine(destino, Path.GetFileName(arquivo)));
        Directory.Delete(origem, recursive: true);
    }
}
