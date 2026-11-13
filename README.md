# Minuta

Transforma gravações de reuniões e de tela em um documento Markdown curto e pesquisável: resumo, decisões, regras de negócio, dúvidas em aberto, passos para reproduzir um erro, próximos passos e cards de tarefa — cada item citando o momento do vídeo que o justifica.

Feito para quem recebe vídeos de explicação (análise de requisitos, relato de erro, reunião) e precisa trabalhar a partir de texto: pesquisar, copiar trechos e não assistir tudo de novo.

> Em desenvolvimento. Fase 1 (console, vídeos com áudio) em andamento.

## Como funciona

```
vídeo ─▶ trilha de áudio ── FFmpeg ─▶ Whisper (Groq) ─▶ falas [mm:ss] ──┐
      └▶ trilha visual ─── FFmpeg ─▶ modelo de visão ─▶ telas [mm:ss] ──┴▶ linha do tempo
                           (Fase 3)
linha do tempo ─▶ modelo de linguagem (OpenRouter) ─▶ JSON validado ─▶ conferência das citações ─▶ perfil ─▶ .md
```

- **Nada é inventado.** O que não está no vídeo vira *dúvida em aberto*, e toda citação é conferida contra a transcrição; a que não bater é sinalizada.
- **O modelo devolve dados, não texto pronto.** O Markdown é montado pelo código, a partir de um **perfil**:

| Perfil | Para |
|---|---|
| Análise para desenvolvimento | resumo, regras de negócio, requisitos, dúvidas e cards de tarefa |
| Ata de reunião | decisões, ações (quem / o quê / prazo, quando ditos) e pendências |
| Relato de erro *(planejado)* | passos para reproduzir, esperado × obtido |

Perfis personalizados podem ficar em pasta local, fora do repositório.

## Roteiro

| Fase | Entrega | Situação |
|---|---|---|
| 1 | Console: vídeo com áudio → transcrição → documento, perfis *Análise* e *Ata* | em andamento |
| 2 | Áudio longo (divisão acima do limite de upload) | planejada |
| 3 | Trilha visual: frames por mudança de tela e por menção na fala, analisados por modelo multimodal | planejada |
| 4 | Interface desktop (WPF) | planejada |
| depois | Importar transcrição pronta (ex.: Claude Meetings) no lugar do Whisper, provedores configuráveis, **modo local** (nada sai da máquina), gravação de tela com registro dos cliques | ideias |

## Estrutura

| Projeto | Papel |
|---|---|
| `src/Minuta.Core` | Biblioteca com toda a lógica (configuração, mídia, transcrição, análise, documento). |
| `src/Minuta.Cli` | Programa de console `minuta`. |
| `src/Minuta.Desktop` | Interface WPF (Fase 4; por enquanto vazia). |
| `tests/Minuta.Core.Tests` | Testes xUnit; os de mídia geram vídeos sintéticos com o próprio FFmpeg. |

## Pré-requisitos

- .NET 10 SDK
- FFmpeg no PATH (`winget install Gyan.FFmpeg`) ou em `FFMPEG_PATH`
- Chave do [Groq](https://console.groq.com/keys) e do [OpenRouter](https://openrouter.ai/settings/keys) (ambos têm plano gratuito)

## Uso

Copie `.env.example` para `.env` e preencha as chaves.

```bash
dotnet run --project src/Minuta.Cli -- --checar
dotnet run --project src/Minuta.Cli -- caminho/do/video.mp4
```

## Testes

```bash
dotnet test
```

## Privacidade

O áudio vai para o Groq e o texto para o OpenRouter; modelos gratuitos podem ter política de dados diferente da dos pagos (inclusive uso para treino). Use apenas vídeos sem dados sensíveis, ou um plano com retenção zero, ou — quando existir — o modo local.
