# Minuta

Transforma vídeos gravados em um documento Markdown curto e pesquisável **para desenvolvimento**: resumo, passos para reproduzir um erro, regras de negócio, decisões, dúvidas em aberto, cards de tarefa com critérios de aceite e próximos passos. Cada item cita o momento do vídeo que o justifica.

O foco são os **vídeos de análise e de relato de erro**: alguém mostra um fluxo de sistema na tela e fala sobre ele. Funciona com o arquivo, venha de onde vier (gravação de tela, vídeo enviado pelo cliente, Teams, Zoom, Meet, áudio).

**E as reuniões ao vivo?** Ferramentas como o Claude Meetings já tomam notas e geram ações em chamadas ao vivo; o Minuta não compete com elas. Para reuniões, o plano é **importar a transcrição pronta** dessas ferramentas (ou um `.vtt`/`.srt`) e gerar a partir dela o mesmo documento de desenvolvimento, com as citações conferidas.

> Em desenvolvimento. Fase 1 (console, vídeos com áudio) concluída.

## Como funciona

```
vídeo ─▶ trilha de áudio ── FFmpeg ─▶ Whisper (Groq) ─▶ falas [mm:ss] ──┐
      └▶ trilha visual ─── FFmpeg ─▶ modelo de visão ─▶ telas [mm:ss] ──┴▶ linha do tempo
                           (Fase 2)
linha do tempo ─▶ modelo de linguagem (OpenRouter) ─▶ JSON validado ─▶ conferência das citações ─▶ perfil ─▶ .md
```

- **Nada é inventado.** O que não está no vídeo vira *dúvida em aberto*, e toda citação é conferida contra a transcrição; a que não bater é sinalizada.
- **O modelo devolve dados, não texto pronto.** O Markdown é montado pelo código, a partir de um **perfil**:

| Perfil | Para |
|---|---|
| Análise para desenvolvimento (`--perfil analise`, padrão) | resumo, passos para reproduzir, regras de negócio, decisões, dúvidas, cards de tarefa e próximos passos |
| Relato de erro *(planejado)* | passos para reproduzir, esperado × obtido |
| Ata de reunião (`--perfil ata`) | resumo, decisões, próximos passos e pendências (perfil simples, secundário) |

Perfis personalizados podem ficar em pasta local, fora do repositório.

## Roteiro

| Fase | Entrega | Situação |
|---|---|---|
| 1 | Console: vídeo com áudio → transcrição → documento, perfis *Análise* e *Ata* | concluída |
| 2 | Trilha visual: frames por mudança de tela e por menção na fala, analisados por modelo multimodal; também vídeos sem fala | **próxima** |
| 3 | Áudio longo (divisão acima do limite de upload, ~1h40) | quando houver necessidade |
| 4 | Interface desktop (WPF) | planejada |
| depois | **Importar transcrição pronta** (Claude Meetings, `.vtt`/`.srt`) para reuniões, provedores configuráveis, **modo local** (nada sai da máquina), gravação de tela com registro dos cliques | ideias |

Junto com a Fase 2: ajustes do prompt para vídeos de análise. Depois: perfis personalizados lidos de pasta local.

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
dotnet run --project src/Minuta.Cli -- caminho/do/video.mp4 --perfil ata
```

Ao lado do vídeo ficam `video.minuta.md` (o documento), `video.transcricao.txt`, a pasta `video.frames/` com as telas escolhidas e os arquivos de cache (`.transcricao.json`, `.visao.json`, `.analise.json`). Tudo é reaproveitado: trocar de perfil não chama nenhuma API (`--retranscrever`, `--redescrever` e `--reanalisar` refazem cada parte).

**Telas (Fase 2, em andamento).** O Minuta escolhe os frames em que a tela muda e um modelo de visão descreve cada um (janela, caminho do menu, campos, mensagens). Use `--sem-visao` para não enviar nenhuma imagem.

**Glossário (opcional).** Se o Whisper erra siglas ou nomes do seu dia a dia, liste os termos certos em `glossario.txt` ao lado do `.env`, um por linha (modelo em `glossario.exemplo.txt`). O arquivo fica fora do git. Mudar o glossário faz o vídeo ser transcrito de novo.

## Testes

```bash
dotnet test
```

## Privacidade

O áudio vai para o Groq; o texto e as imagens das telas, para o OpenRouter. Modelos gratuitos podem ter política de dados diferente da dos pagos (inclusive uso para treino). A tela costuma mostrar mais dados que a fala: use `--sem-visao` quando ela tiver dados sensíveis. Use apenas vídeos sem dados sensíveis, ou um plano com retenção zero, ou — quando existir — o modo local.
