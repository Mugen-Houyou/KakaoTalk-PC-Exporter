using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using KakaoPcLogger.Models;

namespace KakaoPcLogger.Parsing
{
    public static class ChatParserKo
    {
        // 예: 2025년 9월 9일 화요일  (요일은 옵션으로 처리)
        private static readonly Regex DateLineRx = new Regex(
            @"^\s*(\d{4})년\s+(\d{1,2})월\s+(\d{1,2})일(?:\s+(일|월|화|수|목|금|토)요일)?\s*$",
            RegexOptions.Compiled);

        // 예: [큰누나] [오후 7:37] 본문…
        private static readonly Regex MsgHeaderRx = new Regex(
            @"^\[(.+?)\]\s+\[(오전|오후)\s*(\d{1,2}:\d{2})\](?:\s*(.*))?$",
            RegexOptions.Compiled);

        private static readonly CultureInfo KoKr = CultureInfo.GetCultureInfo("ko-KR");

        public static List<ParsedMessage> ParseRaw(string raw)
        {
            var results = new List<ParsedMessage>();
            if (string.IsNullOrWhiteSpace(raw))
                return results;

            var lines = SplitLinesPreserve(raw);

            DateTime? currentDate = null;
            bool sawAnyDate = false;

            string? currentSender = null;
            DateTime? currentTs = null;
            var currentContent = new StringBuilder();

            void FlushCurrent()
            {
                if (currentSender != null && currentTs.HasValue)
                {
                    string content = currentContent.ToString().TrimEnd('\r', '\n');
                    results.Add(new ParsedMessage
                    {
                        Sender = currentSender,
                        LocalTs = currentTs.Value,
                        Content = content,
                        MsgOrder = results.Count
                    });
                }
                currentSender = null;
                currentTs = null;
                currentContent.Clear();
            }

            foreach (var line in lines)
            {
                var trimmed = line.TrimEnd('\r');

                // 1) 날짜 줄
                var dmatch = DateLineRx.Match(trimmed);
                if (dmatch.Success)
                {
                    FlushCurrent();
                    if (int.TryParse(dmatch.Groups[1].Value, out int y) &&
                        int.TryParse(dmatch.Groups[2].Value, out int m) &&
                        int.TryParse(dmatch.Groups[3].Value, out int d))
                    {
                        try
                        {
                            currentDate = new DateTime(y, m, d);
                            sawAnyDate = true;
                        }
                        catch
                        {
                            currentDate = null;
                        }
                    }
                    else
                    {
                        currentDate = null;
                    }
                    continue;
                }

                // 2) 메시지 헤더
                var hmatch = MsgHeaderRx.Match(trimmed);
                if (hmatch.Success)
                {
                    if (!currentDate.HasValue)
                    {
                        // 날짜 경계가 없으면 저장하지 않음(원본 규칙 유지)
                        currentSender = null;
                        currentTs = null;
                        currentContent.Clear();
                        continue;
                    }

                    FlushCurrent();

                    currentSender = hmatch.Groups[1].Value.Trim();
                    var ampm = hmatch.Groups[2].Value;          // 오전/오후
                    var hhmm = hmatch.Groups[3].Value;          // 7:37
                    var inlineBody = hmatch.Groups[4].Success ? hmatch.Groups[4].Value : null;

                    // "오전/오후 h:mm" 파싱
                    DateTime parsedTime;
                    var timeStr = $"{ampm} {hhmm}";
                    if (!DateTime.TryParseExact(timeStr, "tt h:mm", KoKr, DateTimeStyles.None, out parsedTime))
                    {
                        // 예외적으로 24시간 표기 등 대비
                        if (!DateTime.TryParse(hhmm, KoKr, DateTimeStyles.None, out parsedTime))
                        {
                            currentSender = null;
                            currentTs = null;
                            currentContent.Clear();
                            continue;
                        }
                    }

                    currentTs = currentDate.Value.Date
                        .AddHours(parsedTime.Hour)
                        .AddMinutes(parsedTime.Minute);

                    if (!string.IsNullOrEmpty(inlineBody))
                        currentContent.Append(inlineBody);
                }
                else
                {
                    // 3) 본문 라인 누적
                    if (currentSender != null && currentTs.HasValue)
                    {
                        if (currentContent.Length > 0)
                            currentContent.AppendLine();

                        currentContent.Append(trimmed);
                    }
                }
            }

            FlushCurrent();

            // 날짜 구분이 하나도 없으면 저장하지 않음(원본 규칙 유지)
            if (!sawAnyDate)
                results.Clear();

            return results;
        }

        private static IEnumerable<string> SplitLinesPreserve(string s)
        {
            using var reader = new StringReader(s);
            string? line;
            while ((line = reader.ReadLine()) != null)
                yield return line;
        }
    }
}
