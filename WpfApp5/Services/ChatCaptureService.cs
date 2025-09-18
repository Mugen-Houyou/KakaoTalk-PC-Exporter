using System;
using KakaoPcLogger.Data;
using KakaoPcLogger.Models;
using KakaoPcLogger.Parsing;

namespace KakaoPcLogger.Services
{
    public sealed class ChatCaptureService
    {
        private readonly ChatWindowInteractor _windowInteractor;
        private readonly ClipboardService _clipboardService;
        private readonly string _dbPath;

        public ChatCaptureService(ChatWindowInteractor windowInteractor, ClipboardService clipboardService, string dbPath)
        {
            _windowInteractor = windowInteractor;
            _clipboardService = clipboardService;
            _dbPath = dbPath;
        }

        public ChatCaptureResult Capture(ChatEntry entry)
        {
            if (!_windowInteractor.Validate(entry, out var warning))
            {
                return new ChatCaptureResult
                {
                    Success = false,
                    Warning = warning
                };
            }

            _windowInteractor.ActivateAndCopy(entry);

            string? clipboardText = _clipboardService.TryReadText();
            if (clipboardText is null)
            {
                return new ChatCaptureResult
                {
                    Success = false,
                    Warning = $"[경고] 클립보드 읽기 실패: {entry.Title}"
                };
            }

            string? dbMessage = null;
            string? dbError = null;

            try
            {
                ChatDatabase.EnsureDatabase(_dbPath);
                long chatId = ChatDatabase.GetOrCreateChatId(_dbPath, entry.Title);
                var parsed = ChatParser.ParseRaw(clipboardText);

                if (parsed.Count > 0)
                {
                    ChatDatabase.SaveMessages(_dbPath, chatId, parsed);
                    dbMessage = $"[DB] 저장됨: {parsed.Count}건 ({entry.Title})\n";
                }
                else
                {
                    dbMessage = "[DB] 날짜 구분이 없어 저장 생략\n";
                }
            }
            catch (Exception ex)
            {
                dbError = $"[DB 오류] {ex.GetType().Name}: {ex.Message}";
            }

            return new ChatCaptureResult
            {
                Success = true,
                ClipboardText = clipboardText,
                DbMessage = dbMessage,
                DbError = dbError
            };
        }
    }
}
