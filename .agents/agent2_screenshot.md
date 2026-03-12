# Agente 2: Interface e Lógica de Captura de Tela (Print)

**Objetivo:** Permitir que o usuário tire um print da tela e selecione uma área (crop) diretamente pelo aplicativo.

## Tarefas a realizar:
- [ ] Criar uma nova janela WPF (`ScreenCaptureWindow.xaml`) transparente.
- [ ] Implementar a lógica de clique e arraste para desenhar um retângulo de seleção sobre a tela.
- [ ] Usar bibliotecas nativas (`System.Drawing` ou equivalentes no .NET) para capturar as coordenadas selecionadas e gerar um bitmap (imagem).
- [ ] Adicionar botões no `MainWindow.xaml` para ativar o modo de captura de tela.
