# Minuta

Transforma vídeos de reunião (analista + cliente, gravados no Google Meet) em um documento Markdown curto e pesquisável para o desenvolvedor: resumo, decisões, regras de negócio, dúvidas em aberto, passos para reproduzir um erro, próximos passos e cards de tarefa — cada card citando o trecho do vídeo que o justifica.

> Em desenvolvimento. Fase 1 (console, vídeos com áudio) em andamento.

## Como funciona (Fase 1)

```
vídeo ──FFmpeg──▶ áudio (opus, mono, 16 kHz)
      ──Groq/Whisper──▶ transcrição com tempos [mm:ss]
      ──OpenRouter──▶ análise em JSON (validada por schema)
      ──Minuta──▶ confere as citações na transcrição ──▶ documento .md
```

- O modelo de IA devolve **dados estruturados**, não o Markdown pronto. O documento é montado pelo código, sempre no mesmo formato.
- Nada é inventado: o que não está no vídeo vira **dúvida em aberto**, e toda citação é conferida contra a transcrição.
- Na Fase 2, a mesma linha do tempo recebe também o que aparece na tela (frames analisados por modelo multimodal), para fala e tela se complementarem.

## Estrutura

| Projeto | Papel |
|---|---|
| `src/Minuta.Core` | Biblioteca com toda a lógica (configuração, mídia, transcrição, análise, documento). |
| `src/Minuta.Cli` | Programa de console `minuta` (Fase 1). |
| `src/Minuta.Desktop` | Interface WPF (Fase 3; por enquanto vazia). |
| `tests/Minuta.Core.Tests` | Testes xUnit da biblioteca. |

## Pré-requisitos

- .NET 10 SDK
- FFmpeg no PATH (`winget install Gyan.FFmpeg`) ou em `FFMPEG_PATH`
- Chave do [Groq](https://console.groq.com/keys) e do [OpenRouter](https://openrouter.ai/settings/keys)

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

Os vídeos de teste são simulados. O áudio vai para o Groq e a transcrição para o OpenRouter; modelos gratuitos podem ter política de dados diferente da dos pagos. Não use com dados reais de clientes sem rever as políticas de cada provedor.
