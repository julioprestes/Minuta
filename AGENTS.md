# Minuta — instruções para agentes

## Objetivo
Transformar gravações de reuniões e de tela em um documento `.md` curto, pesquisável e fiel ao que foi dito e mostrado: resumo, decisões, regras de negócio, dúvidas em aberto, passos para reproduzir um erro, próximos passos e cards de tarefa.

Caso de uso principal: vídeos em que alguém **mostra um fluxo de sistema e fala sobre ele** (análise de requisitos, relato de erro). O documento é **para desenvolvimento**.

**Reuniões ao vivo não são o foco:** ferramentas como o Claude Meetings já fazem notas e ações. O Minuta complementa: para reuniões, a prioridade é **importar a transcrição pronta** e gerar o documento de desenvolvimento a partir dela. O perfil *Ata* fica simples e secundário (sem investir em ênfase de prompt nem em campos de responsável e prazo).

## Arquitetura
```
vídeo ─▶ trilha de áudio (FFmpeg ─▶ Transcritor) ─┐
      └▶ trilha visual (FFmpeg ─▶ Visão)  [Fase 2] ┴▶ linha do tempo [mm:ss] ─▶ Redator (JSON) ─▶ perfil ─▶ .md
```
- **Transcritor:** Whisper via Groq. **Redator:** modelo via OpenRouter, com saída JSON validada. **Visão:** modelo multimodal (Fase 2). Cada papel tem modelo configurável no `.env`.
- **Núcleo geral, perfil específico.** A extração é uma só (um JSON geral com citações). O **perfil** escolhe as seções, a ênfase do prompt e o modelo do Markdown.
  - Perfis do repositório (genéricos): *Análise para desenvolvimento* (principal) e *Ata de reunião* (secundário); depois *Relato de erro*.
  - Perfis personalizados ficam em pasta local ignorada pelo git.
- Lógica em `Minuta.Core`; `Minuta.Cli` (console) e `Minuta.Desktop` (WPF) só a usam.

## Fases
Não comece uma fase antes de a anterior funcionar. Exceção: a Fase 3 só entra quando houver necessidade, e a Fase 4 pode vir antes dela.
1. **MVP (console):** vídeo COM áudio → áudio → transcrição → análise → `.md`, com os perfis *Análise para desenvolvimento* e *Ata de reunião*.
2. **Trilha visual:** frames escolhidos por mudança de tela, por menção na fala ("aqui", "clico em") e por intervalo, analisados por modelo multimodal e somados à linha do tempo. Também cobre vídeos sem áudio.
3. **Áudio longo:** dividir o áudio acima do limite de upload (~1h40) e juntar as transcrições. Só quando houver necessidade: os vídeos de análise têm no máximo uns 15 min, e as reuniões longas tendem a vir por transcrição pronta.
4. **Interface desktop** simples (WPF).
5. Depois:
   - **importar transcrição pronta** como entrada, no lugar do Whisper: Claude Meetings (reuniões ao vivo) e arquivos de legenda/transcrição. O Whisper continua para os vídeos. Substitui a ideia de capturar reuniões ao vivo pelo app;
   - provedores configuráveis (ex.: API da Anthropic direto);
   - **modo local** (Whisper local + modelo local, nada sai da máquina);
   - gravação de tela pelo app, com registro real dos cliques (vídeos de análise, não reuniões).

## Estado atual
Atualize esta seção ao fim de cada etapa. O porquê das escolhas está em [docs/DECISOES.md](docs/DECISOES.md).

**Fase 1, concluída:**
- ✅ Etapa 1, estrutura: solução com Core, Cli, Desktop e testes; configuração por `.env`; `minuta --checar`.
- ✅ Etapa 2, áudio: `ExtratorAudio` (ffprobe confere o arquivo; ffmpeg gera Opus mono 16 kHz em `<video>.audio.ogg`).
- ✅ Etapa 3, transcrição: `TranscritorGroq` (Whisper, `verbose_json`, descarte de silêncio, novas tentativas). Salva `<video>.transcricao.json` e `.txt` e reaproveita se o vídeo não mudou (`--retranscrever` refaz). Testada com chave real em vídeo fictício.
- ✅ Etapa 4, análise: `RedatorOpenRouter` (JSON Schema estrito, `require_parameters`, novas tentativas via `ChamadorComTentativas`, compartilhado com o Groq).
  - `ConferenciaCitacoes` acha cada trecho na transcrição (sem acentos nem pontuação) e corrige o tempo.
  - Salva `<video>.analise.json` e reaproveita (`--reanalisar` refaz; mudar `VersaoPrompt` invalida).
  - Testada com `nvidia/nemotron-3.5-super-vl:free`: 12 de 12 citações conferidas.
