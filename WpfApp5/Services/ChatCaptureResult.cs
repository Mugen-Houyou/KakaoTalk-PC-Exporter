namespace KakaoPcLogger.Services
{
    public sealed class ChatCaptureResult
    {
        public bool Success { get; init; }
        public string? ClipboardText { get; init; }
        public string? Warning { get; init; }
        public string? DbMessage { get; init; }
        public string? DbError { get; init; }
    }
}
