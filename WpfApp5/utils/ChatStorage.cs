using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using System.Security.Cryptography;


namespace KakaoPcLogger
{
    public static class ChatStorage
    {
        // === 파싱 규칙 ===
        // 날짜 라인 예: "Saturday, May 10, 2025"
        private static readonly Regex DateLineRx = new Regex(
            @"^(Sunday|Monday|Tuesday|Wednesday|Thursday|Friday|Saturday),\s+[A-Za-z]+\s+\d{1,2},\s+\d{4}$",
            RegexOptions.Compiled);

        // 메시지 헤더 예: "[상대방(테스트)] [1:40 AM]"  또는  "[나!!!] [3:47 AM] Photo"
        // 그룹1: 송신자, 그룹2: 시각, 그룹4: 헤더와 같은 줄에 이어지는 본문(있을 수도, 없을 수도)
        private static readonly Regex MsgHeaderRx = new Regex(
            @"^\[(.+?)\]\s+\[(\d{1,2}:\d{2}\s?(AM|PM))\](?:\s*(.*))?$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // 날짜 파싱은 raw가 영어 요일/월이므로 en-US 기준
        private static readonly CultureInfo EnUS = CultureInfo.GetCultureInfo("en-US");

        // === DB 초기화 ===
        public static void EnsureDatabase(string dbPath)
        {
            var dir = Path.GetDirectoryName(dbPath);
            if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            using var conn = new SqliteConnection($"Data Source={dbPath}");
            conn.Open();

            // 기본 테이블 생성
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
PRAGMA journal_mode=WAL;

CREATE TABLE IF NOT EXISTS chats(
    id      INTEGER PRIMARY KEY AUTOINCREMENT,
    title   TEXT NOT NULL UNIQUE
);

CREATE TABLE IF NOT EXISTS messages(
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    chat_id     INTEGER NOT NULL,
    sender      TEXT NOT NULL,
    ts_local    TEXT NOT NULL,   -- ISO-8601 local
    content     TEXT NOT NULL
    -- hash는 아래 마이그레이션에서 추가
);
";
                cmd.ExecuteNonQuery();
            }

            // 마이그레이션: messages.hash 컬럼 추가 (없으면)
            bool hasHash = false;
            using (var pragma = conn.CreateCommand())
            {
                pragma.CommandText = "PRAGMA table_info(messages);";
                using var r = pragma.ExecuteReader();
                while (r.Read())
                {
                    if (string.Equals(r.GetString(1), "hash", StringComparison.OrdinalIgnoreCase))
                    {
                        hasHash = true; break;
                    }
                }
            }
            if (!hasHash)
            {
                using var alter = conn.CreateCommand();
                alter.CommandText = "ALTER TABLE messages ADD COLUMN hash TEXT;";
                alter.ExecuteNonQuery();
            }

            // 유니크 인덱스: 같은 방(chat_id) 안에서 같은 hash는 1번만
            using (var idx = conn.CreateCommand())
            {
                idx.CommandText = @"
CREATE UNIQUE INDEX IF NOT EXISTS ux_messages_chat_hash ON messages(chat_id, hash);
CREATE INDEX IF NOT EXISTS idx_messages_chat_ts ON messages(chat_id, ts_local);
";
                idx.ExecuteNonQuery();
            }
        }

        // === 채팅방 id 확보(없으면 생성) ===
        public static long GetOrCreateChatId(string dbPath, string chatTitle)
        {
            using var conn = new SqliteConnection($"Data Source={dbPath}");
            conn.Open();

            using (var find = conn.CreateCommand())
            {
                find.CommandText = "SELECT id FROM chats WHERE title = $t";
                find.Parameters.AddWithValue("$t", chatTitle);
                var r = find.ExecuteScalar();
                if (r != null && r != DBNull.Value)
                    return (long)(r is long L ? L : Convert.ToInt64(r));
            }

            using (var ins = conn.CreateCommand())
            {
                ins.CommandText = "INSERT INTO chats(title) VALUES($t); SELECT last_insert_rowid();";
                ins.Parameters.AddWithValue("$t", chatTitle);
                return (long)(ins.ExecuteScalar() ?? 0L);
            }
        }

        // === 메시지 모델 ===
        public sealed class ParsedMsg
        {
            public string Sender { get; init; } = "";
            public DateTime LocalTs { get; init; } // Local time
            public string Content { get; init; } = "";
        }

