using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using KakaoPcLogger.Models;

namespace KakaoPcLogger.Services
{
    public sealed class WebhookNotificationService : IDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly Uri _endpoint;
        private readonly string _host;
        private readonly JsonSerializerOptions _jsonOptions;

        public WebhookNotificationService(string endpoint, string host)
        {
            if (string.IsNullOrWhiteSpace(endpoint))
            {
                throw new ArgumentException("Webhook endpoint must be provided.", nameof(endpoint));
            }

            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
            {
                throw new ArgumentException("Webhook endpoint must be an absolute URI.", nameof(endpoint));
            }
            if (string.IsNullOrWhiteSpace(host))
            {
                throw new ArgumentException("Exporter host name must be provided.", nameof(host));
            }

            _endpoint = uri;
            _host = host.Trim();
            _httpClient = new HttpClient();
            _jsonOptions = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            };
        }

        public event Action<string>? Log;

        public void NotifyMessages(string chatRoom, IReadOnlyList<SavedMessageInfo> messages)
        {
            if (messages == null || messages.Count == 0)
            {
                return;
            }

            _ = Task.Run(() => SendAsync(chatRoom, messages));
        }

        private async Task SendAsync(string chatRoom, IReadOnlyList<SavedMessageInfo> messages)
        {
            foreach (var message in messages)
            {
                var payload = new
                {
                    host = _host,
                    chatRoom,
                    sender = message.Sender,
                    timestamp = message.LocalTs.ToString("yyyy-MM-dd HH:mm:ss"),
                    order = message.MsgOrder,
                    content = message.Content
                };

                var json = JsonSerializer.Serialize(payload, _jsonOptions);
                using var content = new StringContent(json, Encoding.UTF8, "application/json");

                try
                {
                    using var response = await _httpClient.PostAsync(_endpoint, content).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        Log?.Invoke($"[Webhook] 전송 실패 ({response.StatusCode}): {_endpoint.ToString()}, {chatRoom} / {message.LocalTs:yyyy-MM-dd HH:mm:ss}");
                    }
                    else
                    {
                        Log?.Invoke($"[Webhook] 전송 완료: {chatRoom} / {message.LocalTs:yyyy-MM-dd HH:mm:ss}");
                    }
                }
                catch (Exception ex)
                {
                    Log?.Invoke($"[Webhook 오류] {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        public void Dispose()
        {
            _httpClient.Dispose();
        }
    }
}
