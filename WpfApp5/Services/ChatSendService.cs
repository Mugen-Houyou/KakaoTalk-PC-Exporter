using KakaoPcLogger.Models;

namespace KakaoPcLogger.Services
{
    public sealed class ChatSendService
    {
        private readonly ChatWindowInteractor _windowInteractor;
        private readonly string _inputClassName;

        public ChatSendService(ChatWindowInteractor windowInteractor, string inputClassName = "RICHEDIT50W")
        {
            _windowInteractor = windowInteractor;
            _inputClassName = inputClassName;
        }

        public bool TrySendMessage(ChatEntry entry, string message, out string? error)
            => _windowInteractor.TrySendMessage(entry, message, _inputClassName, out error);
    }
}