        /// <summary>
        /// raw 텍스트(클립보드) → 메시지 리스트. (날짜 라인이 최소 한 번 이상 등장한 구간만 저장)
        /// 날짜 구분이 한 번도 등장하지 않으면 빈 리스트 반환.
        /// </summary>
        public static List<ParsedMsg> ParseRaw(string raw)
        {
            var results = new List<ParsedMsg>();

            if (string.IsNullOrWhiteSpace(raw))
                return results;

            var lines = SplitLinesPreserve(raw);

            DateTime? currentDate = null;       // 날짜 헤더에서 얻는 '일자' (시각은 메시지 헤더에서)
            bool sawAnyDate = false;

            // 현재 메시지 누적 버퍼
            string? currSender = null;
            DateTime? currTs = null;
            var currContent = new StringBuilder();

            // 내부 헬퍼: message finalize
            void FlushCurrent()
            {
                if (currSender != null && currTs.HasValue)
                {
                    // 내용 끝의 개행 정리
                    var content = currContent.ToString().TrimEnd('\r', '\n');
                    results.Add(new ParsedMsg
                    {
                        Sender = currSender,
                        LocalTs = currTs.Value,
                        Content = content
                    });
                }
                currSender = null;
                currTs = null;
                currContent.Clear();
            }

            foreach (var line in lines)
            {
                var trimmed = line.TrimEnd('\r'); // 원문 개행 보존 위해 끝 CR만 제거

                // 1) 날짜 라인?
                if (DateLineRx.IsMatch(trimmed))
                {
                    // 이전 메시지가 진행 중이면 먼저 플러시
                    FlushCurrent();

                    // 날짜 파싱 (요일은 무시하고 Month Day, Year만)
                    if (DateTime.TryParseExact(
                        trimmed,
                        "dddd, MMMM d, yyyy",
                        EnUS,
                        DateTimeStyles.None,
                        out var date))
                    {
                        currentDate = date.Date; // 00:00
                        sawAnyDate = true;
                    }
                    else
                    {
                        // 형식이지만 파싱 실패하면 날짜 무효화
                        currentDate = null;
                    }
                    continue;
                }

                // 2) 메시지 헤더?
                var m = MsgHeaderRx.Match(trimmed);
                if (m.Success)
                {
                    // 날짜가 없다면(=이전 클립보드에서 누락), 이 메시지와 이어지는 블록은 DB에 저장하지 않음
                    if (!currentDate.HasValue)
                    {
                        // 진행 중이던 메시지도 버림
                        currSender = null;
                        currTs = null;
                        currContent.Clear();
                        continue;
                    }

                    // 기존 메시지 플러시
                    FlushCurrent();

                    currSender = m.Groups[1].Value.Trim();
                    var timeStr = m.Groups[2].Value.Trim();
                    var inlineBody = m.Groups[4].Success ? m.Groups[4].Value : null;

                    // time (AM/PM)
                    if (!DateTime.TryParseExact(timeStr, "h:mm tt", EnUS, DateTimeStyles.None, out var t))
                    {
                        // 가끔 공백이 없거나 소문자 pm 등의 변형 방어
                        if (!DateTime.TryParse(timeStr, EnUS, DateTimeStyles.None, out t))
                        {
                            // 시간 파싱 실패 시, 이 메시지 블록은 스킵
                            currSender = null;
                            currTs = null;
                            currContent.Clear();
                            continue;
                        }
                    }

                    // 최종 Local 시각 = currentDate + time
                    var ts = currentDate.Value.Date
                             .AddHours(t.Hour)
                             .AddMinutes(t.Minute);

                    currTs = ts;

                    // 같은 줄에 본문이 이어졌다면 추가
                    if (!string.IsNullOrEmpty(inlineBody))
                    {
                        currContent.Append(inlineBody);
                    }
                }
                else
                {
                    // 3) 본문 라인 (현재 메시지가 진행 중일 때만 누적)
                    if (currSender != null && currTs.HasValue)
                    {
                        if (currContent.Length > 0)
                            currContent.AppendLine();
                        currContent.Append(trimmed);
                    }
                    else
                    {
                        // 헤더도 날짜도 아닌 떨어진 라인: 저장 대상 아님 (무시)
                    }
                }
            }

            // 마지막 메시지 플러시
            FlushCurrent();

            // 규칙: 날짜 구분이 한 번도 없었던 블록은 저장하지 않음
            if (!sawAnyDate)
                results.Clear();

            return results;
        }

        // === 메시지 저장 ===
        public static void SaveMessages(string dbPath, long chatId, IEnumerable<ParsedMsg> msgs)
        {
            using var conn = new SqliteConnection($"Data Source={dbPath}");
            conn.Open();

            using var tx = conn.BeginTransaction();
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;

            // 중복 무시를 위해 INSERT OR IGNORE 사용
            cmd.CommandText = @"
INSERT OR IGNORE INTO messages(chat_id, sender, ts_local, content, hash)
VALUES ($c, $s, $t, $b, $h);
";
            var pC = cmd.CreateParameter(); pC.ParameterName = "$c";
            var pS = cmd.CreateParameter(); pS.ParameterName = "$s";
            var pT = cmd.CreateParameter(); pT.ParameterName = "$t";
            var pB = cmd.CreateParameter(); pB.ParameterName = "$b";
            var pH = cmd.CreateParameter(); pH.ParameterName = "$h";
            cmd.Parameters.AddRange(new[] { pC, pS, pT, pB, pH });

            foreach (var m in msgs)
            {
                string tsIso = m.LocalTs.ToString("yyyy-MM-ddTHH:mm:ss");
                string hash = ComputeHash(tsIso, m.Content);

                pC.Value = chatId;
                pS.Value = m.Sender;
                pT.Value = tsIso;
                pB.Value = m.Content;
                pH.Value = hash;

                cmd.ExecuteNonQuery(); // 중복이면 자동으로 무시됨
            }

            tx.Commit();
        }


        // === 줄 분리(원문 개행 보전) ===
        private static IEnumerable<string> SplitLinesPreserve(string s)
        {
            using var sr = new StringReader(s);
            string? line;
            while ((line = sr.ReadLine()) != null)
                yield return line;
        }

        // ts_local + '\n' + content 형태로 해시 (요청사항 준수)
        private static string ComputeHash(string tsLocalIso, string content)
        {
            string input = tsLocalIso + "\n" + content;
            using var sha = SHA256.Create();
            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(input));
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

    }
}
