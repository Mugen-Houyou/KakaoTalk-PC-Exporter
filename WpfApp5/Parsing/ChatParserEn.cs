using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using KakaoPcLogger.Models;

namespace KakaoPcLogger.Parsing
{
    public static class ChatParserEn
    {
        private static readonly Regex DateLineRx = new Regex(
            @"^(Sunday|Monday|Tuesday|Wednesday|Thursday|Friday|Saturday),\s+[A-Za-z]+\s+\d{1,2},\s+\d{4}$",
            RegexOptions.Compiled);

        private static readonly Regex MsgHeaderRx = new Regex(
            @"^\[(.+?)\]\s+\[(\d{1,2}:\d{2}\s?(AM|PM))\](?:\s*(.*))?$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly CultureInfo EnUs = CultureInfo.GetCultureInfo("en-US");

        public static List<ParsedMessage> ParseRaw(string raw)
        {
            var results = new List<ParsedMessage>();

            if (string.IsNullOrWhiteSpace(raw))
            {
                return results;
            }

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

                if (DateLineRx.IsMatch(trimmed))
                {
                    FlushCurrent();

                    if (DateTime.TryParseExact(trimmed, "dddd, MMMM d, yyyy", EnUs, DateTimeStyles.None, out var date))
                    {
                        currentDate = date.Date;
                        sawAnyDate = true;
                    }
                    else
                    {
                        currentDate = null;
                    }

                    continue;
                }

                var match = MsgHeaderRx.Match(trimmed);
                if (match.Success)
                {
                    if (!currentDate.HasValue)
                    {
                        currentSender = null;
                        currentTs = null;
                        currentContent.Clear();
                        continue;
                    }

                    FlushCurrent();

                    currentSender = match.Groups[1].Value.Trim();
                    var timeStr = match.Groups[2].Value.Trim();
                    var inlineBody = match.Groups[4].Success ? match.Groups[4].Value : null;

                    if (!DateTime.TryParseExact(timeStr, "h:mm tt", EnUs, DateTimeStyles.None, out var parsedTime))
                    {
                        if (!DateTime.TryParse(timeStr, EnUs, DateTimeStyles.None, out parsedTime))
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
                    {
                        currentContent.Append(inlineBody);
                    }
                }
                else
                {
                    if (currentSender != null && currentTs.HasValue)
                    {
                        if (currentContent.Length > 0)
                        {
                            currentContent.AppendLine();
                        }

                        currentContent.Append(trimmed);
                    }
                }
            }

            FlushCurrent();

            if (!sawAnyDate)
            {
                results.Clear();
            }

            return results;
        }

        private static IEnumerable<string> SplitLinesPreserve(string s)
        {
            using var reader = new StringReader(s);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                yield return line;
            }
        }
    }
}
