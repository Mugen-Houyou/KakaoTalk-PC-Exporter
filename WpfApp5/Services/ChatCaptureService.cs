using System;
using KakaoPcLogger.Data;
using KakaoPcLogger.Models;
using KakaoPcLogger.Parsing;

namespace KakaoPcLogger.Services
{
    public sealed class ChatCaptureService
    {
        private readonly ChatWindowInteractor _windowInteractor;
        private readonly string _dbPath;

        public ChatCaptureService(ChatWindowInteractor windowInteractor, string dbPath)
        {
            _windowInteractor = windowInteractor;
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

            if (!_windowInteractor.TryReadAllText(entry, out var capturedText, out var readWarning))
            {
                return new ChatCaptureResult
                {
                    Success = false,
                    Warning = readWarning ?? $"[경고] UI Automation 읽기 실패: {entry.Title}"
                };
            }

            capturedText ??= string.Empty;

            string? dbMessage = null;
            string? dbError = null;

            try
            {
                ChatDatabase.EnsureDatabase(_dbPath);
                long chatId = ChatDatabase.GetOrCreateChatId(_dbPath, entry.Title);
                var parsed = ChatParser.ParseRaw(capturedText);

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
                CapturedText = capturedText,
                Warning = readWarning,
                DbMessage = dbMessage,
                DbError = dbError
            };
        }
    }
}
