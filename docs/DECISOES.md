# Decisões do projeto

Registro do porquê das escolhas, para retomar o trabalho em qualquer máquina. Datas no formato AAAA-MM-DD.

## 2026-11-12 — Arquitetura

- **Biblioteca + clientes.** Toda a lógica fica em `Minuta.Core`. O console (`Minuta.Cli`) e a interface WPF (`Minuta.Desktop`) só chamam a biblioteca. O pipeline fica em `ProcessadorMinuta`, não no console.
- **WPF movido para `src/Minuta.Desktop`.** Com o `.csproj` na raiz, ele compilaria os `.cs` dos outros projetos (o SDK inclui todas as subpastas).
- **Sem pacotes externos no Core.** `HttpClient`, `System.Text.Json` e um leitor próprio de `.env`. Só o projeto de testes usa xUnit.
- **Duas trilhas, três papéis.** Trilha de áudio e trilha visual (Fase 3) viram uma linha do tempo `[mm:ss]`. Papéis:
  - Transcritor: Whisper no Groq.
  - Redator: LLM no OpenRouter.
  - Visão: modelo multimodal.
  Cada papel tem modelo configurável.
- **O modelo devolve JSON, o código escreve o Markdown.** O formato fica estável e as citações podem ser conferidas contra a transcrição.

## 2026-11-12 — Transcrição: Whisper (Groq), não um LLM multimodal

- Avaliado: `nvidia/nemotron-3-nano-omni-30b-a3b-reasoning:free` (aceita áudio no OpenRouter). Descartado para a transcrição porque:
  - não devolve tempos reais, só estimados;
  - pode resumir ou reescrever a fala;
  - não aceita saída estruturada;
  - é um modelo de raciocínio (mais lento);
  - divide a cota de 50 requisições por dia.
- A regra "cada card cita o trecho que o justifica" depende de uma transcrição **independente e fiel**. Se o mesmo modelo ouve e resume, ele confere a si mesmo.
- O Whisper no Groq tem plano gratuito. Os US$ 0,111/h são do plano pago; um vídeo de 10 min seria cerca de US$ 0,02.
- O Omni continua candidato para a **trilha visual** (aceita vídeo direto), assim como o `thinkingmachines/inkling-2-small:free` (aceita imagem, mas não JSON Schema).

## 2026-11-12 — Áudio

- Extração em **Opus, mono, 16 kHz, 32 kbps** (`.audio.ogg`). O Whisper reduz tudo a 16 kHz mono de qualquer forma. Fica em cerca de 2 MB a cada 10 min (medido: 10 min = 2,1 MB).
- Limite de upload do Groq gratuito: **25 MB**, cerca de 1h40 de áudio. Acima disso, erro claro; a divisão do áudio é a Fase 2.
- **Silêncio descartado** com o critério do Whisper original (`no_speech_prob > 0,6` e `avg_logprob < -1`). Isso evita frases inventadas em trechos mudos.
- **Baixa confiança** marcada com `(?)` quando `avg_logprob < -1` ou `compression_ratio > 2,4`.
- A transcrição é salva (`.transcricao.json` e `.txt`) e **reaproveitada** se o vídeo não mudou (nome, tamanho e data), para não gastar cota a cada ajuste de prompt. `--retranscrever` força uma nova.

## 2026-11-12 — Produto: núcleo geral, perfis de documento

- O Minuta é **projeto pessoal e genérico** (portfólio). Nada de empresa ou cliente real no repositório; testes só com vídeos simulados.
- Uma extração geral; o **perfil** escolhe as seções, a ênfase do prompt e o modelo do Markdown.
  - No repositório: *Análise para desenvolvimento* e *Ata de reunião*; depois *Relato de erro*.
  - Perfis personalizados (ex.: o formato de uma empresa) ficam em **pasta local fora do git**.
- Não separamos quem fala: o foco é o resumo geral, e nos vídeos de análise normalmente só uma pessoa fala. Citações no formato `[mm:ss]`, o mesmo da transcrição em `.txt`.
- Nova ordem das fases: reuniões comuns só precisam de áudio longo + perfil *Ata*, o que é mais barato que a trilha visual.

## 2026-11-12 — Privacidade

- **Groq:** não guarda dados por padrão; pode registrar entradas por até 30 dias (diagnóstico e abuso). Retenção Zero (ZDR) disponível em Data Controls, em qualquer plano. Dados nos EUA.
- **OpenRouter:** não guarda prompts por padrão, mas vale a política do provedor de cada modelo. Modelos gratuitos podem registrar ou treinar com os dados.
- Para uso com dados reais: plano com retenção zero, provedor aprovado pela organização ou o **modo local** (Whisper local + modelo local), planejado para depois.

## 2026-11-13 — Análise (Fase 1, etapa 4)

