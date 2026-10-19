using System.Text.RegularExpressions;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Mattiverse.Core.Catalog;

/// <summary>Turns a catalog file (compose + <c>x-mattiverse</c>) into a <see cref="CatalogApp"/>.</summary>
public static partial class CatalogParser
{
    private static readonly IDeserializer MetaDeserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private static readonly IDeserializer GenericDeserializer = new DeserializerBuilder().Build();

    private sealed class Document
    {
        [YamlMember(Alias = "x-mattiverse", ApplyNamingConventions = false)]
        public CatalogApp? Meta { get; set; }
    }

    /// <summary>Parses one file. Throws <see cref="FormatException"/> with a readable message when it is not a valid entry.</summary>
    public static CatalogApp Parse(string yaml, string source)
    {
        Document document;
        try
        {
            document = MetaDeserializer.Deserialize<Document>(yaml) ?? new Document();
        }
        catch (Exception ex)
        {
            throw new FormatException($"{source}: {ex.Message}", ex);
        }

        var app = document.Meta ?? throw new FormatException($"{source}: the top-level 'x-mattiverse' block is missing.");
        if (!IdRegex().IsMatch(app.Id))
        {
            throw new FormatException($"{source}: 'id' must be lowercase letters, digits and dashes (got '{app.Id}').");
        }

        if (string.IsNullOrWhiteSpace(app.Name))
        {
            throw new FormatException($"{source}: 'name' is missing.");
        }

        app.ComposeYaml = yaml;
        app.Source = source;
        ReadServices(app, yaml, source);

        if (app.Ui is not null)
        {
            if (!app.ServiceNames.Contains(app.Ui.Service))
            {
                throw new FormatException($"{source}: ui.service '{app.Ui.Service}' is not a service of the compose file.");
            }

            if (app.Ui.Port is <= 0 or > 65535)
            {
                throw new FormatException($"{source}: ui.port is missing or invalid.");
            }
        }

        foreach (var shared in app.Shared.Where(s => !app.ServiceNames.Contains(s)))
        {
            throw new FormatException($"{source}: shared service '{shared}' is not a service of the compose file.");
        }

        if (app.Mail?.Sender is { } sender && !SenderRegex().IsMatch(sender))
        {
            throw new FormatException($"{source}: mail.sender must be the part of an address before the @ (letters, digits, dots, dashes; got '{sender}').");
        }

        if ((app.Platform || app.Identity is not null) && app.Ui is null)
        {
            throw new FormatException($"{source}: 'platform' and 'identity' need a 'ui' (the service that answers /api and /signin-oidc).");
        }

        AddManagedVariables(app);
        return app;
    }

    /// <summary>
    /// The variables Mattiverse fills itself for an app that speaks the platform contract (the key of its /api), signs in through MatIdentity (where MatIdentity
    /// is and the app's client) or sends mail through MatSMTP (where MatSMTP is, the app's relay user, its sender): hidden in the form, written to the <c>.env</c> like every variable, so the compose file uses them as <c>${MAT_API_KEY}</c> and so on.
    /// An entry that declares one of them itself keeps its own declaration.
    /// </summary>
    private static void AddManagedVariables(CatalogApp app)
    {
        void Add(string key, string label, bool secret, bool generate = false)
        {
            if (app.Variables.All(v => v.Key != key))
            {
                app.Variables.Add(new CatalogVariable { Key = key, Label = label, Type = secret ? "secret" : "text", Generate = generate, Hidden = true });
            }
        }

        if (app.Platform)
        {
            Add(ManagedVariables.ApiKey, "Platform API key", secret: true, generate: true);
        }

        if (app.Identity is not null)
        {
            Add(ManagedVariables.IdentityAuthority, "MatIdentity address", secret: false);
            Add(ManagedVariables.IdentityInternalUrl, "MatIdentity address inside Docker", secret: false);
            Add(ManagedVariables.IdentityClientId, "MatIdentity client id", secret: false);
            Add(ManagedVariables.IdentityClientSecret, "MatIdentity client secret", secret: true);
        }

        if (app.Mail is not null)
        {
            Add(ManagedVariables.MailHost, "MatSMTP address", secret: false);
            Add(ManagedVariables.MailPort, "MatSMTP port", secret: false);
            Add(ManagedVariables.MailUsername, "MatSMTP relay user", secret: false);
            Add(ManagedVariables.MailPassword, "MatSMTP relay password", secret: true);
            Add(ManagedVariables.MailFrom, "Sender address", secret: false);
            Add(ManagedVariables.MailTls, "MatSMTP TLS mode", secret: false);
        }
    }

    private static void ReadServices(CatalogApp app, string yaml, string source)
    {
        object? root;
        try
        {
            root = GenericDeserializer.Deserialize<object>(yaml);
        }
        catch (Exception ex)
        {
            throw new FormatException($"{source}: {ex.Message}", ex);
        }

        if (root is not IDictionary<object, object> map ||
            !map.TryGetValue("services", out var servicesRaw) ||
            servicesRaw is not IDictionary<object, object> services ||
            services.Count == 0)
        {
            throw new FormatException($"{source}: the compose file has no 'services'.");
        }

        foreach (var (name, definition) in services)
        {
            app.ServiceNames.Add(name.ToString()!);
            if (definition is IDictionary<object, object> service && service.TryGetValue("ports", out var portsRaw) && portsRaw is IList<object> ports)
            {
                foreach (var port in ports)
                {
                    var parsed = ParsePort(port);
                    if (parsed is not null)
                    {
                        app.FixedPorts.Add(parsed);
                    }
                }
            }
        }
    }

    private static CatalogPort? ParsePort(object? definition)
    {
        string? text = definition switch
        {
            string s => s,
            IDictionary<object, object> map when map.TryGetValue("published", out var published) => published.ToString() +
                ":" + (map.TryGetValue("target", out var target) ? target : "0") +
                (map.TryGetValue("protocol", out var protocol) ? "/" + protocol : string.Empty),
            _ => null,
        };

        return text is null ? null : ParsePort(text);
    }

    /// <summary>
    /// Reads <c>"80:80"</c>, <c>"443:443/udp"</c>, <c>"127.0.0.1:9293:9293"</c> and <c>"${PORT:-25}:25"</c> (the default
    /// is used). Returns <c>null</c> for a port without a fixed host side (<c>"8080"</c>).
    /// </summary>
    public static CatalogPort? ParsePort(string text)
    {
        var protocolName = "tcp";
        var slash = text.LastIndexOf('/');
        var body = text;
        if (slash >= 0)
        {
            protocolName = text[(slash + 1)..];
            body = text[..slash];
        }

        // Resolve ${VAR:-default} to its default so the published port is known without the variables.
        body = Apps.EnvSubstitution.Apply(body, null);
        var parts = body.Split(':');
        if (parts.Length < 2)
        {
            return null; // "8080" only: an ephemeral host port, nothing fixed.
        }

        var hostPart = parts.Length == 3 ? parts[1] : parts[0];
        return int.TryParse(hostPart, out var hostPort)
            ? new CatalogPort { Raw = text, HostPort = hostPort, Protocol = protocolName }
            : null;
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,40}$")]
    private static partial Regex IdRegex();

    [GeneratedRegex("^[a-z0-9]([a-z0-9._-]{0,62}[a-z0-9])?$")]
    private static partial Regex SenderRegex();
}
