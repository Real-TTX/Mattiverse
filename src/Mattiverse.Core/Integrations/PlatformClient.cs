using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mattiverse.Core.Apps;
using Mattiverse.Core.Catalog;
using Mattiverse.Core.Docker;
using Mattiverse.Core.Entities;
using Mattiverse.Core.Security;

namespace Mattiverse.Core.Integrations;

// The MatApiDefinition of an app as Mattiverse reads it (MatDesign docs/PLATFORM.md, contract version 1). Only what Mattiverse draws; a field it does not know is ignored.

public sealed record PlatformDefinition(
    int Contract,
    PlatformAppInfo App,
    IReadOnlyList<PlatformPage> Pages,
    IReadOnlyList<PlatformStateItem> State,
    IReadOnlyList<PlatformField> Configure,
    IReadOnlyList<PlatformAction> Actions);

public sealed record PlatformAppInfo(string Id, string Name, string? Description, string? Version);

public sealed record PlatformPage(string Id, string Label, string? Icon, IReadOnlyList<PlatformSection> Sections);

public sealed record PlatformSection(string Kind, IReadOnlyList<string>? Items, string? Label, string? Url);

public sealed record PlatformStateItem(string Key, string Label, string? Widget, string? Icon, string? Unit, int? Decimals, double? WarnAbove, double? ErrorAbove, JsonElement? Max);

public sealed record PlatformField(
    string Key,
    string Label,
    string Type,
    bool? Required,
    IReadOnlyList<PlatformOption>? Options,
    double? Min,
    double? Max,
    string? Pattern,
    string? Placeholder,
    string? Hint,
    string? Group,
    IReadOnlyDictionary<string, JsonElement>? ShowIf,
    bool? Restart);

public sealed record PlatformOption(string Value, string Label);

public sealed record PlatformAction(string Id, string Label, string? Icon, string Kind, string? Url, string? Confirm, bool? Danger);

/// <summary>A value of <c>/api/state</c>: the value itself (number, text, bool, time or null), a level (<c>ok</c>, <c>info</c>, <c>warn</c>, <c>error</c>) and a text.</summary>
public sealed record PlatformValue(JsonElement Value, string? Level, string? Text);

/// <summary>The answer of a change: the errors per key (empty = saved) or the message of an action.</summary>
public sealed record PlatformResult(bool Ok, string? Message, IReadOnlyDictionary<string, string> Errors);

