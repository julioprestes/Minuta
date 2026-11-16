using Minuta.Core.Analises;
using System.Text;
using Minuta.Core.Configuracao;
using Minuta.Core.Documentos;
using Minuta.Core.Midia;
using Minuta.Core.Transcricoes;
using Minuta.Core.Visao;

namespace Minuta.Core;

/// <param name="Retranscrever">Ignora a transcrição salva (e, por consequência, a análise).</param>
/// <param name="Reanalisar">Ignora a análise salva e chama o modelo de novo.</param>
/// <param name="Perfil">Formato do documento. Padrão: <see cref="PerfisDocumento.Analise"/>.</param>
/// <param name="RefazerFrames">Ignora os frames já salvos e escolhe de novo.</param>
/// <param name="Redescrever">Ignora as descrições das telas salvas e chama o modelo de visão de novo.</param>
/// <param name="SemVisao">Pula a trilha visual (nenhuma imagem sai da máquina; não gasta cota com as telas).</param>
/// <param name="Contexto">Texto de quem enviou o vídeo (ex.: descrição do chamado). Orienta a análise, mas não é citado.</param>
/// <param name="BasePastaTrabalho">Onde criar a pasta de trabalho do vídeo. Null = ao lado do documento.</param>
public sealed record OpcoesProcessamento(
    bool Retranscrever = false, bool Reanalisar = false, PerfilDocumento? Perfil = null, bool RefazerFrames = false,
    bool Redescrever = false, bool SemVisao = false, string? Contexto = null, string? BasePastaTrabalho = null);

/// <param name="Frames">Frames escolhidos da tela; null se o arquivo não tem imagem (só áudio) ou com --sem-visao.</param>
/// <param name="Visao">Descrição das telas; null quando não há frames.</param>
/// <param name="CaminhoTelas">Lista das telas (<c>.telas.md</c>); null quando não há telas descritas.</param>
/// <param name="PastaTrabalho">Pasta com a transcrição, as telas, os frames e os caches deste vídeo.</param>
public sealed record ResultadoProcessamento(
    Transcricao Transcricao,
    string CaminhoTranscricaoTexto,
    bool TranscricaoReaproveitada,
    AnaliseVideo Analise,
    string CaminhoAnalise,
    bool AnaliseReaproveitada,
    string CaminhoDocumento,
    FramesExtraidos? Frames,
    DescricaoVisual? Visao,
    string? CaminhoVisao,
    string? CaminhoTelas,
    string PastaTrabalho);

