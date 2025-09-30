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

        public static IReadOnlyList<SavedMessage> SaveMessages(string dbPath, long chatId, IEnumerable<ParsedMessage> messages)
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

            var insertedMessages = new List<SavedMessage>();

            var pC = cmd.CreateParameter(); pC.ParameterName = "$c";
            var pS = cmd.CreateParameter(); pS.ParameterName = "$s";
            var pT = cmd.CreateParameter(); pT.ParameterName = "$t";
            var pB = cmd.CreateParameter(); pB.ParameterName = "$b";
            var pH = cmd.CreateParameter(); pH.ParameterName = "$h";
            var pO = cmd.CreateParameter(); pO.ParameterName = "$o";

            cmd.Parameters.AddRange(new[] { pC, pS, pT, pB, pH, pO });

            using var nextOrderCmd = conn.CreateCommand();
            nextOrderCmd.Transaction = tx;
            nextOrderCmd.CommandText = @"
SELECT MAX(msg_order)
FROM messages
WHERE chat_id = $c AND sender = $s AND ts_local = $t AND content = $b;
";

            var qC = nextOrderCmd.CreateParameter(); qC.ParameterName = "$c";
            var qS = nextOrderCmd.CreateParameter(); qS.ParameterName = "$s";
            var qT = nextOrderCmd.CreateParameter(); qT.ParameterName = "$t";
            var qB = nextOrderCmd.CreateParameter(); qB.ParameterName = "$b";

            nextOrderCmd.Parameters.AddRange(new[] { qC, qS, qT, qB });

            var nextOrderCache = new Dictionary<(string Sender, string Ts, string Content), int>();

            foreach (var message in messages)
            {
                string tsIso = message.LocalTs.ToString("yyyy-MM-ddTHH:mm:ss");
                string sender = message.Sender ?? string.Empty;
                string content = message.Content ?? string.Empty;

                var key = (Sender: sender, Ts: tsIso, Content: content);

                int msgOrder;
                if (!nextOrderCache.TryGetValue(key, out var nextAvailableOrder))
                {
                    qC.Value = chatId;
                    qS.Value = sender;
                    qT.Value = tsIso;
                    qB.Value = content;

                    var scalar = nextOrderCmd.ExecuteScalar();
                    int maxOrder;
                    if (scalar == null || scalar == DBNull.Value)
                    {
                        maxOrder = -1;
                    }
                    else if (scalar is long l)
                    {
                        maxOrder = checked((int)l);
                    }
                    else if (scalar is int i)
                    {
                        maxOrder = i;
                    }
                    else
                    {
                        maxOrder = Convert.ToInt32(Convert.ToString(scalar, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
                    }

                    msgOrder = maxOrder + 1;
                }
                else
                {
                    msgOrder = nextAvailableOrder;
                }

                nextOrderCache[key] = msgOrder + 1;

                string hash = ComputeHash(chatId, sender, tsIso, content, msgOrder);

                pC.Value = chatId;
                pS.Value = sender;
                pT.Value = tsIso;
                pB.Value = content;
                pH.Value = hash;
                pO.Value = msgOrder;

                int affected = cmd.ExecuteNonQuery();
                if (affected > 0)
                {
                    insertedMessages.Add(new SavedMessage
                    {
                        Sender = sender,
                        LocalTs = message.LocalTs,
                        Content = content,
                        MsgOrder = msgOrder
                    });
                }
            }

            tx.Commit();

            return insertedMessages;
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

        private static string ComputeHash(long chatId, string sender, string tsLocalIso, string content, int msgOrder)
        {
            string input = chatId.ToString(CultureInfo.InvariantCulture)
                + "\n" + (sender ?? string.Empty)
                + "\n" + tsLocalIso
                + "\n" + (content ?? string.Empty)
                + "\n" + msgOrder.ToString(CultureInfo.InvariantCulture);
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