- **Saída estruturada estrita.** `response_format: json_schema` com `strict: true` e `provider.require_parameters: true`, para o OpenRouter só usar provedores que respeitam o schema. O schema é escrito sem `$ref`, porque nem todo provedor aceita.
- **Um JSON geral** (resumo, decisões, regras, dúvidas, passos, próximos passos, cards), igual para todos os perfis. Cada item traz citações `{tempo, trecho literal}`.
- **Conferência no código, não confiança no modelo.**
  - O trecho é procurado na transcrição, ignorando acentos, maiúsculas e pontuação, e pode atravessar segmentos.
  - Achou: o tempo passa a ser o do segmento real.
  - Não achou: a citação fica marcada como não conferida (⚠).
  - Trechos com menos de 8 caracteres não contam.
- **Dúvidas podem não ter citação:** muitas vezes são justamente o que não foi dito. Os outros itens exigem citação.
- **JSON fora do formato** é pedido de novo uma vez antes de dar erro. A resposta cortada por tamanho (`finish_reason: length`) é tratada.
- **Cache da análise** (`.analise.json`) por vídeo, modelo e `VersaoPrompt`, para ajustar o documento (etapa 5) sem gastar a cota de 50 requisições por dia.
- **Teste real** (vídeo fictício de 1 min, `nemotron-3.5-super-vl:free`): 13 s; 12 de 12 citações conferidas. O modelo levantou como dúvidas o que não foi dito (reservas, quantidades) em vez de inventar.

## 2026-11-13 — Claude Meetings: complemento, não concorrente

- A Anthropic lançou o **Claude Meetings** (beta, 2026-11-10/12).
  - **O que faz:** entra ao vivo em chamadas do Google Meet, toma notas e gera ações; guarda transcrição, resumo e descrições de tela, sem o áudio.
  - **Disponibilidade:** só nos planos Team e Enterprise e fora de organizações com retenção zero.
  - **Convidados externos:** por padrão, não fica em chamadas com eles.
- **Não cobre o caso central do Minuta:**
  - vídeos **já gravados**, de qualquer ferramenta;
  - o analista gravando sozinho o fluxo na tela;
  - documentos para desenvolvimento com citações conferidas.
- **Decisão:** em vez de capturar reuniões ao vivo pelo app (ideia antiga), o Minuta vai **importar uma transcrição pronta** como entrada alternativa ao Whisper: a do Claude Meetings e, de forma geral, arquivos de transcrição ou legenda. O Whisper continua para os vídeos. A análise, os perfis e a conferência de citações são os mesmos para as duas entradas.
- **A investigar quando chegar a hora:**
  - como obter a transcrição do Claude Meetings fora do claude.ai (exportação manual ou API);
  - se ela traz tempos por trecho (necessários para as citações `[mm:ss]`).
- A gravação de tela pelo app com registro dos cliques continua nos planos: ela é para vídeos de análise, não para reuniões.

## 2026-11-13 — Glossário do Whisper

- **Problema:** num vídeo real, o Whisper ouviu um termo do domínio errado (trocou por palavras parecidas). Siglas e nomes de telas sofrem mais.
- **Solução:** campo `prompt` da transcrição. A documentação do Groq diz que ele serve para "guiar o estilo ou indicar a grafia de palavras incomuns", aceita até **224 tokens** e deve estar no idioma do áudio.
- **Arquivo local:** `glossario.txt` ao lado do `.env` ou o caminho em `MINUTA_GLOSSARIO`. Fica fora do git porque os termos costumam ser da empresa ou do cliente. O repositório só tem `glossario.exemplo.txt`, com termos genéricos.
- **Formato:** um termo por linha, `#` para comentário. O prompt enviado é `Glossário: termo1, termo2.`
- **Limite:** 500 caracteres, abaixo dos 224 tokens por segurança, já que o tokenizador não está disponível aqui. O que passa disso fica de fora, com aviso.
- **Cache:** o prompt usado fica salvo na transcrição. Se o glossário mudar, o vídeo é transcrito de novo (e, por consequência, analisado de novo).
- **Teste** (vídeo fictício, glossário com 3 termos, nenhum deles falado): o texto saiu idêntico, sem termos inventados. Os trechos ficaram menores (7 → 10), o que deixa os tempos das citações mais precisos.

## 2026-11-13 — Filtro de repetição (alucinação com ruído)

- **Problema:** num vídeo real, o barulho do teclado virou "E aí E aí E aí…" (18 vezes) num trecho de 20 s. O Groq não marcou esse trecho como silêncio nem como baixa confiança, então os critérios do Whisper não pegaram.
- **Solução no código:** procurar a maior sequência de uma frase de 1 a 4 palavras repetida **4 ou mais vezes seguidas**, ignorando acentos, maiúsculas e pontuação.
  - Se a repetição cobre **80% ou mais** das palavras do trecho, ele é descartado e contado como ruído, junto com o silêncio.
  - Se cobre menos (fala real com repetição no meio), o trecho fica, marcado com `(?)`.
- "Não, não, não" (3 vezes) não conta como repetição.
- O filtro roda ao interpretar a resposta do Groq: transcrições já salvas só mudam com `--retranscrever`.

## 2026-11-13 — Letras cortadas num trecho (hipótese, a confirmar)

