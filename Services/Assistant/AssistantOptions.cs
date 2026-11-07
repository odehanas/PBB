namespace GovBudget.Services.Assistant
{
    /// <summary>
    /// Configuration for the in-app assistant. Bound from the "Assistant" section of
    /// appsettings; the API key is expected to come from user secrets, an environment
    /// variable (OPENAI_API_KEY) or the hosting platform, never from source control.
    /// </summary>
    public sealed class AssistantOptions
    {
        public const string SectionName = "Assistant";

        public bool Enabled { get; set; } = true;

        public string ApiKey { get; set; } = "";

        public string Model { get; set; } = "gpt-4o-mini";

        public string BaseUrl { get; set; } = "https://api.openai.com/v1";

        /// <summary>How many times the model may call tools before it must answer.</summary>
        public int MaxToolRounds { get; set; } = 8;

        /// <summary>Conversation turns kept per user session (user + assistant messages).</summary>
        public int MaxHistoryMessages { get; set; } = 20;

        public int TimeoutSeconds { get; set; } = 90;

        /// <summary>Maximum rows any data tool returns to the model.</summary>
        public int MaxRows { get; set; } = 200;

        /// <summary>Shows the microphone and read-aloud buttons. Uses the same API key as the chat.</summary>
        public bool VoiceEnabled { get; set; } = true;

        public string TranscriptionModel { get; set; } = "gpt-4o-mini-transcribe";

        public string SpeechModel { get; set; } = "gpt-4o-mini-tts";

        /// <summary>
        /// OpenAI voice. Female: coral (default), nova, shimmer, sage. Male: ash, echo, onyx, ballad.
        /// </summary>
        public string SpeechVoice { get; set; } = "coral";

        /// <summary>
        /// How the voice should sound. Only the gpt-4o-* speech models use it; leave empty for none.
        /// </summary>
        public string SpeechInstructions { get; set; } =
            "Speak like a friendly, professional colleague having a natural conversation: warm, relaxed, " +
            "clear, at a comfortable pace, with natural pauses. Read large figures the way a person would say them. " +
            "If the text is Arabic, speak natural Modern Standard Arabic.";

        /// <summary>
        /// Live voice: the microphone opens a real-time conversation (OpenAI Realtime over WebRTC)
        /// instead of record-then-answer. The browser talks to api.openai.com directly with a
        /// short-lived key minted by the server, so users' PCs must be able to reach it. When
        /// false, the microphone falls back to the record-then-answer voice above.
        /// </summary>
        public bool RealtimeEnabled { get; set; } = true;

        /// <summary>OpenAI realtime model (the "mini" tier, as used by RealEstateManager in October 2026).</summary>
        public string RealtimeModel { get; set; } = "gpt-realtime-2.1-mini";

        /// <summary>Realtime voice. Female: marin (default), coral, sage, shimmer. Male: cedar, ash, echo, verse.</summary>
        public string RealtimeVoice { get; set; } = "marin";

        /// <summary>A live conversation ends after this many minutes; the user can start another.</summary>
        public int RealtimeMaxSessionMinutes { get; set; } = 15;

        /// <summary>Data lookups one live conversation may run.</summary>
        public int RealtimeMaxToolCalls { get; set; } = 60;

        /// <summary>Live conversations one user may start per day, to keep the cost bounded.</summary>
        public int RealtimeDailySessionsPerUser { get; set; } = 40;

        public bool IsRealtimeConfigured => IsConfigured && VoiceEnabled && RealtimeEnabled;

        public bool OecdLiveEnabled { get; set; } = true;

        /// <summary>SDMX REST endpoint of the OECD Data Explorer.</summary>
        public string OecdApiBaseUrl { get; set; } = "https://sdmx.oecd.org/public/rest";

        /// <summary>Hosts the OECD page-reader tool may fetch. Suffix match on the host.</summary>
        public string[] OecdAllowedHosts { get; set; } = new[] { "oecd.org", "sdmx.oecd.org", "data-explorer.oecd.org" };

        /// <summary>Offline OECD/PBB reference document, relative to the content root.</summary>
        public string ReferenceDocPath { get; set; } = "docs/OECD_PBB_Reference.md";

        public bool IsConfigured => Enabled && !string.IsNullOrWhiteSpace(ApiKey);
    }
}
