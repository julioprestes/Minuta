using System.Net;
using System.Text.Json.Nodes;
using Minuta.Core.Analises;
using Minuta.Core.Documentos;
using Minuta.Core.Transcricoes;
using Minuta.Core.Visao;

namespace Minuta.Core.Tests;

public class LinhaDoTempoTests
{
    private static DescricaoVisual Visao() => new("v:free", 1,
    [
        new("001_00-12.jpg", TimeSpan.FromSeconds(12), "Novo pedido", "", [], ["Saldo bloqueado no depósito."], [], "Mensagem de erro.", "Clicou em Salvar"),
        new("002_00-40.jpg", TimeSpan.FromSeconds(40), "", "", [], [], [], "", ""), // o modelo não descreveu
    ]);

    [Fact]
    public void Junta_falas_e_telas_em_ordem_com_a_tela_antes_da_fala_no_mesmo_tempo()
    {
        var linha = LinhaDoTempo.Montar(Exemplos.Transcricao(), Visao());

        Assert.Equal(5, linha.Count); // 4 falas + 1 tela (a vazia fica de fora)
        Assert.True(linha[1].DaTela);
        Assert.Equal(TimeSpan.FromSeconds(12), linha[1].Inicio);
        Assert.Equal("001_00-12.jpg", linha[1].Frame);
        Assert.False(linha[2].DaTela);

        var texto = LinhaDoTempo.ParaTexto(linha);
        Assert.Contains("[00:00] fala: Bom dia, vou mostrar a tela de cadastro de pedidos.", texto);
        // Com fala, a tela entra enxuta: só os nomes copiados (sem descrição nem ação provável).
        Assert.Contains("[00:12] tela: janela \"Novo pedido\" | mensagem: \"Saldo bloqueado no depósito.\"" + Environment.NewLine, texto);
    }

    [Fact]
    public void Sem_fala_a_tela_vai_completa()
    {
        var texto = LinhaDoTempo.ParaTexto(LinhaDoTempo.Montar(new Transcricao("m", "pt", null, []), Visao()));

        Assert.Contains("[00:12] tela: janela \"Novo pedido\" | mensagem: \"Saldo bloqueado no depósito.\" | Mensagem de erro | ação provável (dedução): Clicou em Salvar", texto);
    }

    [Fact]
    public void Com_fala_telas_que_so_mudam_nos_campos_viram_uma_e_tela_sem_nomes_fica_de_fora()
    {
        var visao = new DescricaoVisual("v:free", 1,
        [
            new("001_00-20.jpg", TimeSpan.FromSeconds(20), "Relatório", "", ["Página: 1"], [], [], "Página 1.", ""),
            new("002_00-22.jpg", TimeSpan.FromSeconds(22), "Relatório", "", ["Página: 2"], [], [], "Página 2.", ""),
            new("003_00-25.jpg", TimeSpan.FromSeconds(25), "", "", ["Cliente: 1020"], [], [], "Tela com um campo.", ""),
        ]);

        var telas = LinhaDoTempo.Montar(Exemplos.Transcricao(), visao).Where(e => e.DaTela).ToList();

        var tela = Assert.Single(telas);
        Assert.Equal("janela \"Relatório\"", tela.Texto);
        Assert.Equal(TimeSpan.FromSeconds(22), tela.Fim);
    }

    [Fact]
    public void Confere_citacao_da_tela_com_tempo_e_frame()
    {
        var conferencia = new ConferenciaCitacoes(LinhaDoTempo.Montar(Exemplos.Transcricao(), Visao()));

        var c = conferencia.Conferir("\"Saldo bloqueado no depósito.\"", "00:50");

        Assert.True(c.Conferida);
        Assert.True(c.DaTela);
        Assert.Equal(TimeSpan.FromSeconds(12), c.Inicio);
        Assert.Equal("001_00-12.jpg", c.Frame);
    }

    [Fact]
    public void Trecho_que_esta_na_fala_e_na_tela_conta_como_fala()
    {
        // "estoque insuficiente" foi dito em 00:12; a fala tem prioridade sobre a tela.
        var visao = new DescricaoVisual("v:free", 1,
            [new("001_00-13.jpg", TimeSpan.FromSeconds(13), "Novo pedido", "", [], ["Estoque insuficiente."], [], "", "")]);
        var conferencia = new ConferenciaCitacoes(LinhaDoTempo.Montar(Exemplos.Transcricao(), visao));

        Assert.False(conferencia.Conferir("mensagem: estoque insuficiente", null).DaTela);
    }

