using Minuta.Core.Analises;
using Minuta.Core.Documentos;

namespace Minuta.Core.Tests;

public class GeradorDocumentoTests
{
    private static readonly DateTime Agora = new(2026, 11, 13, 16, 5, 0);

    private static Citacao Ok(int segundos, string trecho) => new(trecho, TimeSpan.FromSeconds(segundos), true);

    /// <summary>Análise fictícia (sistema de pedidos inventado), com uma citação não conferida.</summary>
    private static AnaliseVideo Analise() => new(
        Resumo: "Erro de estoque insuficiente ao salvar pedido.",
        Decisoes: [new("Desconto máximo por item continua 10%.", [Ok(30, "o desconto máximo por item continua sendo 10%")])],
        RegrasNegocio: [],
        DuvidasEmAberto: [new("Pedidos de outras filiais\nconsomem o mesmo estoque?", [])],
        PassosParaReproduzir:
        [
            new("Abrir a tela de cadastro de pedidos.", [Ok(0, "vou mostrar a tela de cadastro de pedidos")]),
            new("Clicar em Salvar.", [Ok(12, "quando eu clico em Salvar"), new("clico duas vezes", TimeSpan.FromSeconds(14), false)]),
        ],
        ProximosPassos: [new("Confirmar com o cliente a parte das filiais.", [Ok(45, "vou confirmar com o cliente a parte das filiais")])],
        Cards:
        [
            new("Corrigir validação de estoque", "Ao salvar, o pedido é recusado com estoque disponível.",
                ["Pedido com estoque disponível é salvo."], [Ok(12, "aparece a mensagem: estoque insuficiente")]),
        ])
    { Modelo = "modelo/teste:free" };

    private static string Gerar(PerfilDocumento perfil) =>
        GeradorDocumento.Gerar(Analise(), Exemplos.Transcricao(), perfil, "pedidos.mp4", Agora, "pedidos.transcricao.txt");

    [Fact]
    public void Analise_tem_cabecalho_secoes_na_ordem_e_tempos()
    {
        var md = Gerar(PerfisDocumento.Analise);

        Assert.StartsWith("# pedidos — Análise para desenvolvimento", md);
        Assert.Contains("- **Vídeo:** pedidos.mp4 (01:10)", md);
        Assert.Contains("- **Gerado em:** 13/11/2026 16:05, pelo Minuta", md);
        Assert.Contains("análise modelo/teste:free", md);
        Assert.Contains("- **Citações conferidas na transcrição:** 5 de 6", md);

        var ordem = new[] { "## Resumo", "## Passos para reproduzir", "## Decisões", "## Dúvidas em aberto", "## Cards de tarefa", "## Próximos passos" }
            .Select(t => md.IndexOf(t, StringComparison.Ordinal)).ToList();
        Assert.DoesNotContain(-1, ordem);
        Assert.Equal(ordem.Order(), ordem);

        Assert.Contains("1. Abrir a tela de cadastro de pedidos. [00:00]", md);
        Assert.Contains("2. Clicar em Salvar. [00:12, 00:14] ⚠", md);
        Assert.Contains("- [ ] Confirmar com o cliente a parte das filiais. [00:45]", md);
        Assert.Contains("Transcrição completa: `pedidos.transcricao.txt`.", md);
    }

    [Fact]
    public void Secao_vazia_nao_aparece()
    {
        Assert.DoesNotContain("## Regras de negócio", Gerar(PerfisDocumento.Analise));
    }

    [Fact]
    public void Duvida_sem_citacao_e_marcada_como_nao_dita_e_quebra_de_linha_vira_espaco()
    {
        Assert.Contains("- Pedidos de outras filiais consomem o mesmo estoque? *(não dito no vídeo)*", Gerar(PerfisDocumento.Analise));
    }

    [Fact]
    public void Card_traz_criterios_e_trecho_citado()
    {
        var md = Gerar(PerfisDocumento.Analise);

        Assert.Contains("### 1. Corrigir validação de estoque", md);
        Assert.Contains("- [ ] Pedido com estoque disponível é salvo.", md);
        Assert.Contains("> [00:12] \"aparece a mensagem: estoque insuficiente\"", md);
    }

    [Fact]
    public void Item_sem_citacao_obrigatoria_e_sinalizado()
    {
        var analise = Analise() with { Decisoes = [new("Decisão sem origem.", [])] };

        var md = GeradorDocumento.Gerar(analise, Exemplos.Transcricao(), PerfisDocumento.Analise, "v.mp4", Agora);

        Assert.Contains("- Decisão sem origem. ⚠ sem citação", md);
    }

    [Fact]
    public void Ata_mostra_so_as_secoes_de_reuniao()
    {
        var md = Gerar(PerfisDocumento.Ata);

        Assert.StartsWith("# pedidos — Ata de reunião", md);
        Assert.Contains("## Decisões", md);
        Assert.Contains("## Próximos passos", md);
        Assert.Contains("## Pendências e dúvidas", md);
        Assert.DoesNotContain("## Passos para reproduzir", md);
        Assert.DoesNotContain("## Cards de tarefa", md);
    }

    [Theory]
    [InlineData("analise", "analise")]
    [InlineData(" ATA ", "ata")]
    public void Obtem_perfil_pelo_nome(string nome, string esperado)
    {
        Assert.Equal(esperado, PerfisDocumento.Obter(nome).Id);
    }

    [Fact]
    public void Perfil_desconhecido_lista_os_disponiveis()
    {
        var ex = Assert.Throws<MinutaException>(() => PerfisDocumento.Obter("relatorio"));
        Assert.Contains("analise", ex.Dica);
        Assert.Contains("ata", ex.Dica);
    }
}
