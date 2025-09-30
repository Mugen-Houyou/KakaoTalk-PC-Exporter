using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WpfApp5.Configuration
{
    public sealed class AppConfiguration
    {
        private const string DefaultWebhookUrl = "http://localhost:8080/";

        public DatabaseConfiguration Database { get; init; } = new();
        public RestApiConfiguration RestApi { get; init; } = new();
        public WebhookConfiguration Webhook { get; init; } = new();

        public static AppConfiguration Load(string baseDirectory)
        {
            if (baseDirectory is null)
            {
                throw new ArgumentNullException(nameof(baseDirectory));
            }

            var configPath = Path.Combine(baseDirectory, "appsettings.json");

            AppConfiguration config;

            if (!File.Exists(configPath))
            {
                config = new AppConfiguration();
            }
            else
            {
                using var stream = File.OpenRead(configPath);
                config = JsonSerializer.Deserialize<AppConfiguration>(stream, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    ReadCommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true
                }) ?? new AppConfiguration();
            }

            config.Database.Path = ResolveDatabasePath(baseDirectory, config.Database.Path);
            config.RestApi.Normalize();
            config.Webhook.MessageUpdateUrl = NormalizeWebhookUrl(config.Webhook.MessageUpdateUrl);

            return config;
        }

        private static string ResolveDatabasePath(string baseDirectory, string? configuredPath)
        {
            var path = string.IsNullOrWhiteSpace(configuredPath)
                ? Path.Combine("data", "kakao_chat_v2.db")
                : configuredPath;

            if (!Path.IsPathRooted(path))
            {
                path = Path.GetFullPath(Path.Combine(baseDirectory, path));
            }

            return path;
        }

        private static string NormalizeWebhookUrl(string? configuredUrl)
        {
            if (string.IsNullOrWhiteSpace(configuredUrl))
            {
                return DefaultWebhookUrl;
            }

            if (Uri.TryCreate(configuredUrl, UriKind.Absolute, out var uri))
            {
                return uri.ToString();
            }

            return DefaultWebhookUrl;
        }
    }

    public sealed class DatabaseConfiguration
    {
        public string? Path { get; set; }
    }

    public sealed class RestApiConfiguration
    {
        private const string DefaultHost = "localhost";
        private const int DefaultPort = 5010;
        private const string AnyHostWildcard = "+";

        public string? Host { get; set; } = DefaultHost;
        public int? Port { get; set; } = DefaultPort;
        public bool UseHttps { get; set; }
        public bool AllowAnyHost { get; set; }

        [JsonIgnore]
        public string? Prefix => BuildPrefix();

        public void Normalize()
        {
            if (AllowAnyHost)
            {
                if (Port is null || Port <= 0 || Port > 65535)
                {
                    Port = DefaultPort;
                }

                return;
            }

            if (Host is null)
            {
                Host = DefaultHost;
            }
            else
            {
                Host = Host.Trim();
            }

            if (!string.IsNullOrEmpty(Host))
            {
                if (Port is null || Port <= 0 || Port > 65535)
                {
                    Port = DefaultPort;
                }
            }
        }

        public string? BuildPrefix()
        {
            var effectivePort = Port ?? DefaultPort;
            if (effectivePort <= 0 || effectivePort > 65535)
            {
                return null;
            }

            var scheme = UseHttps ? "https" : "http";

            if (AllowAnyHost)
            {
                return $"{scheme}://{AnyHostWildcard}:{effectivePort}/";
            }

            if (string.IsNullOrWhiteSpace(Host))
            {
                return null;
            }

            var effectiveHost = Host.Trim();
            if (effectiveHost.Length == 0)
            {
                return null;
            }

            return $"{scheme}://{effectiveHost}:{effectivePort}/";
        }
    }

    public sealed class WebhookConfiguration
    {
        public string? MessageUpdateUrl { get; set; }
    }
}
