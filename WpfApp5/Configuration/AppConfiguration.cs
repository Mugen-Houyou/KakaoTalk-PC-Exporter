using System;
using System.IO;
using System.Text.Json;

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
            config.RestApi.Prefix = ResolveRestApiPrefix(config.RestApi.Prefix);
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

        private static string ResolveRestApiPrefix(string? configuredPrefix)
        {
            return string.IsNullOrWhiteSpace(configuredPrefix)
                ? "http://localhost:5010/"
                : configuredPrefix;
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
        public string? Prefix { get; set; }
    }

    public sealed class WebhookConfiguration
    {
        public string? MessageUpdateUrl { get; set; }
    }
}
