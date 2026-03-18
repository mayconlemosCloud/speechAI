using MeetingTranslator.Models;

namespace MeetingTranslator.Services.Common;

/// <summary>
/// Dados de transcrição/tradução recebidos de um serviço.
/// </summary>
public readonly record struct TranscriptEventArgs
{
    public Speaker Speaker { get; init; }
    public string OriginalText { get; init; }
    public string TranslatedText { get; init; }
    public bool IsPartial { get; init; }
    /// <summary>ID de speaker da diarização Azure (ex: "Guest-1"). Null quando não há diarização.</summary>
    public string? SpeakerId { get; init; }
}

/// <summary>
/// Mudança de status de um serviço.
/// </summary>
public readonly record struct StatusEventArgs
{
    public string Message { get; init; }
}