/// <summary>
/// Talks to the platform API of an installed app (MatDesign docs/PLATFORM.md): its definition (<c>GET /api</c>), its values, its settings and its actions.
/// Mattiverse reaches the app on the shared network (or on its host port during development) and sends the key it gave the app (<c>MAT_API_KEY</c>).
/// </summary>
public sealed class PlatformClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly IHttpClientFactory _http;
    private readonly CatalogService _catalog;
    private readonly ISecretProtector _protector;
    private readonly DockerService _docker;
    private readonly ComposeFiles _files;

    public PlatformClient(IHttpClientFactory http, CatalogService catalog, ISecretProtector protector, DockerService docker, ComposeFiles files)
    {
        _http = http;
        _catalog = catalog;
        _protector = protector;
        _docker = docker;
        _files = files;
    }

    /// <summary>True when the catalog entry says the app speaks the contract.</summary>
    public bool Speaks(InstalledApp app) => _catalog.Get(app.CatalogId)?.Platform == true;

    /// <summary>The definition, or <c>null</c> when the app does not answer (it is stopped, or its image is older than the contract).</summary>
    public async Task<PlatformDefinition?> GetDefinitionAsync(InstalledApp app, CancellationToken ct = default)
    {
        try
        {
            using var response = await SendAsync(app, HttpMethod.Get, "/api", null, withKey: false, ct);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var definition = await response.Content.ReadFromJsonAsync<PlatformDefinition>(Json, ct);
            return definition is { Contract: >= 1 } ? definition : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or NotSupportedException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>The values of the tiles, by key (empty when the app does not answer).</summary>
    public async Task<IReadOnlyDictionary<string, PlatformValue>> GetStateAsync(InstalledApp app, CancellationToken ct = default)
    {
        try
        {
            using var response = await SendAsync(app, HttpMethod.Get, "/api/state", null, withKey: true, ct);
            if (!response.IsSuccessStatusCode)
            {
                return new Dictionary<string, PlatformValue>();
            }

            var body = await response.Content.ReadFromJsonAsync<StateBody>(Json, ct);
            return body?.Values ?? new Dictionary<string, PlatformValue>();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            return new Dictionary<string, PlatformValue>();
        }
    }

    /// <summary>The values of the settings, by key (a secret is <c>{ "isSet": bool }</c>).</summary>
    public async Task<IReadOnlyDictionary<string, JsonElement>> GetConfigurationAsync(InstalledApp app, CancellationToken ct = default)
    {
        try
        {
            using var response = await SendAsync(app, HttpMethod.Get, "/api/configure", null, withKey: true, ct);
            if (!response.IsSuccessStatusCode)
            {
                return new Dictionary<string, JsonElement>();
            }

            var body = await response.Content.ReadFromJsonAsync<ConfigureBody>(Json, ct);
            return body?.Values ?? new Dictionary<string, JsonElement>();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            return new Dictionary<string, JsonElement>();
        }
    }

    /// <summary>Saves settings (<c>PUT /api/configure</c>). The values are already typed by the caller (numbers as numbers, bools as bools).</summary>
    public async Task<PlatformResult> ConfigureAsync(InstalledApp app, IReadOnlyDictionary<string, object?> values, CancellationToken ct = default)
    {
        try
        {
            using var response = await SendAsync(app, HttpMethod.Put, "/api/configure", JsonContent.Create(new { values }, options: Json), withKey: true, ct);
            if (response.IsSuccessStatusCode)
            {
                return new PlatformResult(true, null, new Dictionary<string, string>());
            }

            var errors = await ReadErrorsAsync(response, ct);
            return new PlatformResult(false, errors.Values.FirstOrDefault() ?? $"The app answered HTTP {(int)response.StatusCode}.", errors);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            return new PlatformResult(false, "The app is not reachable: " + ex.Message, new Dictionary<string, string>());
        }
    }

    /// <summary>Runs an action of the kind <c>post</c>.</summary>
    public async Task<PlatformResult> RunActionAsync(InstalledApp app, string actionId, CancellationToken ct = default)
    {
        try
        {
            using var response = await SendAsync(app, HttpMethod.Post, "/api/actions/" + Uri.EscapeDataString(actionId), null, withKey: true, ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            string? message = null;
            try
            {
                using var doc = JsonDocument.Parse(text);
                message = doc.RootElement.TryGetProperty("message", out var m) ? m.GetString() : null;
            }
            catch (JsonException)
            {
                // No JSON: the status says enough.
            }

            return new PlatformResult(response.IsSuccessStatusCode, message ?? (response.IsSuccessStatusCode ? "Done." : $"The app answered HTTP {(int)response.StatusCode}."), new Dictionary<string, string>());
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            return new PlatformResult(false, "The app is not reachable: " + ex.Message, new Dictionary<string, string>());
        }
    }

    private async Task<HttpResponseMessage> SendAsync(InstalledApp app, HttpMethod method, string path, HttpContent? content, bool withKey, CancellationToken ct)
    {
        var baseUrl = await BaseUrlAsync(app, ct) ?? throw new InvalidOperationException("The app has no web service.");
        using var request = new HttpRequestMessage(method, baseUrl + path) { Content = content };
        request.Headers.Accept.ParseAdd("application/json");
        // Mattiverse is English: the app answers its texts (labels, units, messages) in English too.
        request.Headers.AcceptLanguage.ParseAdd("en");
        if (withKey)
        {
            var key = AppVariables.Deserialize(_catalog.Get(app.CatalogId), app.VariablesJson, _protector).GetValueOrDefault(ManagedVariables.ApiKey);
            if (string.IsNullOrEmpty(key))
            {
                throw new InvalidOperationException("The app has no platform key (install it again, or update it, so Mattiverse gives it one).");
            }

            request.Headers.Add("X-Api-Key", key);
        }

        var client = _http.CreateClient("platform");
        client.Timeout = TimeSpan.FromSeconds(10);
        return await client.SendAsync(request, ct);
    }

    /// <summary>The app on the shared network (Mattiverse in Docker), else on its host port (development).</summary>
    private async Task<string?> BaseUrlAsync(InstalledApp app, CancellationToken ct)
    {
        if (app.UiService is null || app.UiPort is null)
        {
            return null;
        }

        return await _docker.GetSelfContainerNameAsync(ct) is not null
            ? $"http://{_files.UiHost(app)}:{app.UiPort}"
            : app.HostPort is { } port ? $"http://localhost:{port}" : null;
    }

    private static async Task<Dictionary<string, string>> ReadErrorsAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var body = await response.Content.ReadFromJsonAsync<ErrorsBody>(Json, ct);
            return body?.Errors ?? new Dictionary<string, string>();
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>();
        }
    }

    private sealed record StateBody(Dictionary<string, PlatformValue>? Values);

    private sealed record ConfigureBody(Dictionary<string, JsonElement>? Values);

    private sealed record ErrorsBody(Dictionary<string, string>? Errors);
}
