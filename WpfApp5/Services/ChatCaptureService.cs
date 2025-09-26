using System;
using System.Collections.Generic;
using System.Linq;
using KakaoPcLogger.Data;
using KakaoPcLogger.Interop;
using KakaoPcLogger.Models;
using KakaoPcLogger.Parsing;

namespace KakaoPcLogger.Services
{
    public sealed class ChatCaptureService
    {
        private readonly ChatWindowInteractor _windowInteractor;
        private readonly ClipboardService _clipboardService;
        private readonly string _dbPath;
        private readonly ChatWindowScanner _scanner;

        // 엔트리 캐시 (짧은 TTL)
        private IReadOnlyList<ChatEntry> _entryCache = Array.Empty<ChatEntry>();
        private DateTime _cacheAtUtc = DateTime.MinValue;
        private readonly TimeSpan _cacheTtl = TimeSpan.FromSeconds(5);

        public ChatCaptureService(
            ChatWindowInteractor windowInteractor,
            ClipboardService clipboardService,
            string dbPath,
            ChatWindowScanner scanner)   // 스캐너 주입
        {
            _windowInteractor = windowInteractor;
            _clipboardService = clipboardService;
            _dbPath = dbPath;
            _scanner = scanner;
        }

        // 기존 라운드로빈 진입점 (그대로 유지)
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

        // 작업표시줄 FLASH/REDRAW 이벤트용 트리거
        public ChatCaptureResult CaptureByHwnd(IntPtr anyHwnd)
        {
            // 최상위 루트 HWND로 승격
            var root = NativeMethods.GetAncestor(anyHwnd, NativeConstants.GA_ROOT);
            if (root == IntPtr.Zero) root = anyHwnd;

            // 캐시에서 매칭 시도
            var entry = FindEntryByRoot(root, useCache: true);

            // 실패하면 즉시 재스캔 1회 후 재시도
            if (entry is null)
            {
                _entryCache = _scanner.Scan(autoInclude: false);
                _cacheAtUtc = DateTime.UtcNow;
                entry = FindEntryByRoot(root, useCache: true);
            }

            if (entry is null)
            {
                return new ChatCaptureResult
                {
                    Success = false,
                    Warning = $"[FLASH] 매칭 실패: root=0x{root.ToInt64():X}"
                };
            }

            // 기존 Capture 파이프라인 재사용
            return Capture(entry);
        }

        private ChatEntry? FindEntryByRoot(IntPtr root, bool useCache)
        {
            var list = useCache ? GetEntriesCached() : _scanner.Scan(autoInclude: false);

            // ParentHwnd로 직접 매칭
            var e = list.FirstOrDefault(x => x.ParentHwnd == root);
            if (e != null) return e;

            // 자식 HWND가 직접 들어온 경우 보정
            e = list.FirstOrDefault(x => x.Hwnd == root);
            if (e != null) return e;

            return null;
        }

        private IReadOnlyList<ChatEntry> GetEntriesCached()
        {
            var now = DateTime.UtcNow;
            if (_entryCache.Count == 0 || (now - _cacheAtUtc) > _cacheTtl)
            {
                _entryCache = _scanner.Scan(autoInclude: false);
                _cacheAtUtc = now;
            }
            return _entryCache;
        }
    }
}
