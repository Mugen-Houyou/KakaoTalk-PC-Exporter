using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using KakaoPcLogger.Models;
using Microsoft.Data.Sqlite;

namespace KakaoPcLogger.Data
{
    public static class ChatDatabase
    {
        public static void EnsureDatabase(string dbPath)
        {
            var dir = Path.GetDirectoryName(dbPath);
            if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            using var conn = new SqliteConnection($"Data Source={dbPath}");
            conn.Open();

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
    ts_local    TEXT NOT NULL,
    content     TEXT NOT NULL,
    hash        TEXT,
    msg_order     INTEGER NOT NULL DEFAULT 0
);
";
                cmd.ExecuteNonQuery();
            }

            EnsureIndexes(conn);
        }

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
                {
                    return (long)(r is long id ? id : Convert.ToInt64(r));
                }
            }

            using (var insert = conn.CreateCommand())
            {
                insert.CommandText = "INSERT INTO chats(title) VALUES($t); SELECT last_insert_rowid();";
                insert.Parameters.AddWithValue("$t", chatTitle);
                return (long)(insert.ExecuteScalar() ?? 0L);
            }
        }

        public static void SaveMessages(string dbPath, long chatId, IEnumerable<ParsedMessage> messages)
        {
            using var conn = new SqliteConnection($"Data Source={dbPath}");
            conn.Open();

            using var tx = conn.BeginTransaction();
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
INSERT OR IGNORE INTO messages(chat_id, sender, ts_local, content, hash, msg_order)
VALUES ($c, $s, $t, $b, $h, $o);
";

            var pC = cmd.CreateParameter(); pC.ParameterName = "$c";
            var pS = cmd.CreateParameter(); pS.ParameterName = "$s";
            var pT = cmd.CreateParameter(); pT.ParameterName = "$t";
            var pB = cmd.CreateParameter(); pB.ParameterName = "$b";
            var pH = cmd.CreateParameter(); pH.ParameterName = "$h";
            var pO = cmd.CreateParameter(); pO.ParameterName = "$o";

            cmd.Parameters.AddRange(new[] { pC, pS, pT, pB, pH, pO });

            foreach (var message in messages)
            {
                string tsIso = message.LocalTs.ToString("yyyy-MM-ddTHH:mm:ss");
                string hash = ComputeHash(chatId, message.Sender, tsIso, message.Content);

                pC.Value = chatId;
                pS.Value = message.Sender;
                pT.Value = tsIso;
                pB.Value = message.Content;
                pH.Value = hash;
                pO.Value = message.MsgOrder;

                cmd.ExecuteNonQuery();
            }

            tx.Commit();
        }

        private static void EnsureIndexes(SqliteConnection connection)
        {
            bool hasHashColumn = false;
            bool hasMsgOrderColumn = false;
            using (var pragma = connection.CreateCommand())
            {
                pragma.CommandText = "PRAGMA table_info(messages);";
                using var reader = pragma.ExecuteReader();
                while (reader.Read())
                {
                    var columnName = reader.GetString(1);
                    if (string.Equals(columnName, "hash", StringComparison.OrdinalIgnoreCase))
                    {
                        hasHashColumn = true;
                    }

                    if (string.Equals(columnName, "msg_order", StringComparison.OrdinalIgnoreCase))
                    {
                        hasMsgOrderColumn = true;
                    }
                }
            }

            if (!hasHashColumn)
            {
                using var alter = connection.CreateCommand();
                alter.CommandText = "ALTER TABLE messages ADD COLUMN hash TEXT;";
                alter.ExecuteNonQuery();
            }

            if (!hasMsgOrderColumn)
            {
                using var alter = connection.CreateCommand();
                alter.CommandText = "ALTER TABLE messages ADD COLUMN msg_order INTEGER NOT NULL DEFAULT 0;";
                alter.ExecuteNonQuery();
            }

            using (var index = connection.CreateCommand())
            {
                index.CommandText = @"
CREATE UNIQUE INDEX IF NOT EXISTS ux_messages_chat_hash ON messages(chat_id, hash);
CREATE INDEX IF NOT EXISTS idx_messages_chat_ts ON messages(chat_id, ts_local);
";
                index.ExecuteNonQuery();
            }
        }

        private static string ComputeHash(long chatId, string sender, string tsLocalIso, string content)
        {
            string input = chatId.ToString(CultureInfo.InvariantCulture) + "\n" + (sender ?? string.Empty) + "\n" + tsLocalIso + "\n" + (content ?? string.Empty);
            using var sha = SHA256.Create();
            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(input));
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes)
            {
                sb.Append(b.ToString("x2"));
            }
            return sb.ToString();
        }
    }
}
