using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mattiverse.Core.Apps;
using Mattiverse.Core.Catalog;
using Mattiverse.Core.Data;
using Mattiverse.Core.Docker;
using Mattiverse.Core.Entities;
using Mattiverse.Core.Security;
using Microsoft.EntityFrameworkCore;

namespace Mattiverse.Core.Integrations;

/// <summary>A route of the Matcad proxy as far as Mattiverse needs it.</summary>
public sealed record MatcadRoute(long Id, string Host, string? Upstream);

/// <summary>
/// Client of the Matcad REST API (<c>/api/v1</c>, header <c>X-Api-Key</c>). The address and the key of the Matcad that
/// Mattiverse installed are looked up from the installed app, so there is nothing to configure by hand.
/// </summary>
public sealed class MatcadClient
{
    private readonly MattiverseDbContext _db;
    private readonly CatalogService _catalog;
    private readonly ISecretProtector _protector;
    private readonly DockerService _docker;
    private readonly ComposeFiles _files;
    private readonly IHttpClientFactory _http;

    public MatcadClient(
        MattiverseDbContext db,
        CatalogService catalog,
        ISecretProtector protector,
        DockerService docker,
        ComposeFiles files,
        IHttpClientFactory http)
    {
        _db = db;
        _catalog = catalog;
        _protector = protector;
        _docker = docker;
        _files = files;
        _http = http;
    }

    /// <summary>The installed Matcad, or <c>null</c> when none is installed.</summary>
    public Task<InstalledApp?> FindAppAsync(CancellationToken ct = default)
        => _db.InstalledApps.AsNoTracking()
            .FirstOrDefaultAsync(a => a.CatalogId == "matcad" && a.Lifecycle == AppLifecycle.Installed, ct);

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
        => await FindAppAsync(ct) is not null;

