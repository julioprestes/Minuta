using Minuta.Core.Analises;
using Minuta.Core.Configuracao;
using Minuta.Core.Documentos;
using Minuta.Core.Midia;
using Minuta.Core.Transcricoes;

namespace Minuta.Core.Tests;

public class PerfilResumoTests
{
    private static readonly DateTime Agora = new(2026, 11, 16, 16, 0, 0);

    private static Citacao Ok(int segundos, string trecho) => new(trecho, TimeSpan.FromSeconds(segundos), true);

    /// <summary>Análise fictícia de um pedido de melhoria (sistema de pedidos inventado).</summary>
    private static AnaliseVideo Analise(bool comCards = true) => new(
        Resumo: "O vídeo mostra a tela 'Novo pedido'. O cliente pede que o total apareça com desconto. Ficou em aberto o arredondamento.",
        Decisoes: [],
        RegrasNegocio: [new("O sistema deve mostrar o total com desconto.", [Ok(20, "o cliente deseja ver o total com desconto")])],
        DuvidasEmAberto: [new("Como arredondar o desconto?", [])],
        PassosParaReproduzir: [],
        ProximosPassos: [],
        Cards: comCards
            ? [new("Mostrar total com desconto no 'Novo pedido'", "Exibir o total já com desconto.",
                ["O total considera o desconto."], [Ok(20, "o cliente deseja ver o total com desconto")])]
            : [])
    { Modelo = "modelo/teste:free" };

    private static string Gerar(AnaliseVideo analise) =>
        GeradorDocumento.Gerar(analise, Exemplos.Transcricao(), PerfisDocumento.Resumo, "pedidos.mp4", Agora,
            new ExtrasDocumento { ArquivoTranscricao = "pedidos.minuta/pedidos.transcricao.txt" });

    [Fact]
    public void Resumo_explica_lista_os_pedidos_e_as_duvidas_sem_criterios_de_aceite()
    {
        var md = Gerar(Analise());

        Assert.StartsWith("# pedidos — Resumo", md);
        Assert.Contains("## O que o vídeo mostra\n\nO vídeo mostra a tela 'Novo pedido'.", md.ReplaceLineEndings("\n"));
        Assert.Contains("## O que se pede\n\n- Mostrar total com desconto no 'Novo pedido' [00:20]", md.ReplaceLineEndings("\n"));
        Assert.Contains("- Como arredondar o desconto? *(não dito no vídeo)*", md);
        Assert.DoesNotContain("Critérios de aceite", md);
        Assert.DoesNotContain("## Regras de negócio", md);
        Assert.DoesNotContain("## Passos para reproduzir", md); // sem erro, sem passos
        Assert.Contains("Transcrição completa: `pedidos.minuta/pedidos.transcricao.txt`.", md);
    }

    [Fact]
    public void Sem_cards_o_que_se_pede_vem_das_regras()
    {
        var md = Gerar(Analise(comCards: false));

        Assert.Contains("- O sistema deve mostrar o total com desconto. [00:20]", md);
    }

    [Fact]
    public void Cada_perfil_tem_o_proprio_arquivo()
    {
        Assert.Equal("video.resumo.md", PerfisDocumento.Resumo.NomeArquivo("video"));
        Assert.Equal("video.analise.md", PerfisDocumento.Analise.NomeArquivo("video"));
        Assert.Equal("video.ata.md", PerfisDocumento.Ata.NomeArquivo("video"));
        Assert.Same(PerfisDocumento.Resumo, PerfisDocumento.Obter(" Resumo "));
    }

    [Fact]
    public void Prompt_pede_resumo_explicativo_e_duvidas_uteis()
    {
        Assert.Contains("a explicação que permite entender o caso sem ver o vídeo", RedatorOpenRouter.InstrucoesDoSistema);
        Assert.Contains("NÃO pergunte sobre pessoas, nomes, saudações", RedatorOpenRouter.InstrucoesDoSistema);
    }
}

public class PastaTrabalhoTests
{
    [Fact]
    public void Move_os_arquivos_antigos_para_a_pasta_de_trabalho_sem_sobrescrever()
    {
        using var temp = new PastaTemporaria();
        temp.CriarArquivo("v" + ArquivoTranscricao.SufixoJson, "transcrição antiga");
        temp.CriarArquivo("v" + ArquivoAnalise.Sufixo, "análise antiga");
        temp.CriarArquivo(Path.Combine("v" + ExtratorFrames.SufixoPasta, "001_00-00.jpg"), "imagem");
        temp.CriarArquivo("v.minuta.md", "documento antigo");      // documento do usuário: fica onde está
        temp.CriarArquivo("outro" + ArquivoAnalise.Sufixo, "x");   // de outro vídeo: fica onde está
        var trabalho = PastaTrabalho.Caminho(temp.Caminho, "v");
        temp.CriarArquivo(Path.Combine("v" + PastaTrabalho.Sufixo, "v" + ArquivoAnalise.Sufixo), "análise nova");

        var movidos = PastaTrabalho.MoverArquivosAntigos(temp.Caminho, trabalho, "v");

        Assert.Equal(2, movidos); // a transcrição e a pasta de frames; a análise nova não é sobrescrita
        Assert.Equal("transcrição antiga", File.ReadAllText(Path.Combine(trabalho, "v" + ArquivoTranscricao.SufixoJson)));
        Assert.True(File.Exists(Path.Combine(trabalho, "v" + ExtratorFrames.SufixoPasta, "001_00-00.jpg")));
        Assert.Equal("análise nova", File.ReadAllText(Path.Combine(trabalho, "v" + ArquivoAnalise.Sufixo)));
        Assert.True(File.Exists(Path.Combine(temp.Caminho, "v" + ArquivoAnalise.Sufixo))); // a antiga fica, não some
        Assert.True(File.Exists(Path.Combine(temp.Caminho, "v.minuta.md")));
        Assert.True(File.Exists(Path.Combine(temp.Caminho, "outro" + ArquivoAnalise.Sufixo)));
        Assert.False(Directory.Exists(Path.Combine(temp.Caminho, "v" + ExtratorFrames.SufixoPasta)));
    }

    [Fact]
    public void Caminhos_do_documento_sao_relativos_e_com_barra()
    {
        var pasta = Path.Combine(Path.GetTempPath(), "docs");
        var arquivo = Path.Combine(pasta, "v.minuta", "v.frames");

        Assert.Equal("v.minuta/v.frames", PastaTrabalho.Relativo(pasta, arquivo));
    }

    [Fact]
    public void Pasta_de_trabalho_do_env_e_relativa_a_pasta_do_env()
    {
        using var temp = new PastaTemporaria();
        temp.CriarArquivo(".env", "MINUTA_PASTA_TRABALHO=trabalho");

        var config = ConfiguracaoMinuta.Carregar(temp.Caminho, _ => null);

        Assert.Equal(Path.Combine(temp.Caminho, "trabalho"), config.PastaTrabalho);
    }

    [Fact]
    public void Sem_a_variavel_a_pasta_de_trabalho_fica_ao_lado_do_documento()
    {
        using var temp = new PastaTemporaria();
        temp.CriarArquivo(".env", "GROQ_API_KEY=g");

        Assert.Null(ConfiguracaoMinuta.Carregar(temp.Caminho, _ => null).PastaTrabalho);
    }
}
