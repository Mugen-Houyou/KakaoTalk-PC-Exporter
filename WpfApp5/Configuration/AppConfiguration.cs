using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WpfApp5.Configuration
{
    public sealed class AppConfiguration
    {
        private const string DefaultExporterHostname = "mytesthost123";
        public DatabaseConfiguration Database { get; init; }
        public RestApiConfiguration RestApi { get; init; }
        public WebhookConfiguration Webhook { get; init; }
        public string ExporterHostname { get; set; }

        // init 전용 속성은 생성자에서 설정 가능
        public AppConfiguration()
        {
            Database = new DatabaseConfiguration();
            RestApi = new RestApiConfiguration();
            Webhook = new WebhookConfiguration();
            ExporterHostname = DefaultExporterHostname;
        }

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
            config.Webhook.Normalize();
            if (string.IsNullOrWhiteSpace(config.ExporterHostname))
                config.ExporterHostname = DefaultExporterHostname;
            else
                config.ExporterHostname = config.ExporterHostname.Trim();

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

    }

    public sealed class DatabaseConfiguration
    {
        public string? Path { get; set; }
    }

    public sealed class RestApiConfiguration
    {
        private const string DefaultHost = "localhost";
        private const int DefaultPort = 15099;
        private const string AnyHostWildcard = "+";

        public string? Host { get; set; } = DefaultHost;
        public string[]? Hosts { get; set; }
        public int? Port { get; set; } = DefaultPort;
        public bool UseHttps { get; set; }
        public bool AllowAnyHost { get; set; }

        [JsonIgnore]
        public string? Prefix => BuildPrefix();

        [JsonIgnore]
        public IReadOnlyList<string>? Prefixes => BuildPrefixes();

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

            if (Hosts is { Length: > 0 })
            {
                Hosts = CleanHostsArray(Hosts);
                if (Hosts?.Length == 0)
                {
                    Hosts = null;
                }
            }

            if ((Hosts is null || Hosts.Length == 0) && !string.IsNullOrWhiteSpace(Host) && Host.Contains(',', StringComparison.Ordinal))
            {
                Hosts = CleanHostsArray(Host.Split(',', StringSplitOptions.RemoveEmptyEntries));
                if (Hosts?.Length == 0)
                {
                    Hosts = null;
                }
            }

            if (Hosts is { Length: > 0 })
            {
                Host = Hosts[0];
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
            var prefixes = BuildPrefixes();
            return prefixes is { Count: > 0 } ? prefixes[0] : null;
        }

        private static string[]? CleanHostsArray(string[] hosts)
        {
            if (hosts.Length == 0)
            {
                return Array.Empty<string>();
            }

            var cleaned = new List<string>(hosts.Length);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var host in hosts)
            {
                if (string.IsNullOrWhiteSpace(host))
                {
                    continue;
                }

                var trimmed = host.Trim();
                if (seen.Add(trimmed))
                {
                    cleaned.Add(trimmed);
                }
            }

            return cleaned.Count == 0 ? Array.Empty<string>() : cleaned.ToArray();
        }

        private IReadOnlyList<string>? BuildPrefixes()
        {
            var effectivePort = Port ?? DefaultPort;
            if (effectivePort <= 0 || effectivePort > 65535)
            {
                return null;
            }

            var scheme = UseHttps ? "https" : "http";

            if (AllowAnyHost)
            {
                return new[] { $"{scheme}://{AnyHostWildcard}:{effectivePort}/" };
            }

            var configuredHosts = Hosts is { Length: > 0 }
                ? Hosts
                : (string.IsNullOrWhiteSpace(Host) ? Array.Empty<string>() : new[] { Host });

            if (configuredHosts.Length == 0)
            {
                return null;
            }

            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var hostEntry in configuredHosts)
            {
                if (string.IsNullOrWhiteSpace(hostEntry))
                {
                    continue;
                }

                var trimmed = hostEntry.Trim();
                if (TryBuildPrefix(scheme, trimmed, effectivePort, out var prefix) && seen.Add(prefix))
                {
                    result.Add(prefix);
                }
            }

            return result.Count == 0 ? null : result;
        }

        private static bool TryBuildPrefix(string scheme, string hostEntry, int fallbackPort, out string prefix)
        {
            prefix = string.Empty;

            if (hostEntry.Contains("://", StringComparison.Ordinal))
            {
                if (!Uri.TryCreate(hostEntry, UriKind.Absolute, out var absolute))
                {
                    return false;
                }

                var builder = new UriBuilder(absolute)
                {
                    Scheme = scheme,
                    Port = absolute.IsDefaultPort ? fallbackPort : absolute.Port
                };

                prefix = NormalizePrefix(builder.Uri);
                return true;
            }

            if (!Uri.TryCreate($"{scheme}://{hostEntry}", UriKind.Absolute, out var parsed))
            {
                return false;
            }

            var builderDefault = new UriBuilder
            {
                Scheme = scheme,
                Host = parsed.Host,
                Port = parsed.IsDefaultPort ? fallbackPort : parsed.Port
            };

            prefix = NormalizePrefix(builderDefault.Uri);
            return true;
        }

        private static string NormalizePrefix(Uri uri)
        {
            var absolute = uri.AbsoluteUri;
            return absolute.EndsWith("/", StringComparison.Ordinal) ? absolute : absolute + "/";
        }
    }

    public sealed class WebhookConfiguration
    {
        private const string DefaultRemoteHost = "http://localhost:8080";
        private const string DefaultMessageUpdatePath = "/webhook/message-update";
        private const string DefaultHealthCheckPath = "/webhook/health";

        public string? RemoteHost { get; set; }
        public string? Prefix { get; set; }
        public string? MessageUpdateUrl { get; set; }
        public string? HealthCheck { get; set; }

        [JsonIgnore]
        public string MessageUpdateEndpoint => BuildEndpoint(MessageUpdateUrl);

        [JsonIgnore]
        public string HealthCheckEndpoint => BuildEndpoint(HealthCheck);

        public void Normalize()
        {
            RemoteHost = NormalizeRemoteHost(RemoteHost);
            Prefix = NormalizePrefix(Prefix);
            MessageUpdateUrl = NormalizePath(MessageUpdateUrl, DefaultMessageUpdatePath);
            HealthCheck = NormalizePath(HealthCheck, DefaultHealthCheckPath);
        }

        private string BuildEndpoint(string? relativePath)
        {
            var baseUri = BuildBaseUri();

            if (string.IsNullOrWhiteSpace(relativePath) || relativePath.Trim() == "/")
            {
                return baseUri.ToString().TrimEnd('/');
            }

            var trimmed = relativePath.Trim();
            if (trimmed.StartsWith("/", StringComparison.Ordinal))
            {
                trimmed = trimmed.Substring(1);
            }

            var combined = new Uri(baseUri, trimmed);
            return combined.ToString();
        }

        private Uri BuildBaseUri()
        {
            var remote = RemoteHost ?? DefaultRemoteHost;
            if (!Uri.TryCreate(remote, UriKind.Absolute, out var remoteUri))
            {
                remoteUri = new Uri(DefaultRemoteHost);
            }

            var prefix = Prefix;
            if (string.IsNullOrEmpty(prefix))
            {
                var absolute = remoteUri.AbsoluteUri;
                if (!absolute.EndsWith("/", StringComparison.Ordinal))
                {
                    absolute += "/";
                }

                return new Uri(absolute, UriKind.Absolute);
            }

            var normalized = prefix;
            if (!normalized.StartsWith("/", StringComparison.Ordinal))
            {
                normalized = "/" + normalized;
            }

            if (!normalized.EndsWith("/", StringComparison.Ordinal))
            {
                normalized += "/";
            }

            return new Uri(remoteUri, normalized);
        }

        private static string NormalizeRemoteHost(string? remoteHost)
        {
            if (string.IsNullOrWhiteSpace(remoteHost))
            {
                return DefaultRemoteHost;
            }

            var trimmed = remoteHost.Trim();
            if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
            {
                return DefaultRemoteHost;
            }

            var builder = new UriBuilder(uri)
            {
                Query = string.Empty,
                Fragment = string.Empty,
                Path = string.Empty
            };

            return builder.Uri.GetLeftPart(UriPartial.Authority);
        }

        private static string NormalizePrefix(string? prefix)
        {
            if (string.IsNullOrWhiteSpace(prefix) || prefix.Trim() == "/")
            {
                return string.Empty;
            }

            var trimmed = prefix.Trim();

            if (!trimmed.StartsWith("/", StringComparison.Ordinal))
            {
                trimmed = "/" + trimmed;
            }

            trimmed = trimmed.TrimEnd('/');
            return trimmed;
        }

        private static string NormalizePath(string? path, string defaultValue)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return defaultValue;
            }

            var trimmed = path.Trim();

            if (!trimmed.StartsWith("/", StringComparison.Ordinal))
            {
                trimmed = "/" + trimmed;
            }

            if (trimmed.Length > 1)
            {
                trimmed = trimmed.TrimEnd('/');
            }

            return trimmed;
        }
    }
}
