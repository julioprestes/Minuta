using Minuta.Core.Configuracao;

namespace Minuta.Core.Tests;

public class ArquivoEnvTests
{
    [Fact]
    public void Interpreta_chaves_ignorando_comentarios_e_linhas_vazias()
    {
        var valores = ArquivoEnv.Interpretar("""
            # comentário
            GROQ_API_KEY=abc123

            export OPENROUTER_API_KEY = "x y z"
            MINUTA_IDIOMA='pt'
            URL=https://exemplo.com/?a=1
            LINHA_SEM_IGUAL
            """);

        Assert.Equal("abc123", valores["GROQ_API_KEY"]);
        Assert.Equal("x y z", valores["OPENROUTER_API_KEY"]);
        Assert.Equal("pt", valores["MINUTA_IDIOMA"]);
        Assert.Equal("https://exemplo.com/?a=1", valores["URL"]);
        Assert.Equal(4, valores.Count);
    }

    [Fact]
    public void Aceita_quebra_de_linha_do_Windows()
    {
        var valores = ArquivoEnv.Interpretar("A=1\r\nB=2\r\n");

        Assert.Equal("1", valores["A"]);
        Assert.Equal("2", valores["B"]);
    }

    [Fact]
    public void Localiza_env_em_diretorio_acima()
    {
        using var temp = new PastaTemporaria();
        File.WriteAllText(Path.Combine(temp.Caminho, ".env"), "A=1");
        var sub = Directory.CreateDirectory(Path.Combine(temp.Caminho, "a", "b")).FullName;

        Assert.Equal(Path.Combine(temp.Caminho, ".env"), ArquivoEnv.Localizar(sub));
    }
}