    [Fact]
    public void Fala_continua_conferida_mesmo_com_tela_no_meio()
    {
        // A fala de 00:30 a 00:52 atravessa dois trechos; uma tela entre eles não pode quebrar a conferência.
        var visao = new DescricaoVisual("v:free", 1,
            [new("001_00-45.jpg", TimeSpan.FromSeconds(45), "Pedidos", "", [], [], [], "Tela de pedidos.", "")]);
        var conferencia = new ConferenciaCitacoes(LinhaDoTempo.Montar(Exemplos.Transcricao(), visao));

        var c = conferencia.Conferir("continua sendo 10%, sem mudança", null);

        Assert.True(c.Conferida);
        Assert.False(c.DaTela);
        Assert.Equal(TimeSpan.FromSeconds(30), c.Inicio);
    }
}

public class RedatorComTelasTests
{
    private static DescricaoVisual Visao() => new("v:free", 1,
        [new("001_00-12.jpg", TimeSpan.FromSeconds(12), "Novo pedido", "", [], ["Estoque insuficiente para o produto 77."], [], "Erro.", "")]);

    [Fact]
    public async Task Envia_linha_do_tempo_com_telas_e_contexto_e_marca_citacao_de_tela()
    {
        var resposta = new
        {
            resumo = "Erro ao salvar.",
            decisoes = Array.Empty<object>(),
            regras_negocio = Array.Empty<object>(),
            duvidas_em_aberto = Array.Empty<object>(),
            passos_para_reproduzir = new[]
            {
                new { texto = "Salvar o pedido.", citacoes = new[] { new { tempo = "00:12", trecho = "Estoque insuficiente para o produto 77" } } },
            },
            proximos_passos = Array.Empty<object>(),
            cards = Array.Empty<object>(),
        };
        var handler = new HandlerFalso(() => HandlerFalso.Json(HttpStatusCode.OK, Exemplos.RespostaChat(resposta)));
        var transcricao = Exemplos.Transcricao();

        var a = await new RedatorOpenRouter(new HttpClient(handler), "k", "m:free")
            .AnalisarAsync(transcricao, Visao(), "Chamado: não salva pedido.");

        var mensagem = JsonNode.Parse(handler.UltimoCorpo!)!["messages"]![1]!["content"]!.GetValue<string>();
        Assert.Contains("Contexto informado por quem enviou o vídeo", mensagem);
        Assert.Contains("Chamado: não salva pedido.", mensagem);
        Assert.Contains("[00:12] tela: janela \"Novo pedido\"", mensagem);
        Assert.Contains("falas e telas", mensagem);

        var c = Assert.Single(Assert.Single(a.PassosParaReproduzir).Citacoes);
        Assert.True(c.DaTela);
        Assert.Equal("001_00-12.jpg", c.Frame);
        Assert.Equal(RedatorOpenRouter.AssinaturaDaEntrada(transcricao, Visao(), "Chamado: não salva pedido."), a.Assinatura);
    }

    [Fact]
    public async Task Video_so_com_telas_avisa_o_modelo()
    {
        var resposta = new
        {
            resumo = "Só telas.",
            decisoes = Array.Empty<object>(), regras_negocio = Array.Empty<object>(), duvidas_em_aberto = Array.Empty<object>(),
            passos_para_reproduzir = Array.Empty<object>(), proximos_passos = Array.Empty<object>(), cards = Array.Empty<object>(),
        };
        var handler = new HandlerFalso(() => HandlerFalso.Json(HttpStatusCode.OK, Exemplos.RespostaChat(resposta)));
        var semFala = new Transcricao("(sem áudio)", "pt", TimeSpan.FromSeconds(20), []);

        await new RedatorOpenRouter(new HttpClient(handler), "k", "m:free").AnalisarAsync(semFala, Visao());

        var mensagem = JsonNode.Parse(handler.UltimoCorpo!)!["messages"]![1]!["content"]!.GetValue<string>();
        Assert.Contains("só telas (o vídeo não tem fala reconhecida)", mensagem);
    }

    [Fact]
    public void Assinatura_muda_com_telas_ou_contexto()
    {
        var t = Exemplos.Transcricao();
        var semNada = RedatorOpenRouter.AssinaturaDaEntrada(t, null, null);

        Assert.Equal(semNada, RedatorOpenRouter.AssinaturaDaEntrada(t, null, null));
        Assert.NotEqual(semNada, RedatorOpenRouter.AssinaturaDaEntrada(t, Visao(), null));
        Assert.NotEqual(semNada, RedatorOpenRouter.AssinaturaDaEntrada(t, null, "contexto"));
    }

