# Minuta — instruções para agentes

## Objetivo
Transformar gravações de reuniões e de tela em um documento `.md` curto, pesquisável e fiel ao que foi dito e mostrado: resumo, decisões, regras de negócio, dúvidas em aberto, passos para reproduzir um erro, próximos passos e cards de tarefa.

Caso de uso principal: vídeos em que alguém **mostra um fluxo de sistema e fala sobre ele** (análise de requisitos, relato de erro). O núcleo é geral e também serve para reuniões comuns.

## Arquitetura
```
vídeo ─▶ trilha de áudio (FFmpeg ─▶ Transcritor) ─┐
      └▶ trilha visual (FFmpeg ─▶ Visão)  [Fase 3] ┴▶ linha do tempo [mm:ss] ─▶ Redator (JSON) ─▶ perfil ─▶ .md
```
- **Transcritor:** Whisper via Groq. **Redator:** modelo via OpenRouter, com saída JSON validada. **Visão:** modelo multimodal (Fase 3). Cada papel tem modelo configurável no `.env`.
- **Núcleo geral, perfil específico.** A extração é uma só (um JSON geral com citações). O **perfil** escolhe as seções, a ênfase do prompt e o modelo do Markdown.
  - Perfis do repositório (genéricos): *Análise para desenvolvimento* e *Ata de reunião*; depois *Relato de erro*.
  - Perfis personalizados ficam em pasta local ignorada pelo git.
- Lógica em `Minuta.Core`; `Minuta.Cli` (console) e `Minuta.Desktop` (WPF) só a usam.

## Fases
Não comece uma fase antes de a anterior funcionar.
1. **MVP (console):** vídeo COM áudio → áudio → transcrição → análise → `.md`, com os perfis *Análise para desenvolvimento* e *Ata de reunião*.
2. **Áudio longo:** dividir o áudio acima do limite de upload e juntar as transcrições.
3. **Trilha visual:** frames escolhidos por mudança de tela, por menção na fala ("aqui", "clico em") e por intervalo, analisados por modelo multimodal e somados à linha do tempo. Também cobre vídeos sem áudio.
4. **Interface desktop** simples (WPF).
5. Depois:
   - provedores configuráveis (ex.: API da Anthropic direto);
   - **modo local** (Whisper local + modelo local, nada sai da máquina);
   - gravação/captura direta pelo app, com registro real dos cliques.

## Regras de trabalho
- Plano curto primeiro; código só depois da aprovação do desenvolvedor.
- Uma etapa por vez. Ao terminar: parar, dizer como testar e sugerir a mensagem de commit.
- Antes de decidir sobre um provedor de IA, consultar a documentação oficial atual (custos, limites, tamanho máximo de arquivo, tratamento dos dados).
- Chaves só em variáveis de ambiente ou `.env`. `.env` no `.gitignore`; manter `.env.example` atualizado.
- **Não inventar.** O que não estiver no vídeo vira dúvida em aberto. Cada card cita o trecho (com tempo) que o justifica, e o código confere a citação contra a transcrição.
- Tratar: arquivo inválido, FFmpeg ausente, falha de rede, resposta inválida da API.
- Evitar dependências e abstrações desnecessárias.
- **Repositório pessoal e genérico:** nenhum nome, dado ou formato de empresa ou cliente real. Testes só com vídeos simulados e dados fictícios.
- Responder em português.
