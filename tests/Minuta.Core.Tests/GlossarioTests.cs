using System.Net;
using Minuta.Core.Configuracao;
using Minuta.Core.Transcricoes;

namespace Minuta.Core.Tests;

public class GlossarioTests
{
    private static readonly Func<string, string?> SemVariaveis = _ => null;

    [Fact]
    public void Ignora_comentarios_linhas_vazias_e_repetidos()
    {
        var g = Glossario.Interpretar("# comentário\r\nnota fiscal\r\n\r\n  CFOP  \nNota Fiscal\npedido de venda\n");

        Assert.Equal(["nota fiscal", "CFOP", "pedido de venda"], g.Termos);
        Assert.Empty(g.TermosIgnorados);
        Assert.Equal("Glossário: nota fiscal, CFOP, pedido de venda.", g.Prompt);
    }

    [Fact]
    public void Sem_termos_nao_gera_prompt()
    {
        Assert.Null(Glossario.Interpretar("# só comentário\n\n").Prompt);
    }

    [Fact]
    public void Termos_alem_do_limite_ficam_de_fora()
    {
        var linhas = Enumerable.Range(1, 100).Select(i => $"termo número {i:000}");

        var g = Glossario.Interpretar(string.Join('\n', linhas));

        Assert.NotEmpty(g.TermosIgnorados);
        Assert.Equal(100, g.Termos.Count + g.TermosIgnorados.Count);
        Assert.True(g.Prompt!.Length <= Glossario.LimiteCaracteres);
        Assert.Equal("termo número 001", g.Termos[0]); // mantém a ordem do arquivo
    }

    [Fact]
    public void Arquivo_inexistente_gera_erro_claro()
    {
        var ex = Assert.Throws<MinutaException>(() => Glossario.Carregar(@"C:\nao\existe\glossario.txt"));
        Assert.Contains("Glossário não encontrado", ex.Message);
    }

    [Fact]
    public void Configuracao_usa_glossario_ao_lado_do_env_se_existir()
    {
        using var temp = new PastaTemporaria();
        temp.CriarArquivo(".env", "GROQ_API_KEY=g");

        Assert.Null(ConfiguracaoMinuta.Carregar(temp.Caminho, SemVariaveis).CaminhoGlossario);

        var caminho = temp.CriarArquivo(Glossario.NomeArquivoPadrao, "CFOP");
        Assert.Equal(caminho, ConfiguracaoMinuta.Carregar(temp.Caminho, SemVariaveis).CaminhoGlossario);
    }

    [Fact]
    public void Configuracao_resolve_caminho_relativo_a_pasta_do_env()
    {
        using var temp = new PastaTemporaria();
        temp.CriarArquivo(".env", "MINUTA_GLOSSARIO=termos/meu.txt");
        var subpasta = Directory.CreateDirectory(Path.Combine(temp.Caminho, "outra")).FullName;

        var config = ConfiguracaoMinuta.Carregar(subpasta, SemVariaveis);

        Assert.Equal(Path.Combine(temp.Caminho, "termos", "meu.txt"), config.CaminhoGlossario);
    }

    [Fact]
    public async Task Transcritor_envia_o_prompt_e_registra_o_glossario()
    {
        using var temp = new PastaTemporaria();
        var handler = new HandlerFalso(() => HandlerFalso.Json(HttpStatusCode.OK,
            """{"text":"Emiti a nota fiscal.","duration":2.0,"segments":[{"start":0,"end":2,"text":"Emiti a nota fiscal.","avg_logprob":-0.1,"no_speech_prob":0.01,"compression_ratio":1.0}]}"""));
        var transcritor = new TranscritorGroq(new HttpClient(handler), "chave", "whisper-large-v3", "pt", "Glossário: CFOP.");

        var t = await transcritor.TranscreverAsync(temp.CriarArquivo("a.audio.ogg"));

        Assert.Contains("name=prompt", handler.UltimoCorpo);
        Assert.Contains("Glossário: CFOP.", handler.UltimoCorpo);
        Assert.Equal("Glossário: CFOP.", t.Glossario);
    }

    [Fact]
    public async Task Sem_glossario_nao_envia_prompt()
    {
        using var temp = new PastaTemporaria();
        var handler = new HandlerFalso(() => HandlerFalso.Json(HttpStatusCode.OK,
            """{"text":"Oi.","duration":1.0,"segments":[{"start":0,"end":1,"text":"Oi.","avg_logprob":-0.1,"no_speech_prob":0.01,"compression_ratio":1.0}]}"""));

        await new TranscritorGroq(new HttpClient(handler), "chave", "whisper-large-v3", "pt")
            .TranscreverAsync(temp.CriarArquivo("a.audio.ogg"));

        Assert.DoesNotContain("name=prompt", handler.UltimoCorpo);
    }

    [Fact]
    public void Transcricao_salva_com_outro_glossario_nao_e_reaproveitada()
    {
        using var temp = new PastaTemporaria();
        var video = new FileInfo(temp.CriarArquivo("reuniao.mp4", "conteúdo"));
        var json = Path.Combine(temp.Caminho, "reuniao" + ArquivoTranscricao.SufixoJson);
        var transcricao = new Transcricao("whisper-large-v3", "pt", null,
            [new SegmentoTranscricao(TimeSpan.Zero, TimeSpan.FromSeconds(2), "Emiti a nota fiscal.")])
        { Origem = OrigemTranscricao.De(video), Glossario = "Glossário: CFOP." };
        ArquivoTranscricao.Salvar(transcricao, json, Path.Combine(temp.Caminho, "r.txt"));

        Assert.NotNull(ArquivoTranscricao.Carregar(json, video, "Glossário: CFOP."));
        Assert.Null(ArquivoTranscricao.Carregar(json, video, "Glossário: CFOP, boleto."));
        Assert.Null(ArquivoTranscricao.Carregar(json, video, null));
    }
}
