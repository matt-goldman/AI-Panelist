namespace Shared;

public static class Messages
{
    public const string UpdateConversationState = "UpdateConversationState";
    public const string UpdatePanelState = "UpdatePanelState";
    public const string TestCustomState = "TestCustomState";
    public const string SetThinkingStateAnimation = "SetThinkingStateAnimation";
    public const string SetSpeakingStateAnimation = "SetSpeakingStateAnimation";
    public const string IntroduceSelf = "IntroduceSelf";

    /// <summary>
    /// A <see cref="SpeechEnvelope"/>: how loud Bubbles' audio is, and when, so a display
    /// can drive a mouth from it.
    /// </summary>
    public const string SpeechEnvelope = "SpeechEnvelope";

    /// <summary>
    /// Bubbles has genuinely finished speaking. Until this arrives, a gap in the envelope
    /// is the seam between chunks, not the end of the answer.
    /// </summary>
    public const string SpeechComplete = "SpeechComplete";
}
