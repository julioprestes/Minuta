using Minuta.Core.Configuracao;

namespace Minuta.Core.Tests;

public class ConfiguracaoMinutaTests
{
    private static readonly Func<string, string?> SemVariaveis = _ => null;

    [Fact]
    public void Le_valores_do_env_e_aplica_padroes()
    {
        using var temp = new PastaTemporaria();
        temp.CriarArquivo(".env", "GROQ_API_KEY=g\nOPENROUTER_API_KEY=o\nOPENROUTER_MODELO_ANALISE=m:free");

        var config = ConfiguracaoMinuta.Carregar(temp.Caminho, SemVariaveis);

        Assert.Equal("g", config.GroqApiKey);
        Assert.Equal("o", config.OpenRouterApiKey);
        Assert.Equal("m:free", config.ModeloAnalise);
        Assert.Equal(ConfiguracaoMinuta.ModeloTranscricaoPadrao, config.ModeloTranscricao);
        Assert.Equal("pt", config.Idioma);
        Assert.Empty(config.Pendencias());
    }

    [Fact]
    public void Variavel_de_ambiente_tem_prioridade_sobre_o_env()
    {
        using var temp = new PastaTemporaria();
        temp.CriarArquivo(".env", "GROQ_API_KEY=do-arquivo");

        var config = ConfiguracaoMinuta.Carregar(temp.Caminho,
            nome => nome == ConfiguracaoMinuta.VarGroqApiKey ? "do-ambiente" : null);

        Assert.Equal("do-ambiente", config.GroqApiKey);
    }

    [Fact]
    public void Valor_vazio_conta_como_nao_definido()
    {
        using var temp = new PastaTemporaria();
        temp.CriarArquivo(".env", "GROQ_API_KEY=\nGROQ_MODELO_TRANSCRICAO=  ");

        var config = ConfiguracaoMinuta.Carregar(temp.Caminho, SemVariaveis);

        Assert.Null(config.GroqApiKey);
        Assert.Equal(ConfiguracaoMinuta.ModeloTranscricaoPadrao, config.ModeloTranscricao);
    }

    [Fact]
    public void Sem_env_lista_as_tres_pendencias()
    {
        using var temp = new PastaTemporaria();

        var config = ConfiguracaoMinuta.Carregar(temp.Caminho, SemVariaveis);

        Assert.Equal(3, config.Pendencias().Count);
    }
}
