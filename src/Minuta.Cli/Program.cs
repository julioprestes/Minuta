using System.Text;
using Minuta.Core;
using Minuta.Core.Analises;
using Minuta.Core.Configuracao;
using Minuta.Core.Documentos;
using Minuta.Core.Midia;
using Minuta.Core.Transcricoes;

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
    var opcoes = new OpcoesProcessamento(
        Retranscrever: args.Contains("--retranscrever"),
        Reanalisar: args.Contains("--reanalisar"),
        RefazerFrames: args.Contains("--refazer-frames"),
        Redescrever: args.Contains("--redescrever"),
        SemVisao: args.Contains("--sem-visao"),
        Contexto: LerOpcao(args, "--contexto"),
        Perfil: PerfisDocumento.Obter(LerOpcao(args, "--perfil") ?? PerfisDocumento.Analise.Id));
    var config = ConfiguracaoMinuta.Carregar();
    opcoes = opcoes with
    {
        BasePastaTrabalho = LerOpcao(args, "--trabalho") is { } trabalho ? Path.GetFullPath(trabalho) : config.PastaTrabalho,
    };

    Console.WriteLine($"Vídeo: {video.FullName} ({Megabytes(video.Length)})");
    Console.WriteLine($"Documento em: {pastaSaida}");
    Console.WriteLine();

    using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
    var resultado = await new ProcessadorMinuta(config, http)
        .ProcessarAsync(video, pastaSaida, opcoes, new ProgressoNoConsole(), cancelamento);

    var t = resultado.Transcricao;
    Console.WriteLine();
    Console.WriteLine($"Transcrição: {t.Segmentos.Count} trechos, duração {FormatarDuracao(t.Duracao)}"
        + (t.SegmentosDescartados > 0 ? $", {t.SegmentosDescartados} trecho(s) de silêncio/ruído descartado(s)" : "")
        + (t.Segmentos.Any(s => s.BaixaConfianca) ? $", {t.Segmentos.Count(s => s.BaixaConfianca)} com baixa confiança (?)" : "")
        + ".");
    Console.WriteLine($"  {resultado.CaminhoTranscricaoTexto}");

    if (resultado.Frames is { } f)
    {
        Console.WriteLine();
        Console.WriteLine($"Telas: {f.Frames.Count} frame(s) em {f.Pasta}");
        if (resultado.Visao is { } v)
        {
            var vazias = v.Telas.Count(tela => tela.Vazia);
            Console.WriteLine($"  Descritas por {v.Modelo}: {v.TelasDistintas().Count} tela(s) distinta(s)"
                + (vazias > 0 ? $", {vazias} frame(s) sem descrição" : "") + ".");
            Console.WriteLine($"  {resultado.CaminhoTelas}");
        }
    }

    MostrarAnalise(resultado.Analise, resultado.CaminhoAnalise);

    Console.WriteLine();
    Console.WriteLine($"Arquivos de trabalho (transcrição, telas, caches): {resultado.PastaTrabalho}");
    Console.WriteLine();
    Console.WriteLine($"Documento ({opcoes.Perfil!.Titulo}):");
    Console.WriteLine($"  {resultado.CaminhoDocumento}");
    var outros = PerfisDocumento.Todos.Where(p => p != opcoes.Perfil).Select(p => p.Id);
    Console.WriteLine($"  Outros formatos, sem gastar API: --perfil {string.Join(" | ", outros)}");
    return 0;
}

// O conteúdo vai para o .md; aqui só a contagem e o que precisa de atenção.
static void MostrarAnalise(AnaliseVideo a, string caminho)
{
    Console.WriteLine();
    Console.WriteLine($"Análise ({a.Modelo}):");
    Console.WriteLine($"  {caminho}");
    Console.WriteLine($"  {a.PassosParaReproduzir.Count} passo(s), {a.RegrasNegocio.Count} regra(s), {a.Decisoes.Count} decisão(ões), "
        + $"{a.DuvidasEmAberto.Count} dúvida(s), {a.Cards.Count} card(s), {a.ProximosPassos.Count} próximo(s) passo(s).");

    var citacoes = a.TodasAsCitacoes.ToList();
    var naoConferidas = citacoes.Where(c => !c.Conferida).ToList();
    Console.WriteLine($"  Citações conferidas no vídeo: {citacoes.Count - naoConferidas.Count} de {citacoes.Count}"
        + $" ({citacoes.Count(c => c.DaTela)} da tela).");
    foreach (var c in naoConferidas)
        Console.WriteLine($"  ⚠ não encontrada: \"{c.Trecho}\"");
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
    Console.WriteLine($"  {ConfiguracaoMinuta.VarModeloVisao,-26}{config.ModeloVisao ?? (config.ModeloAnalise is null ? "(não definido)" : "(o mesmo da análise)")}");
    Console.WriteLine($"  {ConfiguracaoMinuta.VarIdioma,-26}{config.Idioma}");
    Console.WriteLine($"  {"Glossário",-26}{DescreverGlossario(config.CaminhoGlossario)}");
    Console.WriteLine($"  {ConfiguracaoMinuta.VarPastaTrabalho,-26}{config.PastaTrabalho ?? "(ao lado do documento)"}");

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

static string DescreverGlossario(string? caminho)
{
    if (caminho is null)
        return $"(nenhum; opcional, crie {Glossario.NomeArquivoPadrao} ao lado do .env)";
    if (!File.Exists(caminho))
        return $"NÃO ENCONTRADO — {caminho}";
    var g = Glossario.Carregar(caminho);
    return $"{caminho} ({g.Termos.Count} termo(s)"
        + (g.TermosIgnorados.Count > 0 ? $", {g.TermosIgnorados.Count} fora do limite" : "") + ")";
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
        Minuta — transforma vídeos de sistema (análise, relato de erro) em documento .md para o desenvolvimento.

        Uso:
          minuta <video> [opções]   processa o vídeo
          minuta --checar           confere chaves, modelos e FFmpeg
          minuta --ajuda            mostra esta ajuda

        Opções:
          --perfil <nome>     formato do documento (video.<perfil>.md); trocar não gasta API:
                                analise  regras, cards e critérios, para o desenvolvimento (padrão)
                                resumo   o que o vídeo mostra, o que se pede e as dúvidas, para ler sem assistir
                                ata      resumo, decisões e próximos passos de reunião
          --contexto "<texto>" o que quem enviou o vídeo disse (ex.: o chamado); orienta, mas não é citado
          --saida <pasta>     onde gravar o documento (padrão: pasta do vídeo)
          --trabalho <pasta>  onde criar a pasta de trabalho <video>.minuta, com transcrição, telas e caches
                              (padrão: ao lado do documento; ou MINUTA_PASTA_TRABALHO no .env)
          --retranscrever     ignora a transcrição já salva e transcreve de novo
          --reanalisar        ignora a análise já salva e chama o modelo de novo
          --refazer-frames    escolhe de novo os frames da tela
          --redescrever       descreve as telas de novo (chama o modelo de visão)
          --sem-visao         ignora a tela: nenhuma imagem sai da máquina
        """);
}

/// <summary>Escreve o progresso na hora, na ordem (o Progress&lt;T&gt; padrão pode trocar a ordem no console).</summary>
sealed class ProgressoNoConsole : IProgress<string>
{
    public void Report(string valor) => Console.WriteLine($"• {valor}");
}