    [Fact]
    public void Analise_salva_com_outra_assinatura_nao_e_reaproveitada()
    {
        using var temp = new PastaTemporaria();
        var video = new FileInfo(temp.CriarArquivo("v.mp4", "vídeo"));
        var caminho = Path.Combine(temp.Caminho, "v" + ArquivoAnalise.Sufixo);
        ArquivoAnalise.Salvar(new AnaliseVideo("Resumo.", [], [], [], [], [], [])
        {
            Modelo = "m:free",
            VersaoPrompt = RedatorOpenRouter.VersaoPrompt,
            Origem = OrigemTranscricao.De(video),
            Assinatura = "AAA",
        }, caminho);

        Assert.NotNull(ArquivoAnalise.Carregar(caminho, video, "m:free", RedatorOpenRouter.VersaoPrompt, "AAA"));
        Assert.Null(ArquivoAnalise.Carregar(caminho, video, "m:free", RedatorOpenRouter.VersaoPrompt, "BBB"));
    }
}

public class DocumentoComTelasTests
{
    private static readonly DateTime Agora = new(2026, 11, 16, 12, 0, 0);

    private static DescricaoVisual Visao() => new("modelo/visao:free", 1,
    [
        new("001_00-04.jpg", TimeSpan.FromSeconds(4), "Sistema Exemplo", "Vendas > Pedidos > Novo pedido", [], [], [], "Menu aberto.", ""),
        new("002_00-09.jpg", TimeSpan.FromSeconds(9), "Novo pedido", "", ["Produto: 77"], ["Estoque insuficiente para o produto 77."], ["OK"], "Erro ao salvar.", "Clicou em Salvar"),
        new("003_00-12.jpg", TimeSpan.FromSeconds(12), "", "", [], [], [], "", ""), // o modelo não descreveu
    ]);

    private static string Gerar(PerfilDocumento perfil, string? contexto = null)
    {
        var tela = new Citacao("Estoque insuficiente para o produto 77", TimeSpan.FromSeconds(9), true, DaTela: true, Frame: "002_00-09.jpg");
        var analise = new AnaliseVideo("Erro ao salvar pedido.", [], [], [],
            [new("Clicar em Salvar no Novo pedido.", [tela])], [],
            [new("Corrigir validação de estoque", "Recusa com saldo disponível.", ["Salva com saldo."], [tela])])
        { Modelo = "modelo/analise:free" };
        return GeradorDocumento.Gerar(analise, Exemplos.Transcricao(), perfil, "tela.mp4", Agora, new ExtrasDocumento
        {
            Visao = Visao(),
            PastaFrames = "tela.frames",
            ArquivoTelas = "tela.telas.md",
            Contexto = contexto,
        });
    }

    [Fact]
    public void Citacao_de_tela_e_marcada_e_o_card_tem_link_para_a_imagem()
    {
        var md = Gerar(PerfisDocumento.Analise);

        Assert.Contains("1. Clicar em Salvar no Novo pedido. [00:09 (tela)]", md);
        Assert.Contains("> [00:09] *(tela)* \"Estoque insuficiente para o produto 77\" — [ver tela](<tela.frames/002_00-09.jpg>)", md);
        Assert.Contains("- **Modelos:** transcrição whisper-large-v3; telas modelo/visao:free; análise modelo/analise:free", md);
        Assert.Contains("- **Telas descritas:** 2 (lista em `tela.telas.md`; imagens em `tela.frames/`)", md);
    }

    [Fact]
    public void Documento_principal_nao_lista_as_telas_so_aponta_o_arquivo()
    {
        var md = Gerar(PerfisDocumento.Analise);

        Assert.DoesNotContain("## Telas do vídeo", md);
        Assert.DoesNotContain("001_00-04.jpg", md); // tela não citada não aparece
        Assert.Contains("Telas do vídeo: `tela.telas.md`.", md);
    }

    [Fact]
    public void Contexto_aparece_no_cabecalho()
    {
        var md = Gerar(PerfisDocumento.Ata, contexto: "Cliente diz que\nnão consegue salvar.");

        Assert.Contains("- **Contexto informado (não vem do vídeo):** Cliente diz que não consegue salvar.", md);
    }
}

public class TelasDistintasTests
{
    private static readonly DateTime Agora = new(2026, 11, 16, 12, 0, 0);

    private static DescricaoTela Tela(int segundos, string janela, string descricao = "Tela.", params string[] mensagens) =>
        new($"{segundos:000}.jpg", TimeSpan.FromSeconds(segundos), janela, "", [], mensagens, [], descricao, "");

    private static DescricaoVisual Visao() => new("modelo/visao:free", 1,
    [
        Tela(4, "Consulta de pedidos"),
        Tela(6, "Consulta de pedidos", "A tela continua igual."), // mesma tela, descrição diferente: junta
        Tela(8, "Consulta de pedidos", "Tela.", "Pedido salvo."),  // apareceu uma mensagem: é outra tela
        Tela(10, "", ""),                                           // vazia: fica de fora
        Tela(12, "Consulta de pedidos"),                            // voltou: conta de novo
        Tela(14, "", "Área de trabalho vazia."),
        Tela(16, "", "Ícones dos módulos."),                        // sem textos da tela: só junta se a descrição for igual
    ]);

