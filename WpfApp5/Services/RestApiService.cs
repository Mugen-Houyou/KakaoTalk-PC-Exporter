using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace KakaoPcLogger.Services
{
    public sealed class RestApiService : IDisposable
    {
        private readonly string _dbPath;
        private const string DefaultHealthCheckPath = "/api/webhook/health";

        private readonly HttpListener _listener;
        private readonly JsonSerializerOptions _jsonOptions;
        private readonly HashSet<string> _healthCheckPaths;
        private CancellationTokenSource? _cts;
        private Task? _backgroundTask;

        public RestApiService(IEnumerable<string> prefixes, string dbPath, IEnumerable<string>? healthCheckPaths = null)
        {
            if (!HttpListener.IsSupported)
            {
                throw new NotSupportedException("HTTP Listener is not supported on this platform.");
            }

            _dbPath = dbPath ?? throw new ArgumentNullException(nameof(dbPath));
            _listener = new HttpListener();

            if (prefixes is null)
            {
                throw new ArgumentNullException(nameof(prefixes));
            }

            var prefixList = prefixes
                .Where(static prefix => !string.IsNullOrWhiteSpace(prefix))
                .Select(static prefix => prefix.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (prefixList.Count == 0)
            {
                throw new ArgumentException("At least one HTTP prefix must be provided.", nameof(prefixes));
            }

            foreach (var prefix in prefixList)
            {
                _listener.Prefixes.Add(prefix);
            }

            _jsonOptions = new JsonSerializerOptions
            {
                WriteIndented = true
            };

            _healthCheckPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var defaultHealthPath = NormalizeAbsolutePath(DefaultHealthCheckPath);
            if (!string.IsNullOrEmpty(defaultHealthPath))
            {
                _healthCheckPaths.Add(defaultHealthPath);
            }

            if (healthCheckPaths is not null)
            {
                foreach (var path in healthCheckPaths)
                {
                    var normalized = NormalizeAbsolutePath(path);
                    if (!string.IsNullOrEmpty(normalized))
                    {
                        _healthCheckPaths.Add(normalized);
                    }
                }
            }
        }

        public event Action<string>? Log;

        public void Start()
        {
            if (_cts != null)
            {
                return;
            }

            _cts = new CancellationTokenSource();

            try
            {
                _listener.Start();
            }
            catch
            {
                _cts.Dispose();
                _cts = null;
                throw;
            }

            _backgroundTask = Task.Run(() => ListenLoopAsync(_cts.Token));
            Log?.Invoke("[REST] 서비스가 시작되었습니다.");
        }

        private async Task ListenLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                HttpListenerContext? context = null;
                try
                {
                    context = await _listener.GetContextAsync().ConfigureAwait(false);
                }
                catch (HttpListenerException ex) when (ex.ErrorCode == 995)
                {
                    // Listener가 중지될 때 발생하는 예외. 루프 종료.
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (InvalidOperationException)
                {
                    break;
                }

                if (context != null)
                {
                    _ = Task.Run(() => ProcessRequestAsync(context), token);
                }
            }
        }

        private async Task ProcessRequestAsync(HttpListenerContext context)
        {
            try
            {
                if (!string.Equals(context.Request.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteErrorAsync(context.Response, HttpStatusCode.MethodNotAllowed, "Only GET is supported.").ConfigureAwait(false);
                    return;
                }

                var normalizedPath = NormalizeAbsolutePath(context.Request.Url?.AbsolutePath);

                if (_healthCheckPaths.Contains(normalizedPath))
                {
                    await HandleWebhookHealthAsync(context.Response).ConfigureAwait(false);
                    return;
                }

                var segments = normalizedPath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);

                if (segments.Length == 2 && string.Equals(segments[0], "messages", StringComparison.OrdinalIgnoreCase))
                {
                    string chatTitle = WebUtility.UrlDecode(segments[1]);
                    await HandleGetMessagesAsync(context.Response, chatTitle).ConfigureAwait(false);
                }
                else
                {
                    await WriteErrorAsync(context.Response, HttpStatusCode.NotFound, "Endpoint not found.").ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Log?.Invoke($"[REST] 요청 처리 중 오류: {ex.Message}");
                if (context.Response.OutputStream.CanWrite)
                {
                    await WriteErrorAsync(context.Response, HttpStatusCode.InternalServerError, "Internal server error.").ConfigureAwait(false);
                }
            }
            finally
            {
                try
                {
                    context.Response.OutputStream.Close();
                }
                catch
                {
                    // ignore
                }
            }
        }

        private async Task HandleGetMessagesAsync(HttpListenerResponse response, string chatTitle)
        {
            if (string.IsNullOrWhiteSpace(chatTitle))
            {
                await WriteErrorAsync(response, HttpStatusCode.BadRequest, "Chat room title is required.").ConfigureAwait(false);
                return;
            }

            try
            {
                using var connection = new SqliteConnection($"Data Source={_dbPath}");
                await connection.OpenAsync().ConfigureAwait(false);

                long? chatId = await GetChatIdAsync(connection, chatTitle).ConfigureAwait(false);
                if (chatId is null)
                {
                    await WriteErrorAsync(response, HttpStatusCode.NotFound, "Chat room not found.").ConfigureAwait(false);
                    return;
                }

                var messages = await GetMessagesAsync(connection, chatId.Value, chatTitle).ConfigureAwait(false);

                var payload = JsonSerializer.Serialize(messages, _jsonOptions);
                var buffer = Encoding.UTF8.GetBytes(payload);

                response.StatusCode = (int)HttpStatusCode.OK;
                response.ContentType = "application/json; charset=utf-8";
                response.ContentEncoding = Encoding.UTF8;
                response.ContentLength64 = buffer.Length;

                await response.OutputStream.WriteAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log?.Invoke($"[REST] 데이터 조회 중 오류: {ex.Message}");
                await WriteErrorAsync(response, HttpStatusCode.InternalServerError, "Failed to read messages.").ConfigureAwait(false);
            }
        }

        private static async Task HandleWebhookHealthAsync(HttpListenerResponse response)
        {
            const string payload = "{\"status\":\"running\"}";
            var buffer = Encoding.UTF8.GetBytes(payload);

            response.StatusCode = (int)HttpStatusCode.OK;
            response.ContentType = "application/json; charset=utf-8";
            //response.ContentType = "text/plain; charset=utf-8";
            response.ContentEncoding = Encoding.UTF8;
            response.ContentLength64 = buffer.Length;

            await response.OutputStream.WriteAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
        }

        private static async Task<long?> GetChatIdAsync(SqliteConnection connection, string chatTitle)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT id FROM chats WHERE title = $title LIMIT 1";
            cmd.Parameters.AddWithValue("$title", chatTitle);

            var result = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
            if (result == null || result == DBNull.Value)
            {
                return null;
            }

            return result switch
            {
                long id => id,
                int id => id,
                _ => Convert.ToInt64(result, CultureInfo.InvariantCulture)
            };
        }

        private static async Task<List<MessageDto>> GetMessagesAsync(SqliteConnection connection, long chatId, string chatTitle)
        {
            var messages = new List<MessageDto>();

            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
SELECT sender, ts_local, content, msg_order
FROM messages
WHERE chat_id = $chatId
ORDER BY msg_order ASC, id ASC;";
            cmd.Parameters.AddWithValue("$chatId", chatId);

            using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                string sender = reader.GetString(0);
                string tsLocal = reader.GetString(1);
                string content = reader.GetString(2);
                long orderValue = reader.GetInt64(3);

                string timestamp = TryFormatTimestamp(tsLocal);

                messages.Add(new MessageDto
                {
                    ChatRoom = chatTitle,
                    Sender = sender,
                    Timestamp = timestamp,
                    Order = orderValue.ToString(CultureInfo.InvariantCulture),
                    Content = content
                });
            }

            return messages;
        }

        private static string TryFormatTimestamp(string tsLocal)
        {
            if (DateTime.TryParseExact(tsLocal,
                                       "yyyy-MM-dd'T'HH:mm:ss",
                                       CultureInfo.InvariantCulture,
                                       DateTimeStyles.AssumeLocal,
                                       out var parsed))
            {
                return parsed.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            }

            if (DateTime.TryParse(tsLocal, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out parsed))
            {
                return parsed.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            }

            return tsLocal;
        }

        private async Task WriteErrorAsync(HttpListenerResponse response, HttpStatusCode statusCode, string message)
        {
            if (!response.OutputStream.CanWrite)
            {
                return;
            }

            response.StatusCode = (int)statusCode;
            response.ContentType = "application/json; charset=utf-8";
            response.ContentEncoding = Encoding.UTF8;

            var error = new ErrorResponse { Message = message };
            var payload = JsonSerializer.Serialize(error, _jsonOptions);
            var buffer = Encoding.UTF8.GetBytes(payload);
            response.ContentLength64 = buffer.Length;

            await response.OutputStream.WriteAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
        }


        private static string NormalizeAbsolutePath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return "/";
            }

            var trimmed = path.Trim();

            if (!trimmed.StartsWith("/", StringComparison.Ordinal))
            {
                trimmed = "/" + trimmed;
            }

            if (trimmed.Length > 1 && trimmed.EndsWith("/", StringComparison.Ordinal))
            {
                trimmed = trimmed.TrimEnd('/');
            }

            return trimmed;
        }

        public void Dispose()
        {
            Stop();
            _listener.Close();
        }

        public void Stop()
        {
            if (_cts == null)
            {
                return;
            }

            try
            {
                _cts.Cancel();
                _listener.Stop();
                _backgroundTask?.Wait(TimeSpan.FromSeconds(2));
            }
            catch (Exception ex)
            {
                Log?.Invoke($"[REST] 서비스 중지 중 오류: {ex.Message}");
            }
            finally
            {
                _backgroundTask = null;
                _cts.Dispose();
                _cts = null;
            }
        }

        private sealed class MessageDto
        {
            [JsonPropertyName("chat_room")]
            public string ChatRoom { get; set; } = string.Empty;

            [JsonPropertyName("sender")]
            public string Sender { get; set; } = string.Empty;

            [JsonPropertyName("timestamp")]
            public string Timestamp { get; set; } = string.Empty;

            [JsonPropertyName("order")]
            public string Order { get; set; } = string.Empty;

            [JsonPropertyName("content")]
            public string Content { get; set; } = string.Empty;
        }

        private sealed class ErrorResponse
        {
            [JsonPropertyName("message")]
            public string Message { get; set; } = string.Empty;
        }
    }
}
