namespace API.Configuration;

/// <summary>
/// Publishes how loud Bubbles' speech is, frame by frame, so the display can move a mouth
/// that corresponds to the audio instead of one shaped to look like speech.
/// </summary>
public class SpeechEnvelopeOptions
{
    public const string SectionName = "SpeechEnvelope";

    /// <summary>
    /// Master switch. Off, nothing is published and displays keep their canned animations.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Milliseconds of audio per frame. 50ms is about the shortest a mouth can usefully
    /// react to, and keeps a whole sentence's envelope down to a few hundred bytes.
    /// </summary>
    public int FrameMs { get; set; } = 50;

    /// <summary>
    /// Quietest level that still registers, in dBFS. Below this a frame reads as closed.
    /// Room for TTS noise floor without the mouth twitching between words.
    /// </summary>
    public double FloorDb { get; set; } = -45;

    /// <summary>
    /// Level that counts as fully open, in dBFS. Speech peaks well below 0dB, so mapping
    /// the top of the range to 0 would leave the mouth permanently half shut.
    /// </summary>
    public double CeilingDb { get; set; } = -8;

    /// <summary>
    /// How far behind the queued audio the audible audio runs. Audio is handed to the
    /// output as fast as it synthesises, so without this the mouth leads the sound by the
    /// depth of the output buffer. Roughly PipeWire's configured playback latency plus
    /// whatever pw-cat holds; tune by watching it rather than by calculation.
    /// </summary>
    public int OutputLatencyMs { get; set; } = 120;
}