    [Fact]
    public void Junta_telas_iguais_em_sequencia()
    {
        var grupos = Visao().TelasDistintas();

        Assert.Equal([4, 8, 12, 14, 16], grupos.Select(g => (int)g.Tela.Tempo.TotalSeconds));
        Assert.Equal(TimeSpan.FromSeconds(6), grupos[0].Fim);
        Assert.Equal(2, grupos[0].Frames);
        Assert.Equal(1, grupos[1].Frames);
    }

    [Fact]
    public void Linha_do_tempo_manda_a_tela_repetida_uma_vez_com_o_intervalo()
    {
        var texto = LinhaDoTempo.ParaTexto(LinhaDoTempo.Montar(new Transcricao("m", "pt", null, []), Visao()));

        Assert.Contains("[00:04] tela (até 00:06): janela \"Consulta de pedidos\" | Tela", texto);
        Assert.DoesNotContain("[00:06]", texto);
        Assert.Contains("[00:08] tela: janela \"Consulta de pedidos\" | mensagem: \"Pedido salvo.\"", texto);
    }

    [Fact]
    public void Arquivo_de_telas_lista_as_distintas_com_intervalo_e_link()
    {
        var md = DocumentoTelas.Gerar(Visao(), "pedidos.mp4", "pedidos.frames", Agora);

        Assert.StartsWith("# pedidos — Telas do vídeo", md);
        Assert.Contains("- **Telas:** 5 distintas, de 7 frames", md);
        Assert.Contains("- [00:04–00:06] Consulta de pedidos — [imagem](<pedidos.frames/004.jpg>)", md);
        Assert.Contains("- [00:08] Consulta de pedidos | mensagem: \"Pedido salvo.\" — [imagem](<pedidos.frames/008.jpg>)", md);
        Assert.Contains("- [00:14] Área de trabalho vazia. — [imagem](<pedidos.frames/014.jpg>)", md);
        Assert.DoesNotContain("010.jpg", md);
    }
}

public class PromptsTests
{
    [Fact]
    public void Prompt_da_analise_manda_manter_o_item_dito_mesmo_com_divergencia()
    {
        Assert.Contains("A FALA é a fonte principal", RedatorOpenRouter.InstrucoesDoSistema);
        Assert.Contains("NUNCA elimina o item", RedatorOpenRouter.InstrucoesDoSistema);
        Assert.Contains("tela (até mm:ss)", RedatorOpenRouter.InstrucoesDoSistema);
    }

    [Fact]
    public void Prompt_da_analise_separa_pedido_de_contexto_e_limita_os_passos()
    {
        Assert.Contains("é contexto, não pedido", RedatorOpenRouter.InstrucoesDoSistema);
        Assert.Contains("Em vídeo de requisito ou pedido de melhoria, sem erro, deixe vazio", RedatorOpenRouter.InstrucoesDoSistema);
        Assert.Contains("é dúvida, não card", RedatorOpenRouter.InstrucoesDoSistema);
        Assert.Contains("costuma continuar na fala seguinte", RedatorOpenRouter.InstrucoesDoSistema);
    }

    [Theory]
    [InlineData("Cpf: 742.617.700-9", "Cpf: [oculto]")]
    [InlineData("CPF 266.400.191-47 e CNPJ 12.345.678/0001-90", "CPF [oculto] e CNPJ [oculto]")]
    [InlineData("Fone: 05197421107", "Fone: [oculto]")]
    [InlineData("Fone: (51) 3742-1107 / 99876-5432", "Fone: [oculto] / [oculto]")]
    [InlineData("E-mail: fulano.tal@exemplo.com.br", "E-mail: [oculto]")]
    public void Esconde_documentos_e_contatos(string texto, string esperado) =>
        Assert.Equal(esperado, DadosPessoais.Mascarar(texto));

    [Theory]
    [InlineData("Safra: 2025-2026")]
    [InlineData("Valor a Fixar: 0,0000000000")]
    [InlineData("Cep: 96900-000; Documento: 42599; Data: 26/08/2026")]
    [InlineData("Chave: 43260812345678000190550010000425991000425990")] // 44 dígitos: não é CPF nem telefone
    public void Nao_esconde_numeros_comuns(string texto) =>
        Assert.Equal(texto, DadosPessoais.Mascarar(texto));

    [Fact]
    public void Prompt_da_visao_nao_aceita_barra_de_menus_como_caminho()
    {
        Assert.Contains("A barra de menus, as abas e as listas de opções apenas visíveis NÃO são caminho",
            DescritorFramesOpenRouter.InstrucoesDoSistema);
    }
}