- No mesmo vídeo, um trecho longo, de fala rápida, perdeu as letras a partir do acento ("até" → "at", "não" → "n").
- **Hipótese:** o erro vem do Whisper, não do Minuta. O resto da mesma resposta tem acentos normais, e um erro de leitura do texto trocaria caracteres ("Ã©") em todos os trechos, em vez de sumir com letras num só.
- **Turbo testado (2026-11-13):** o `whisper-large-v3-turbo` cortou o mesmo trecho, do mesmo jeito. Também errou mais nomes e perdeu 30 s de fala importante. Continuamos com o `whisper-large-v3`.
  - A transcrição salva é ASCII puro nesse trecho: não há caractere escondido, as letras já não vieram.
- **Resposta bruta (2026-11-16):** a cada transcrição, o Minuta grava `<video>.groq.json` exatamente como chegou, antes de interpretar e mesmo que seja inválida. O arquivo fica fora do git e o programa não o lê.
  - Se o campo `text` (a transcrição inteira) vier certo e só o trecho vier cortado, é falha do Groq ao montar os trechos, e dá para corrigir o trecho a partir do `text`.
  - Se o `text` também vier cortado, é o modelo: no máximo, marcar o trecho com `(?)`.
- **Resultado do diagnóstico (2026-11-16):**
  - O `text` da resposta bruta também vem cortado: o erro acontece dentro do Groq, não na montagem dos trechos nem no Minuta.
  - **O mesmo pedaço de áudio (24 s), enviado sozinho, volta perfeito**, com e sem glossário ("até então a estrutura está ok… histórico… lá… não").
  - **Conclusão:** o erro depende do contexto do áudio longo. O Whisper transcreve em janelas de 30 s condicionadas ao texto anterior, e uma janela "sem acentos" contamina o resto dela. Não é um problema da gravação.
  - Os campos `seek` e `tokens` da resposta não batem com o texto do trecho; não servem para o diagnóstico.
- **Decisão: não corrigir por enquanto.** Re-transcrever sozinhos os trechos suspeitos resolveria, mas a análise já entende o contexto mesmo com essas falhas, e o resumo sai correto. Fica como ideia caso o problema passe a prejudicar o documento.

## 2026-11-13 — Documento (Fase 1, etapa 5)

- **O código escreve o Markdown** a partir de `.analise.json`. Trocar de perfil ou ajustar o formato não chama API; o documento é sempre refeito.
- **Perfil = dados:** id, título e lista de seções (tipo, título, estilo: marcadores, numerada ou tarefas). Isso prepara a leitura de perfis personalizados de uma pasta local.
  - `analise`: resumo, passos para reproduzir, regras, decisões, dúvidas, cards e próximos passos.
  - `ata`: resumo, decisões, próximos passos e pendências.
- **Seções vazias não aparecem**, para o documento ficar curto.
- **Itens:** texto em uma linha com os tempos `[mm:ss]` no fim. ⚠ marca citação não conferida ou citação obrigatória ausente. Dúvida sem citação aparece como *(não dito no vídeo)*.
- **Cards:** descrição, critérios de aceite como checklist e o trecho literal que os originou.
- **Cabeçalho:** vídeo, duração, data, modelos e quantas citações foram conferidas. **Rodapé:** legenda das marcas e o nome do `.txt` da transcrição.
- **Saída:** `<video>.minuta.md`, sobrescrito a cada execução (já está no `.gitignore`).

## 2026-11-16 — Reposicionamento: foco em vídeos de análise

- **Situação:** se a empresa usar o Claude Meetings, ele cobre a ata de reuniões ao vivo no Meet (notas, resumo e ações) melhor que o Minuta, sem precisar gravar e baixar arquivo. Não vale competir nisso.
- **Onde o Minuta continua útil:**
  - vídeos gravados, sobretudo de análise e relato de erro (tela + fala), de qualquer origem;
  - Teams e Zoom (o Claude Meetings só atende o Meet);
  - reuniões com convidados externos (por padrão, o Claude Meetings não entra nelas) e organizações sem plano Team/Enterprise;
  - documento **para desenvolvimento**, com citações conferidas no código e formato fixo (perfil);
  - no futuro, a trilha visual e o modo local.
- **Decisões:**
  - O perfil *Ata* fica simples e secundário, sem ênfase de prompt nem campos de responsável e prazo.
  - Para reuniões, a prioridade passa a ser **importar a transcrição pronta** (Claude Meetings, `.vtt`/`.srt`).
  - Nova ordem de prioridade: ajustes do prompt → perfis personalizados → Fase 3 (trilha visual) → importar transcrição → Fase 2 (áudio longo), esta só se houver necessidade.
  - Isso substitui a ordem anterior (áudio longo antes da trilha visual), que fazia sentido quando reuniões comuns eram um caso central.

## Limites dos planos gratuitos (consultados em 2026-11-12)

| Provedor | Limite |
|---|---|
| Groq Whisper | 20 req/min, 2.000 req/dia, 2 h de áudio por hora, 8 h por dia (fontes secundárias; conferir em console.groq.com/settings/limits) |
| OpenRouter `:free` | 20 req/min; 50 req/dia com menos de US$ 10 em créditos já comprados, 1.000/dia com pelo menos US$ 10 |
