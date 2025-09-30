using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using KakaoPcLogger.Models;

namespace KakaoPcLogger.Services
{
    public sealed class WebhookNotificationService : IDisposable
    {
        private readonly Uri? _endpoint;
        private readonly HttpClient _httpClient;
        private readonly JsonSerializerOptions _jsonOptions;
        private readonly Action<string>? _log;
        private bool _missingEndpointLogged;
        private readonly object _logLock = new();

        public WebhookNotificationService(Uri? endpoint, Action<string>? log)
        {
            _endpoint = endpoint;
            _log = log;
            _httpClient = new HttpClient();
            _jsonOptions = new JsonSerializerOptions
            {
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };
        }

        public async Task NotifyNewMessagesAsync(string chatRoom, IReadOnlyList<SavedMessage> messages, CancellationToken cancellationToken = default)
        {
            if (messages == null || messages.Count == 0)
            {
                return;
            }

            if (_endpoint is null)
            {
                LogMissingEndpointOnce();
                return;
            }

            foreach (var message in messages)
            {
                try
                {
                    var payload = new
                    {
                        chatRoom,
                        sender = message.Sender,
                        timestamp = message.LocalTs.ToString("yyyy-MM-dd HH:mm:ss"),
                        order = message.MsgOrder,
                        content = message.Content
                    };

                    using var content = new StringContent(JsonSerializer.Serialize(payload, _jsonOptions), Encoding.UTF8, "application/json");
                    using var response = await _httpClient.PostAsync(_endpoint, content, cancellationToken).ConfigureAwait(false);

                    if (response.IsSuccessStatusCode)
                    {
                        _log?.Invoke($"[Webhook] 전송 성공: {chatRoom} #{message.MsgOrder}");
                    }
                    else
                    {
                        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                        _log?.Invoke($"[Webhook] 실패({(int)response.StatusCode}): {body}");
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _log?.Invoke($"[Webhook] 예외: {ex.Message}");
                }
            }
        }

        private void LogMissingEndpointOnce()
        {
            lock (_logLock)
            {
                if (_missingEndpointLogged)
                {
                    return;
                }

                _missingEndpointLogged = true;
                _log?.Invoke("[Webhook] 엔드포인트가 설정되지 않아 전송이 비활성화되었습니다.");
            }
        }

        public void Dispose()
        {
            _httpClient.Dispose();
        }
    }
}
