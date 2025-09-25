using System.Collections.Generic;
using System.Text;
using KakaoPcLogger.Models;

namespace KakaoPcLogger.Services
{
    public sealed class ChatLogManager
    {
        private const int MaxChars = 1_000_000;
        private readonly Dictionary<string, StringBuilder> _logs = new();

        public string GetKey(ChatEntry entry)
            => $"{entry.Title}|0x{entry.Hwnd.ToInt64():X}";

        public void Append(ChatEntry entry, string line)
        {
            if (string.IsNullOrEmpty(line))
                return;

            string key = GetKey(entry);
            if (!_logs.TryGetValue(key, out var sb))
            {
                sb = new StringBuilder();
                _logs[key] = sb;
            }

            if (sb.Length > MaxChars)
                sb.Clear();

            if (sb.Length > 0 && !line.EndsWith("\n"))
                sb.AppendLine(line);
            else
                sb.Append(line);
        }

        public void Set(ChatEntry entry, string text)
        {
            text ??= string.Empty;
            if (text.Length > MaxChars)
                text = text[^MaxChars..];

            var sb = new StringBuilder(text.Length + 64);
            sb.Append(text);
            _logs[GetKey(entry)] = sb;
        }

        public bool TryGet(ChatEntry entry, out string text)
            => TryGet(GetKey(entry), out text);

        public bool TryGet(string key, out string text)
        {
            if (_logs.TryGetValue(key, out var sb))
            {
                text = sb.ToString();
                return true;
            }

            text = string.Empty;
            return false;
        }

        public void ReplaceKey(string oldKey, ChatEntry updatedEntry)
        {
            if (string.IsNullOrEmpty(oldKey))
                return;

            if (!_logs.TryGetValue(oldKey, out var sb))
                return;

            _logs.Remove(oldKey);
            _logs[GetKey(updatedEntry)] = sb;
        }
    }
}
