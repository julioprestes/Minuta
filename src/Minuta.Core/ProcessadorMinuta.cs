using Minuta.Core.Analises;
using System.Text;
using Minuta.Core.Configuracao;
using Minuta.Core.Documentos;
using Minuta.Core.Midia;
using Minuta.Core.Transcricoes;

namespace Minuta.Core;

/// <param name="Retranscrever">Ignora a transcrição salva (e, por consequência, a análise).</param>
/// <param name="Reanalisar">Ignora a análise salva e chama o modelo de novo.</param>
/// <param name="Perfil">Formato do documento. Padrão: <see cref="PerfisDocumento.Analise"/>.</param>
public sealed record OpcoesProcessamento(bool Retranscrever = false, bool Reanalisar = false, PerfilDocumento? Perfil = null);

public sealed record ResultadoProcessamento(
    Transcricao Transcricao,
    string CaminhoTranscricaoTexto,
    bool TranscricaoReaproveitada,
    AnaliseVideo Analise,
    string CaminhoAnalise,
    bool AnaliseReaproveitada,
    string CaminhoDocumento);

/// <summary>
/// Pipeline do Minuta, usado pelo console e, depois, pela interface.
/// Vídeo → áudio → transcrição → análise → documento .md.
/// Transcrição e análise são salvas e reaproveitadas para não gastar cota das APIs a cada execução;
/// o documento é sempre refeito (não chama API).
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
        var caminhoAnalise = Path.Combine(pastaSaida, nome + ArquivoAnalise.Sufixo);
        var caminhoDocumento = Path.Combine(pastaSaida, nome + GeradorDocumento.Sufixo);

        // Falha cedo, antes de gastar tempo extraindo e transcrevendo.
        if (config.OpenRouterApiKey is null || config.ModeloAnalise is null)
            throw new MinutaException(
                $"{ConfiguracaoMinuta.VarOpenRouterApiKey} e {ConfiguracaoMinuta.VarModeloAnalise} são necessários para a análise.",
                "Preencha no .env e confira com \"minuta --checar\".");

        var (transcricao, transcricaoReaproveitada) = await ObterTranscricaoAsync(
            video, pastaSaida, caminhoJson, caminhoTexto, opcoes, progresso, cancelamento);

        // Transcrição nova invalida a análise salva.
        var analise = transcricaoReaproveitada && !opcoes.Reanalisar
            ? ArquivoAnalise.Carregar(caminhoAnalise, video, config.ModeloAnalise, RedatorOpenRouter.VersaoPrompt)
            : null;
        var analiseReaproveitada = analise is not null;

        if (analiseReaproveitada)
            progresso?.Report("Análise reaproveitada (use --reanalisar para refazer).");
        else
        {
            progresso?.Report($"Analisando com {config.ModeloAnalise} (OpenRouter)...");
            analise = await new RedatorOpenRouter(http, config.OpenRouterApiKey, config.ModeloAnalise)
                .AnalisarAsync(transcricao, cancelamento);
            ArquivoAnalise.Salvar(analise, caminhoAnalise);
        }

        var perfil = opcoes.Perfil ?? PerfisDocumento.Analise;
        progresso?.Report($"Montando o documento ({perfil.Titulo})...");
        var documento = GeradorDocumento.Gerar(
            analise!, transcricao, perfil, video.Name, DateTime.Now, Path.GetFileName(caminhoTexto));
        File.WriteAllText(caminhoDocumento, documento, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        return new ResultadoProcessamento(
            transcricao, caminhoTexto, transcricaoReaproveitada, analise!, caminhoAnalise, analiseReaproveitada, caminhoDocumento);
    }

    private async Task<(Transcricao, bool Reaproveitada)> ObterTranscricaoAsync(
        FileInfo video, string pastaSaida, string caminhoJson, string caminhoTexto,
        OpcoesProcessamento opcoes, IProgress<string>? progresso, CancellationToken cancelamento)
    {
        var glossario = config.CaminhoGlossario is null ? null : Glossario.Carregar(config.CaminhoGlossario);
        if (glossario is not null)
        {
            progresso?.Report($"Glossário: {glossario.Termos.Count} termo(s) de {config.CaminhoGlossario}.");
            if (glossario.TermosIgnorados.Count > 0)
                progresso?.Report($"Aviso: {glossario.TermosIgnorados.Count} termo(s) não couberam no limite do Whisper "
                    + $"e ficaram de fora: {string.Join(", ", glossario.TermosIgnorados)}.");
        }

        if (!opcoes.Retranscrever && ArquivoTranscricao.Carregar(caminhoJson, video, glossario?.Prompt) is { } salva)
        {
            progresso?.Report("Transcrição reaproveitada (use --retranscrever para refazer).");
            return (salva, true);
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
        var transcricao = await new TranscritorGroq(
                http, config.GroqApiKey, config.ModeloTranscricao, config.Idioma, glossario?.Prompt)
            .TranscreverAsync(audio.Caminho, cancelamento);
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
        return (transcricao, false);
    }
}