/// <summary>
/// Pipeline do Minuta, usado pelo console e, depois, pela interface.
/// Vídeo → frames e descrição das telas → áudio e transcrição → linha do tempo → análise → documento .md.
/// Transcrição, telas e análise são salvas (na pasta de trabalho) e reaproveitadas para não gastar cota das APIs a
/// cada execução; o documento é sempre refeito (não chama API).
/// </summary>
public sealed class ProcessadorMinuta(ConfiguracaoMinuta config, HttpClient http)
{
    /// <param name="pastaSaida">Onde gravar o documento. O resto vai para a pasta de trabalho.</param>
    public async Task<ResultadoProcessamento> ProcessarAsync(
        FileInfo video,
        string pastaSaida,
        OpcoesProcessamento opcoes,
        IProgress<string>? progresso = null,
        CancellationToken cancelamento = default)
    {
        var nome = Path.GetFileNameWithoutExtension(video.Name);
        var perfil = opcoes.Perfil ?? PerfisDocumento.Analise;
        var caminhoDocumento = Path.Combine(pastaSaida, perfil.NomeArquivo(nome));

        var trabalho = Core.PastaTrabalho.Caminho(opcoes.BasePastaTrabalho ?? pastaSaida, nome);
        Directory.CreateDirectory(trabalho);
        var movidos = Core.PastaTrabalho.MoverArquivosAntigos(pastaSaida, trabalho, nome);
        if (movidos > 0)
            progresso?.Report($"{movidos} arquivo(s) de trabalho de uma versão anterior movido(s) para {trabalho}.");

        var caminhoJson = Path.Combine(trabalho, nome + ArquivoTranscricao.SufixoJson);
        var caminhoTexto = Path.Combine(trabalho, nome + ArquivoTranscricao.SufixoTexto);
        var caminhoAnalise = Path.Combine(trabalho, nome + ArquivoAnalise.Sufixo);

        // Falha cedo, antes de gastar tempo extraindo e transcrevendo.
        if (config.OpenRouterApiKey is null || config.ModeloAnalise is null)
            throw new MinutaException(
                $"{ConfiguracaoMinuta.VarOpenRouterApiKey} e {ConfiguracaoMinuta.VarModeloAnalise} são necessários para a análise.",
                "Preencha no .env e confira com \"minuta --checar\".");

        var ffmpeg = LocalizadorFfmpeg.Localizar(config.FfmpegPath);
        var info = await new ExtratorAudio(ffmpeg).InspecionarAsync(video.FullName, cancelamento);
        if (!info.TemAudio && (!info.TemVideo || opcoes.SemVisao))
            throw new MinutaException(
                $"O arquivo não tem áudio{(info.TemVideo ? "" : " nem imagem")}: {video.Name}",
                info.TemVideo ? "Sem áudio, o Minuta depende das telas: rode sem --sem-visao." : null);

        // Trilha visual: frames da tela (local) e a descrição de cada um (modelo de visão).
        var frames = opcoes.SemVisao || !info.TemVideo
            ? null
            : await ObterFramesAsync(video, ffmpeg, trabalho, opcoes, progresso, cancelamento);
        var caminhoVisao = Path.Combine(trabalho, nome + ArquivoVisao.Sufixo);
        var visao = frames is null || frames.Frames.Count == 0
            ? null
            : await ObterVisaoAsync(video, frames, caminhoVisao, opcoes, progresso, cancelamento);

        // Trilha de áudio.
        var (transcricao, transcricaoReaproveitada) = await ObterTranscricaoAsync(
            video, ffmpeg, info, trabalho, caminhoJson, caminhoTexto, opcoes, progresso, cancelamento);
        if (transcricao.Vazia)
        {
            if (visao is null || visao.Telas.All(t => t.Vazia))
                throw new MinutaException(
                    "Nenhuma fala foi reconhecida e não há telas descritas para analisar.",
                    info.TemAudio ? "Ouça o .audio.ogg para conferir se a narração está audível." : null);
            if (info.TemAudio)
                progresso?.Report("Nenhuma fala reconhecida: a análise usa só as telas.");
        }

        // A análise salva só vale para a mesma entrada (falas, telas e contexto).
        var assinatura = RedatorOpenRouter.AssinaturaDaEntrada(transcricao, visao, opcoes.Contexto);
        var analise = opcoes.Reanalisar
            ? null
            : ArquivoAnalise.Carregar(caminhoAnalise, video, config.ModeloAnalise, RedatorOpenRouter.VersaoPrompt, assinatura);
        var analiseReaproveitada = analise is not null;

        if (analiseReaproveitada)
            progresso?.Report("Análise reaproveitada (use --reanalisar para refazer).");
        else
        {
            progresso?.Report($"Analisando com {config.ModeloAnalise} (OpenRouter)...");
            analise = await new RedatorOpenRouter(http, config.OpenRouterApiKey, config.ModeloAnalise)
                .AnalisarAsync(transcricao with { Origem = OrigemTranscricao.De(video) }, visao, opcoes.Contexto, cancelamento);
            ArquivoAnalise.Salvar(analise, caminhoAnalise);
        }

        progresso?.Report($"Montando o documento ({perfil.Titulo})...");
        var agora = DateTime.Now;
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        // A lista das telas fica na pasta de trabalho, ao lado dos frames, para não poluir o documento.
        string? caminhoTelas = null;
        if (visao is not null)
        {
            caminhoTelas = Path.Combine(trabalho, nome + DocumentoTelas.Sufixo);
            var framesVistosDaLista = frames is null ? null : Core.PastaTrabalho.Relativo(trabalho, frames.Pasta);
            File.WriteAllText(caminhoTelas, DocumentoTelas.Gerar(visao, video.Name, framesVistosDaLista, agora), utf8);
        }

        // Os caminhos no documento são relativos a ele, para os links funcionarem e a pasta poder ser movida junto.
        Directory.CreateDirectory(pastaSaida);
        string Rel(string caminho) => Core.PastaTrabalho.Relativo(pastaSaida, caminho);
        var documento = GeradorDocumento.Gerar(analise!, transcricao, perfil, video.Name, agora, new ExtrasDocumento
        {
            ArquivoTranscricao = transcricao.Vazia ? null : Rel(caminhoTexto),
            Visao = visao,
            PastaFrames = frames is null ? null : Rel(frames.Pasta),
            ArquivoTelas = caminhoTelas is null ? null : Rel(caminhoTelas),
            Contexto = opcoes.Contexto,
        });
        File.WriteAllText(caminhoDocumento, documento, utf8);

        return new ResultadoProcessamento(
            transcricao, caminhoTexto, transcricaoReaproveitada, analise!, caminhoAnalise, analiseReaproveitada, caminhoDocumento,
            frames, visao, visao is null ? null : caminhoVisao, caminhoTelas, trabalho);
    }

