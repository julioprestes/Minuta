using Minuta.Core.Configuracao;
using Minuta.Core.Midia;
using Minuta.Core.Transcricoes;

namespace Minuta.Core;

public sealed record OpcoesProcessamento(bool Retranscrever = false);

public sealed record ResultadoProcessamento(
    Transcricao Transcricao,
    string CaminhoTranscricaoJson,
    string CaminhoTranscricaoTexto,
    bool TranscricaoReaproveitada);

/// <summary>
/// Pipeline do Minuta, usado pelo console e, depois, pela interface.
/// Hoje: vídeo → áudio → transcrição. Próximas etapas: análise e documento.
/// </summary>
public sealed class ProcessadorMinuta(ConfiguracaoMinuta config, HttpClient http)
{
    public async Task<ResultadoProcessamento> ProcessarAsync(
        FileInfo video,
        string pastaSaida,
        OpcoesProcessamento opcoes,
        IProgress<string>? progresso = null,
        CancellationToken cancelamento = default)
    {
        var nome = Path.GetFileNameWithoutExtension(video.Name);
        var caminhoJson = Path.Combine(pastaSaida, nome + ArquivoTranscricao.SufixoJson);
        var caminhoTexto = Path.Combine(pastaSaida, nome + ArquivoTranscricao.SufixoTexto);

        // Reaproveitar evita gastar cota da API a cada ajuste nas etapas seguintes.
        if (!opcoes.Retranscrever && ArquivoTranscricao.Carregar(caminhoJson, video) is { } salva)
        {
            progresso?.Report($"Transcrição reaproveitada de {caminhoJson} (use --retranscrever para refazer).");
            return new ResultadoProcessamento(salva, caminhoJson, caminhoTexto, TranscricaoReaproveitada: true);
        }

        if (config.GroqApiKey is null)
            throw new MinutaException(
                $"{ConfiguracaoMinuta.VarGroqApiKey} não definida; ela é necessária para a transcrição.",
                "Crie uma chave em https://console.groq.com/keys e coloque no .env.");

        var ffmpeg = LocalizadorFfmpeg.Localizar(config.FfmpegPath);

        progresso?.Report("Extraindo o áudio...");
        var audio = await new ExtratorAudio(ffmpeg).ExtrairAsync(video, pastaSaida, cancelamento);
        progresso?.Report($"Áudio: {audio.Bytes / 1_000_000.0:N1} MB, duração {(audio.Duracao is { } d ? Transcricao.FormatarTempo(d) : "desconhecida")}.");

        progresso?.Report($"Transcrevendo com {config.ModeloTranscricao} (Groq)...");
        var transcritor = new TranscritorGroq(http, config.GroqApiKey, config.ModeloTranscricao, config.Idioma);
        var transcricao = await transcritor.TranscreverAsync(audio.Caminho, cancelamento);
        transcricao = transcricao with
        {
            Duracao = transcricao.Duracao ?? audio.Duracao,
            Origem = OrigemTranscricao.De(video),
        };

        if (transcricao.Vazia)
            throw new MinutaException(
                "Nenhuma fala foi reconhecida no áudio.",
                $"Ouça {audio.Caminho} para conferir se a narração está audível. Vídeos só com a tela serão tratados na Fase 3.");

        ArquivoTranscricao.Salvar(transcricao, caminhoJson, caminhoTexto);
        return new ResultadoProcessamento(transcricao, caminhoJson, caminhoTexto, TranscricaoReaproveitada: false);
    }
}
