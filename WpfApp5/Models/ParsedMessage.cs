using System;

namespace KakaoPcLogger.Models
{
    public sealed class ParsedMessage
    {
        public string Sender { get; init; } = string.Empty;
        public DateTime LocalTs { get; init; }
        public string Content { get; init; } = string.Empty;
        public int MsgOrder { get; init; }
    }
}