    /// <summary>Waits until the API answers (Caddy and Matcad need a few seconds after the start).</summary>
    public async Task<bool> WaitReadyAsync(TimeSpan timeout, Action<string> log, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        var attempt = 0;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var request = await CreateRequestAsync(HttpMethod.Get, "status", ct);
                using var response = await Client().SendAsync(request, ct);
                if (response.IsSuccessStatusCode)
                {
                    return true;
                }

                if (++attempt == 1)
                {
                    log($"Waiting for the Matcad API (HTTP {(int)response.StatusCode}) ...");
                }
            }
            catch (HttpRequestException)
            {
                if (++attempt == 1)
                {
                    log("Waiting for the Matcad API to come up ...");
                }
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                // A single request timed out; try again.
            }

            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }

        return false;
    }

    public async Task<string?> GetBaseDomainAsync(CancellationToken ct = default)
    {
        var settings = await GetSettingsAsync(ct);
        return settings?["baseDomain"]?.GetValue<string>();
    }

    /// <summary>Sets the base domain in Matcad's settings (all other settings stay as they are).</summary>
    public async Task SetBaseDomainAsync(string? baseDomain, CancellationToken ct = default)
    {
        var settings = await GetSettingsAsync(ct) ?? new JsonObject();
        settings["baseDomain"] = baseDomain;
        using var request = await CreateRequestAsync(HttpMethod.Put, "settings", ct);
        request.Content = JsonContent.Create(settings);
        using var response = await Client().SendAsync(request, ct);
        await EnsureSuccessAsync(response, "update the Matcad settings", ct);
    }

    public async Task<List<MatcadRoute>> ListRoutesAsync(CancellationToken ct = default)
    {
        using var request = await CreateRequestAsync(HttpMethod.Get, "routes/manual", ct);
        using var response = await Client().SendAsync(request, ct);
        await EnsureSuccessAsync(response, "list the Matcad routes", ct);

        var routes = new List<MatcadRoute>();
        var array = await response.Content.ReadFromJsonAsync<JsonArray>(ct) ?? [];
        foreach (var node in array.OfType<JsonObject>())
        {
            var id = node["id"]?.GetValue<long>();
            var host = node["host"]?.GetValue<string>();
            if (id is not null && !string.IsNullOrEmpty(host))
            {
                routes.Add(new MatcadRoute(id.Value, host, node["upstream"]?.GetValue<string>()));
            }
        }

        return routes;
    }

    /// <summary>Creates the route for <paramref name="host"/> or updates the existing one (Matcad upserts by id, so it is looked up first).</summary>
    public async Task UpsertRouteAsync(string host, string upstream, string name, CancellationToken ct = default)
    {
        var existing = (await ListRoutesAsync(ct)).FirstOrDefault(r => r.Host.Equals(host, StringComparison.OrdinalIgnoreCase));
        var body = new JsonObject
        {
            ["host"] = host,
            ["wildcard"] = false,
            ["target"] = "proxy",
            ["upstream"] = upstream,
            ["enabled"] = true,
            ["name"] = name,
        };
        if (existing is not null)
        {
            body["id"] = existing.Id;
        }

        using var request = await CreateRequestAsync(HttpMethod.Post, "routes", ct);
        request.Content = JsonContent.Create(body);
        using var response = await Client().SendAsync(request, ct);
        await EnsureSuccessAsync(response, $"create the route for {host}", ct);
    }

    public async Task DeleteRouteAsync(string host, CancellationToken ct = default)
    {
        var existing = (await ListRoutesAsync(ct)).FirstOrDefault(r => r.Host.Equals(host, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            return;
        }

        using var request = await CreateRequestAsync(HttpMethod.Delete, $"routes/{existing.Id}", ct);
        using var response = await Client().SendAsync(request, ct);
        await EnsureSuccessAsync(response, $"delete the route for {host}", ct);
    }

    // ------------------------------------------------------------------ plumbing

    private async Task<JsonObject?> GetSettingsAsync(CancellationToken ct)
    {
        using var request = await CreateRequestAsync(HttpMethod.Get, "settings", ct);
        using var response = await Client().SendAsync(request, ct);
        await EnsureSuccessAsync(response, "read the Matcad settings", ct);
        return await response.Content.ReadFromJsonAsync<JsonObject>(ct);
    }

    private HttpClient Client()
    {
        var client = _http.CreateClient("matcad");
        client.Timeout = TimeSpan.FromSeconds(15);
        return client;
    }

    private async Task<HttpRequestMessage> CreateRequestAsync(HttpMethod method, string path, CancellationToken ct)
    {
        var app = await FindAppAsync(ct) ?? throw new InvalidOperationException("Matcad is not installed.");
        var catalog = _catalog.Get(app.CatalogId);
        var variables = AppVariables.Deserialize(catalog, app.VariablesJson, _protector);
        if (!variables.TryGetValue("MATCAD_API_KEY", out var apiKey) || string.IsNullOrEmpty(apiKey))
        {
            throw new InvalidOperationException("The Matcad API key is not available (the Data Protection keys were lost?).");
        }

        // Inside Docker the shared network resolves the container name; outside (local development) the host port is used.
        var inDocker = await _docker.GetSelfContainerNameAsync(ct) is not null;
        var baseUrl = inDocker
            ? $"http://{_files.UiHost(app)}:{app.UiPort}"
            : $"http://localhost:{app.HostPort}";

        var request = new HttpRequestMessage(method, $"{baseUrl}/api/v1/{path}");
        request.Headers.Add("X-Api-Key", apiKey);
        return request;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string action, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var detail = await response.Content.ReadAsStringAsync(ct);
        try
        {
            using var doc = JsonDocument.Parse(detail);
            if (doc.RootElement.TryGetProperty("error", out var error) || doc.RootElement.TryGetProperty("detail", out error))
            {
                detail = error.GetString() ?? detail;
            }
        }
        catch (JsonException)
        {
            // Not JSON: keep the text.
        }

        throw new InvalidOperationException($"Matcad could not {action} (HTTP {(int)response.StatusCode}): {detail}");
    }
}