- ✅ Glossário do Whisper: `glossario.txt` local (ou `MINUTA_GLOSSARIO`), enviado no `prompt`; até 500 caracteres; mudar o glossário refaz a transcrição.
- ✅ Filtro de repetição: trecho que é quase só uma frase curta repetida (≥ 4 vezes, ≥ 80% das palavras) é descartado como ruído; repetição menor marca `(?)`.
- ✅ Resposta bruta do Groq gravada em `<video>.groq.json` (diagnóstico). Em investigação: um trecho de vídeo real vem com letras cortadas a partir do acento (ver DECISOES).
- ✅ Etapa 5, documento: `GeradorDocumento` monta `<video>.minuta.md` a partir da análise, com os perfis `analise` (padrão) e `ata` (`--perfil`). Seções vazias não aparecem; tempos `[mm:ss]` e ⚠ para citação não conferida. O console mostra só a contagem e o caminho.

**Fase 2, em andamento (trilha visual):**
- ✅ Etapa 1, frames: `DetectorMudancas` compara quadros pequenos em cinza lidos crus da saída do ffmpeg (sem biblioteca de imagem). Escolhe a tela quando ela muda e assenta, ignora o cursor, pega um frame por intervalo longo se algo mudou e o último quadro.
  - `ExtratorFrames` salva os JPEG (até 1280 px) em `<video>.frames/`, com `indice.json`, e reaproveita (`--refazer-frames` refaz; mudar `VersaoSelecao` invalida).
  - Testada em vídeo real de 3,5 min: 35 frames em ~15 s, incluindo menus e submenus abertos.
- ✅ Etapa 2, descrição: `DescritorFramesOpenRouter` manda os frames em lotes de 10 (base64, cada um com rótulo e tempo) e recebe, por JSON Schema estrito, o título da janela, o caminho do menu, os campos e valores, as mensagens, os botões, uma descrição e a ação provável.
  - Casa cada item com o frame pelo número; um lote incompleto é pedido de novo uma vez e depois aceito, com o frame faltante vazio.
  - Cada lote recebe o resumo da última tela do lote anterior.
  - Modelo em `OPENROUTER_MODELO_VISAO` (vazio = o da análise). Cache `<video>.visao.json` (`--redescrever` refaz). `--sem-visao` pula a trilha visual.
  - A parte comum do OpenRouter (requisição, erros, leitura da resposta, ajudantes do schema) foi para `Http/OpenRouterChat`.
  - Testada com vídeo sintético (telas fictícias + narração TTS): 4 telas, menu destacado e mensagem de erro lidos certo.
- ⏭️ Etapa 3, linha do tempo: falas e telas juntas por `[mm:ss]`; prompt `VersaoPrompt` 2 (com os ajustes: observação não vira passo, sugestão separada de decisão); `--contexto`; vídeo sem fala deixa de dar erro; frames principais citados no `.md`.

**Próximos passos, em ordem de prioridade** (cada um precisa de liberação):
1. Terminar a Fase 2 (etapa 3 acima).
2. Perfis personalizados lidos de pasta local fora do git.
3. Importar transcrição pronta (Claude Meetings, `.vtt`/`.srt`) para reuniões.
4. Fase 3 (áudio longo), se aparecer necessidade.

**Para rodar numa máquina nova:**
1. Instalar o .NET 10 SDK e o FFmpeg (`winget install Gyan.FFmpeg`, depois abrir um terminal novo).
2. Copiar `.env.example` para `.env` e preencher as chaves.
3. Rodar `dotnet test` e `dotnet run --project src/Minuta.Cli -- --checar`.

## Regras de trabalho
- Plano curto primeiro; código só depois da aprovação do desenvolvedor.
- Uma etapa por vez. Ao terminar: parar, dizer como testar e sugerir a mensagem de commit.
- **Só comece a próxima etapa com liberação explícita** ("pode fazer"). Mostrar uma chave configurada ou um teste rodando não é liberação.
- Antes de decidir sobre um provedor de IA, consultar a documentação oficial atual (custos, limites, tamanho máximo de arquivo, tratamento dos dados).
- Chaves só em variáveis de ambiente ou `.env`. `.env` no `.gitignore`; manter `.env.example` atualizado.
- **Não inventar.** O que não estiver no vídeo vira dúvida em aberto. Cada card cita o trecho (com tempo) que o justifica, e o código confere a citação contra a transcrição.
- Tratar: arquivo inválido, FFmpeg ausente, falha de rede, resposta inválida da API.
- Evitar dependências e abstrações desnecessárias.
- **Repositório pessoal e genérico:** nenhum nome, dado ou formato de empresa ou cliente real. Testes só com vídeos simulados e dados fictícios.
- Responder em português.
