# 🎙️ Speech Translation Ecosystem

Este repositório contém um conjunto de ferramentas poderosas para tradução e interpretação de reuniões em tempo real, utilizando as mais recentes tecnologias de IA generativa e processamento de áudio.

---

## 📺 Demonstração

Confira o projeto em ação neste vídeo:

[![Demonstração do Projeto](https://img.youtube.com/vi/5ARUsHx-Epc/0.jpg)](https://www.youtube.com/watch?v=5ARUsHx-Epc)

---

## 🚀 Projetos Principais

### 1. MeetingTranslator (Multi-Provider & Avançado)
Uma suíte completa e versátil para tradução, análise e produtividade em reuniões.

- **Múltiplos Provedores**: Suporte para OpenAI (Realtime API), Azure Speech Services e Google Cloud.
- **Análise Visual (OCR/Vision)**: Captura de tela e recorte de área para análise imediata por IA (ex: traduzir slides ou explicar gráficos).
- **Modo Furtivo (Stealth)**: Torna a aplicação invisível em capturas de tela e compartilhamentos, remove o ícone da barra de tarefas e oculta o cursor sobre a janela.
- **Auto-Whisper**: Fornece dicas e informações contextuais em tempo real baseadas no fluxo da conversa.
- **Resumo Inteligente**: Histórico completo com funcionalidade de resumo automático via IA.
- **Controle de Sistema**: Capacidade de mutar o microfone em nível de sistema operacional simultaneamente ao aplicativo.

### 2. MeetingGoogle (Simultâneo & Live)
Uma aplicação leve focada em ultra-baixa latência, utilizando a nova API **Gemini 1.5 Multimodal Live**.

- **Tradução Nativa de Áudio**: O Gemini processa o áudio e gera a tradução com voz natural diretamente via WebSocket (Native Audio Output).
- **Simultaneidade**: Tradução de áudio para áudio e texto com latência mínima.
- **Interface Flutuante**: Janela compacta, sempre no topo (Topmost), ideal para sobrepor a aplicativos de reunião (Teams, Zoom, Google Meet).
- **Suporte a Loopback**: Traduza o áudio do sistema (o que as outras pessoas estão falando) ou do seu próprio microfone.

---

## 🛠️ Tecnologias Utilizadas

- **Framework**: .NET 8.0 / 9.0 (WPF)
- **Áudio**: [NAudio](https://github.com/naudio/NAudio) para captura, loopback (WASAPI) e reprodução.
- **IA/ML**: 
  - Google Gemini 1.5 Pro/Flash (WebSockets & REST)
  - OpenAI Realtime API
  - Azure Cognitive Services (Speech & Translation)
- **Configuração**: `dotenv.net` para gerenciamento de chaves de serviço.

---

## ⚙️ Configuração

1. Clone o repositório.
2. Crie um arquivo `.env` na raiz do repositório (ou dentro das pastas dos projetos).
3. Adicione suas chaves de API:

```env
# Google Gemini
GEMINI_API_KEY=sua_chave_aqui

# OpenAI
OPENAI_API_KEY=sua_chave_aqui

# Azure Speech
AZURE_SPEECH_KEY=sua_chave_aqui
AZURE_SPEECH_REGION=eastus
```

---

## 🏃 Como Executar

1. Certifique-se de ter o **.NET 8 SDK** (ou superior) instalado.
2. Abra a solução `speech.sln` no Visual Studio ou VS Code.
3. Restaure os pacotes NuGet:
   ```bash
   dotnet restore
   ```
4. Compile e rode o projeto desejado:
   ```bash
   # Para o MeetingGoogle
   dotnet run --project MeetingGoogle/MeetingGoogle.csproj

   # Para o MeetingTranslator
   dotnet run --project MeetingTranslator/MeetingTranslator.csproj
   ```

---

## 📂 Estrutura de Pastas

- `MeetingGoogle/`: Código fonte do tradutor focado em Gemini Live.
- `MeetingTranslator/`: Código fonte da suíte avançada multi-provedor.
- `Services/`: Implementações de lógica de negócio e integração com APIs.
- `ViewModels/`: Lógica de interface (MVVM).
- `Converters/`: Conversores de dados para XAML.

---

## 📝 Licença
Este projeto é para fins de desenvolvimento e pesquisa em tradução em tempo real.