    /// <summary>Transcrição vazia para vídeos sem trilha de áudio (só a tela).</summary>
    private Transcricao SemAudio(FileInfo video, InfoMidia info) =>
        new("(sem áudio)", config.Idioma, info.Duracao, []) { Origem = OrigemTranscricao.De(video) };

    private async Task<DescricaoVisual> ObterVisaoAsync(
        FileInfo video, FramesExtraidos frames, string caminho, OpcoesProcessamento opcoes,
        IProgress<string>? progresso, CancellationToken cancelamento)
    {
        var modelo = config.ModeloVisaoEfetivo!; // a análise já exige o modelo, então aqui ele existe
        if (!opcoes.Redescrever
            && ArquivoVisao.Carregar(caminho, video, modelo, DescritorFramesOpenRouter.VersaoPrompt, frames.Frames) is { } salva)
        {
            progresso?.Report("Descrição das telas reaproveitada (use --redescrever para refazer).");
            return salva;
        }

        var visao = await new DescritorFramesOpenRouter(http, config.OpenRouterApiKey!, modelo)
            .DescreverAsync(frames, progresso, cancelamento) with { Origem = OrigemTranscricao.De(video) };
        ArquivoVisao.Salvar(visao, caminho);
        return visao;
    }

    private static async Task<FramesExtraidos> ObterFramesAsync(
        FileInfo video, FerramentasFfmpeg ffmpeg, string pastaSaida, OpcoesProcessamento opcoes,
        IProgress<string>? progresso, CancellationToken cancelamento)
    {
        progresso?.Report("Escolhendo os frames da tela...");
        var frames = await new ExtratorFrames(ffmpeg).ExtrairAsync(
            video, pastaSaida, refazer: opcoes.RefazerFrames, cancelamento: cancelamento);
        progresso?.Report(frames.Reaproveitados
            ? $"Frames reaproveitados: {frames.Frames.Count} (use --refazer-frames para escolher de novo)."
            : $"Frames: {frames.Frames.Count} salvos.");
        return frames;
    }

    /// <summary>Transcrição salva ou nova. Pode voltar vazia (sem fala); quem chama decide se isso é erro.</summary>
    private async Task<(Transcricao, bool Reaproveitada)> ObterTranscricaoAsync(
        FileInfo video, FerramentasFfmpeg ffmpeg, InfoMidia info, string pastaSaida, string caminhoJson, string caminhoTexto,
        OpcoesProcessamento opcoes, IProgress<string>? progresso, CancellationToken cancelamento)
    {
        if (!info.TemAudio)
        {
            progresso?.Report("O vídeo não tem áudio: a análise usa só as telas.");
            return (SemAudio(video, info), false);
        }

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

        progresso?.Report("Extraindo o áudio...");
        var audio = await new ExtratorAudio(ffmpeg).ExtrairAsync(video, pastaSaida, cancelamento);
        progresso?.Report($"Áudio: {audio.Bytes / 1_000_000.0:N1} MB, duração {(audio.Duracao is { } d ? Transcricao.FormatarTempo(d) : "desconhecida")}.");

        // Resposta do Groq como chegou, para diagnosticar erros de transcrição (ex.: texto cortado).
        var caminhoRespostaBruta = Path.Combine(pastaSaida,
            Path.GetFileNameWithoutExtension(video.Name) + ArquivoTranscricao.SufixoRespostaBruta);

        progresso?.Report($"Transcrevendo com {config.ModeloTranscricao} (Groq)...");
        var transcricao = await new TranscritorGroq(
                http, config.GroqApiKey, config.ModeloTranscricao, config.Idioma, glossario?.Prompt)
            .TranscreverAsync(audio.Caminho, caminhoRespostaBruta, cancelamento);
        transcricao = transcricao with
        {
            Duracao = transcricao.Duracao ?? audio.Duracao,
            Origem = OrigemTranscricao.De(video),
        };

        ArquivoTranscricao.Salvar(transcricao, caminhoJson, caminhoTexto);
        return (transcricao, false);
    }
}
