using System;
using System.Linq;
using System.Threading.Tasks;
using MeetingTranslator.Models;
using MeetingTranslator.Services.Azure;
using MeetingTranslator.Services.Common;
using MeetingTranslator.Services.OpenAI;
using OpenAiVoiceService = MeetingTranslator.Services.OpenAI.VoiceTranslationService;
using AzureVoiceService = MeetingTranslator.Services.Azure.VoiceTranslationService;

namespace MeetingTranslator.ViewModels;

public partial class MainViewModel
{
    public async Task ToggleConnectionAsync()
    {
        if (IsConnected)
        {
            await DisconnectAsync();
        }
        else
        {
            await ConnectAsync();
        }
    }

    private async Task ConnectAsync()
    {
        LoadEnvironmentVariables();

        try
        {
            if (_useAzureProvider)
            {
                if (string.IsNullOrWhiteSpace(AzureSpeechKey) || string.IsNullOrWhiteSpace(AzureSpeechRegion))
                {
                    StatusText = "⚠ AZURE_SPEECH_KEY/REGION não configuradas no .env";
                    return;
                }

                if (SelectedMode == TranslationMode.Voice)
                    await ConnectAzureVoiceModeAsync();
                else
                    await ConnectAzureTranscriptionAsync();
            }
            else
            {
                var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
                if (string.IsNullOrWhiteSpace(apiKey))
                {
                    StatusText = "⚠ OPENAI_API_KEY não encontrada";
                    return;
                }

                if (SelectedMode == TranslationMode.Voice)
                    await ConnectVoiceModeAsync(apiKey);
                else
                    await ConnectTranscriptionModeAsync(apiKey);
            }

            IsConnected = true;
            StatusText = "Pronto — ouvindo...";
        }
        catch (Exception ex)
        {
            StatusText = $"⚠ Erro: {ex.Message}";
        }
    }

    private async Task ConnectVoiceModeAsync(string apiKey)
    {
        _voiceService = new OpenAiVoiceService(apiKey, _sharedAudioState);

        _voiceService.TranscriptReceived += OnTranscriptReceived;
        _voiceService.StatusChanged += OnStatusChanged;
        _voiceService.ErrorOccurred += OnError;
        _voiceService.AnalyzingChanged += OnAnalyzingChanged;

        await _voiceService.StartAsync(
            SelectedMicDevice?.DeviceIndex ?? 0,
            SelectedLoopbackDevice?.DeviceIndex ?? 0,
            UseMic,
            UseLoopback
        );
    }

    private async Task ConnectTranscriptionModeAsync(string apiKey)
    {
        _transcriptionService = new TextTranslationService(apiKey);

        _transcriptionService.TranscriptReceived += OnTranscriptReceived;
        _transcriptionService.StatusChanged += OnStatusChanged;
        _transcriptionService.ErrorOccurred += OnError;
        _transcriptionService.AnalyzingChanged += OnAnalyzingChanged;

        await _transcriptionService.StartAsync(
            SelectedMicDevice?.DeviceIndex ?? 0,
            SelectedLoopbackDevice?.DeviceIndex ?? 0,
            UseMic,
            UseLoopback
        );
    }

    private async Task ConnectAzureTranscriptionAsync()
    {
        _azureTranscriptionService = new AzureTranscriptionService(AzureSpeechKey, AzureSpeechRegion);

        _azureTranscriptionService.TranscriptReceived += OnTranscriptReceived;
        _azureTranscriptionService.StatusChanged += OnStatusChanged;
        _azureTranscriptionService.ErrorOccurred += OnError;
        _azureTranscriptionService.AnalyzingChanged += OnAnalyzingChanged;

        await _azureTranscriptionService.StartAsync(
            SelectedMicDevice?.DeviceIndex ?? 0,
            SelectedLoopbackDevice?.DeviceIndex ?? 0,
            UseMic,
            UseLoopback
        );
    }

    private async Task ConnectAzureVoiceModeAsync()
    {
        _azureVoiceService = new AzureVoiceService(AzureSpeechKey, AzureSpeechRegion, _sharedAudioState);

        // Força EN -> PT-BR para ambos os fluxos (requisito da Entrada)
        _azureVoiceService.SetDirections("en-US", "pt-BR", "en-US", "pt-BR");

        // Aplica a voz selecionada para ambos (independe de qual entrada foi escolhida)
        if (!string.IsNullOrWhiteSpace(AzureSpeechVoice))
        {
            _azureVoiceService.SetBothVoices(AzureSpeechVoice);
        }

        _azureVoiceService.TranscriptReceived += OnTranscriptReceived;
        _azureVoiceService.StatusChanged += OnStatusChanged;
        _azureVoiceService.ErrorOccurred += OnError;
        _azureVoiceService.AnalyzingChanged += OnAnalyzingChanged;

        await _azureVoiceService.StartAsync(
            SelectedMicDevice?.DeviceIndex ?? 0,
            SelectedLoopbackDevice?.DeviceIndex ?? 0,
            UseMic,
            UseLoopback
        );
    }

