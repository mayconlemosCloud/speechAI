# Nova Feature: Análise de Conversa e Recorte de Tela com IA

Este documento descreve o planejamento e a divisão de tarefas (agentes) para implementar a nova funcionalidade de análise inteligente e captura de tela no MeetingTranslator.

## Visão Geral
A nova funcionalidade permitirá que o sistema analise o histórico da conversa e ofereça suporte para envio de recortes de tela (screenshots) para uma IA analisar. Além disso, introduziremos o suporte ao **Google Gemini**, que oferece uma camada gratuita generosa e excelente suporte a visão computacional (multimodal).

### Sobre a IA Gemini (Gratuita) e Limitações
- **Possibilidade:** O Google Gemini (como o modelo `gemini-1.5-flash` ou `gemini-1.5-pro`) possui uma API gratuita (Google AI Studio) ideal para projetos de baixo custo.
- **Vantagens:** Excelente análise multimodal (texto + imagens), permitindo enviar a imagem do print da tela junto com o prompt de análise.
- **Limitações da versão grátis:**
  - Limite de requisições por minuto (RPM) e por dia (RPD) - geralmente 15 RPM e 1.500 RPD no tier gratuito do AI Studio.
  - Os dados enviados para a API gratuita podem ser usados pelo Google para treinar modelos (não recomendado para dados empresariais altamente sensíveis sem uma conta paga no Google Cloud/Vertex AI).
  - Pode haver latência um pouco maior dependendo do volume de uso regional.

---

## Definição dos Agentes (Plano de Ação)

A implementação desta grande feature foi dividida em **5 Agentes** para garantir entregas incrementais e seguras.

### Agente 1: Infraestrutura do Gemini e Análise de Texto
**Objetivo:** Integrar a API do Google Gemini e criar a lógica básica de análise da conversa.
- [ ] Adicionar um novo serviço `GeminiService` (ou `GoogleAILanguageService`) no diretório `Services/`.
- [ ] Criar métodos para enviar blocos de texto para o Gemini (ex: "Resuma esta reunião", "Quais as tarefas pendentes?").
- [ ] Configurar a chave de API do Gemini no `.env` (`GEMINI_API_KEY`).
- [ ] Criar os modelos (Models) de requisição e resposta para a API do Gemini.

### Agente 2: Interface e Lógica de Captura de Tela (Print)
**Objetivo:** Permitir que o usuário tire um print da tela e selecione uma área (crop) diretamente pelo aplicativo.
- [ ] Criar uma nova janela WPF (`ScreenCaptureWindow.xaml`) transparente.
- [ ] Implementar a lógica de clique e arraste para desenhar um retângulo de seleção sobre a tela.
- [ ] Usar bibliotecas nativas (`System.Drawing` ou equivalentes no .NET) para capturar as coordenadas selecionadas e gerar um bitmap (imagem).
- [ ] Adicionar botões no `MainWindow.xaml` para ativar o modo de captura de tela.

### Agente 3: Integração Visão Computacional (Screenshot + Gemini)
**Objetivo:** Conectar a imagem capturada pelo usuário com a análise do Gemini.
- [ ] Estender o `GeminiService` para suportar queries multimodais (Texto + Imagem).
- [ ] Converter o bitmap/imagem gerada no Agente 2 para o formato base64 esperado pela API da IA.
- [ ] Refinar o prompt ("Analise a imagem a seguir e explique o que está na tela").
- [ ] Exibir o resultado da análise em um novo painel ou pop-up na interface principal.

### Agente 4: Atualização da UI para Análise de Conversa e Resultados
**Objetivo:** Criar a interface para o usuário interagir com a análise da conversa e os prints.
- [ ] Criar um painel lateral ou aba de "Análise de IA" no arquivo `MainWindow.xaml`.
- [ ] Criar comandos no `MainViewModel.cs` para processar a "Análise Completa da Reunião" e "Analisar Imagem".
- [ ] Exibir indicadores de carregamento (loading spinners) enquanto a IA processa a resposta.
- [ ] Adicionar suporte a formatação Markdown no resultado retornado pela IA (se necessário, usando um renderizador básico).

### Agente 5: Refinamentos UX, Tratamento de Erros e Limites
**Objetivo:** Polir a funcionalidade, lidar com as limitações da API gratuita e melhorar a experiência.
- [ ] Adicionar tratamento de erros elegante caso a API do Gemini atinja o limite de quota (Rate Limit).
- [ ] Adicionar atalhos de teclado (ex: `Ctrl+Shift+S`) para chamar a ferramenta de captura de tela rapidamente.
- [ ] Permitir que o usuário digite uma pergunta customizada (prompt) antes de enviar a imagem capturada para a IA (ex: "Traduza o texto desta imagem").
- [ ] Limpeza de código e testes finais da funcionalidade.
