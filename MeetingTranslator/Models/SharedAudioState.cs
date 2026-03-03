namespace MeetingTranslator.Models;

/// <summary>
/// Estado compartilhado entre RealtimeService e SpeakTranslateService.
///
/// Problema resolvido:
/// Quando ambos os serviços estão ativos e usam o mesmo dispositivo de áudio
/// (ex: SpeakTranslate output → Auscultadores, RealtimeService loopback → Auscultadores),
/// o loopback captura o áudio que o SpeakTranslateService está tocando.
/// Isso cria um feedback: EN audio → loopback → RealtimeService → traduz EN→PT → toca PT
/// → o usuário ouve sua própria voz traduzida de volta.
///
/// Solução: cada serviço sinaliza quando está tocando áudio.
/// O outro serviço usa essa informação para gatar captura.
/// </summary>
public class SharedAudioState
{
    /// <summary>
    /// True quando o RealtimeService está tocando áudio de resposta.
    /// SpeakTranslateService verifica isso para gatar o mic (evitar captar a tradução do Realtime).
    /// </summary>
    public volatile bool RealtimePlaybackActive;

    /// <summary>
    /// True quando o SpeakTranslateService está tocando áudio de resposta.
    /// RealtimeService verifica isso para gatar o loopback (evitar captar a tradução do Speak).
    /// </summary>
    public volatile bool SpeakPlaybackActive;

    /// <summary>
    /// True quando o SpeakTranslateService está em cooldown pós-playback.
    /// RealtimeService também deve gatar o loopback durante o cooldown,
    /// pois o eco do speaker ainda pode estar no ar.
    /// </summary>
    public volatile bool SpeakCooldownActive;

    /// <summary>
    /// True quando o SpeakTranslateService (intérprete) está CONECTADO e ativo.
    /// Quando true, o RealtimeService NÃO processa áudio do mic — apenas loopback.
    /// Motivo: ambos os serviços capturam o mesmo mic. Sem este gate,
    /// a voz do usuário é traduzida por ambos simultaneamente → duas vozes.
    /// O intérprete cuida da voz do usuário (PT→EN).
    /// O RealtimeService cuida do áudio remoto via loopback (EN→PT).
    /// </summary>
    public volatile bool SpeakServiceActive;

    /// <summary>
    /// True se qualquer serviço está tocando/em cooldown.
    /// </summary>
    public bool IsAnyExternalPlaybackActive =>
        RealtimePlaybackActive || SpeakPlaybackActive || SpeakCooldownActive;
}