    private async Task DisconnectAsync()
    {
        if (_voiceService != null)
        {
            _voiceService.TranscriptReceived -= OnTranscriptReceived;
            _voiceService.StatusChanged -= OnStatusChanged;
            _voiceService.ErrorOccurred -= OnError;
            _voiceService.AnalyzingChanged -= OnAnalyzingChanged;

            await _voiceService.StopAsync();
            _voiceService.Dispose();
            _voiceService = null;
        }

        if (_transcriptionService != null)
        {
            _transcriptionService.TranscriptReceived -= OnTranscriptReceived;
            _transcriptionService.StatusChanged -= OnStatusChanged;
            _transcriptionService.ErrorOccurred -= OnError;
            _transcriptionService.AnalyzingChanged -= OnAnalyzingChanged;

            await _transcriptionService.StopAsync();
            _transcriptionService.Dispose();
            _transcriptionService = null;
        }

        if (_azureVoiceService != null)
        {
            _azureVoiceService.TranscriptReceived -= OnTranscriptReceived;
            _azureVoiceService.StatusChanged -= OnStatusChanged;
            _azureVoiceService.ErrorOccurred -= OnError;
            _azureVoiceService.AnalyzingChanged -= OnAnalyzingChanged;

            await _azureVoiceService.StopAsync();
            _azureVoiceService.Dispose();
            _azureVoiceService = null;
        }

        if (_azureTranscriptionService != null)
        {
            _azureTranscriptionService.TranscriptReceived -= OnTranscriptReceived;
            _azureTranscriptionService.StatusChanged -= OnStatusChanged;
            _azureTranscriptionService.ErrorOccurred -= OnError;
            _azureTranscriptionService.AnalyzingChanged -= OnAnalyzingChanged;

            await _azureTranscriptionService.StopAsync();
            _azureTranscriptionService.Dispose();
            _azureTranscriptionService = null;
        }

        IsConnected = false;
        SubtitleText = "";
        StatusText = "Desconectado";
    }

    private void OnTranscriptReceived(object? sender, TranscriptEventArgs e)
    {
        // Log via channel batched
        var speakerTag = e.SpeakerId != null ? $" [{e.SpeakerId}]" : "";
        var logLine = $"[{DateTime.Now:HH:mm:ss}] IsPartial={e.IsPartial}, Speaker={e.Speaker}{speakerTag}, " +
                      $"Original=\"{e.OriginalText}\", Translated=\"{e.TranslatedText}\"";
        _logChannel.Writer.TryWrite(("transcripts.log", logLine));

        if (e.IsPartial)
        {
            // No modo Transcrição, mostramos o OriginalText para feedback instantâneo
            string textToShow = (SelectedMode == TranslationMode.Transcription && !string.IsNullOrWhiteSpace(e.OriginalText))
                ? e.OriginalText
                : e.TranslatedText;

            _pendingPartialText = textToShow;

            if (e.Speaker == Speaker.You) _partialTranscriptYou = textToShow;
            else _partialTranscriptThem = textToShow;

            if (!_partialUpdateScheduled)
            {
                _partialUpdateScheduled = true;
                _dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Normal, () =>
                {
                    _partialUpdateScheduled = false;
                    var text = _pendingPartialText;
                    if (text != null)
                    {
                        IsAnalyzing = false;
                        IsAssistantTyping = true;
                        SubtitleText = text;
                    }
                });
            }
        }
        else
        {
            var translatedText = e.TranslatedText;
            var originalText = e.OriginalText;
            var speaker = e.Speaker;
            var speakerId = e.SpeakerId;

            _dispatcher.BeginInvoke(() =>
            {
                IsAnalyzing = false;
                IsAssistantTyping = false;
                _pendingPartialText = null;

                var lastPartial = (speaker == Speaker.You) ? _partialTranscriptYou : _partialTranscriptThem;
                var finalText = string.IsNullOrEmpty(translatedText) ? lastPartial : translatedText;

                SubtitleText = finalText;

                if (!string.IsNullOrWhiteSpace(finalText))
                {
                    History.Add(new ConversationEntry
                    {
                        Speaker = speaker,
                        SpeakerId = speakerId,
                        OriginalText = originalText ?? "",
                        TranslatedText = finalText
                    });

                    // Trigger AutoWhisper check after finalizing a sentence
                    _ = RunAutoWhisperAsync(force: false);
                }

                if (speaker == Speaker.You) _partialTranscriptYou = "";
                else _partialTranscriptThem = "";
            });
        }
    }

    private void OnAnalyzingChanged(object? sender, bool isAnalyzing)
    {
        _dispatcher.BeginInvoke(() =>
        {
            IsAnalyzing = isAnalyzing;
        });
    }

    private void OnStatusChanged(object? sender, StatusEventArgs e)
    {
        _dispatcher.BeginInvoke(() => StatusText = e.Message);
    }

    private void OnError(object? sender, StatusEventArgs e)
    {
        // Log via channel batched
        var errorMsg = e.Message;
        _logChannel.Writer.TryWrite(("error.log", $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {errorMsg}"));

        _dispatcher.BeginInvoke(() =>
        {
            IsAnalyzing = false;
            StatusText = $"⚠ {errorMsg}";
        });
    }
}
