# Minuta — instruções para agentes

## Objetivo
Transformar vídeos de reunião (analista + cliente, ~10 min, gravados no Google Meet) em um documento `.md` curto e pesquisável para o desenvolvedor: resumo, decisões, regras de negócio, dúvidas em aberto, passos para reproduzir um erro, próximos passos e cards de tarefa.

Foco: vídeos em que o analista **mostra o fluxo e fala sobre ele**.

## Escopo
- Aplicação Windows em C# / .NET 10.
- Lógica em uma biblioteca (`Minuta.Core`), usada pelo console e, depois, pela interface WPF.
- Transcrição: Whisper via Groq. Análise: modelo via OpenRouter.
- Apenas vídeos simulados nos testes. Nenhum dado real de cliente no repositório.

## Fases
Não comece uma fase antes de a anterior funcionar.
1. **MVP (console):** vídeo COM áudio → extração do áudio (FFmpeg) → transcrição → `.md` com resumo, decisões, regras de negócio, dúvidas em aberto, próximos passos e cards de tarefa.
2. Vídeos de erro sem áudio: amostragem de frames + análise por modelo multimodal.
3. Interface desktop simples (WPF).
4. Depois: gravação/captura direta pelo app.

## Regras de trabalho
- Plano curto primeiro; código só depois da aprovação do desenvolvedor.
- Uma etapa por vez. Ao terminar: parar, dizer como testar e sugerir a mensagem de commit.
- Antes de decidir sobre Groq, Whisper ou OpenRouter, consultar a documentação oficial atual (custos, limites, tamanho máximo de arquivo, tratamento dos dados).
- Chaves só em variáveis de ambiente ou `.env`. `.env` no `.gitignore`; manter `.env.example` atualizado.
- **Não inventar.** O que não estiver no vídeo vira dúvida em aberto. Cada card cita o trecho da transcrição (com tempo) que o justifica.
- Tratar: arquivo inválido, FFmpeg ausente, falha de rede, resposta inválida da API.
- Evitar dependências e abstrações desnecessárias.
- Responder em português.
