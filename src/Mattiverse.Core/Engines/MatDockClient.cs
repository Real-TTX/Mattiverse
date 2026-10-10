using System.Text.Json.Nodes;
using Mattiverse.Core.Apps;

namespace Mattiverse.Core.Engines;

/// <summary>
/// Client of the MatDock REST API (see docs/ENGINES.md). MatDock owns the stacks, their containers, backups and schedules; what
/// Mattiverse writes there carries <c>managedBy: mattiverse</c>. Long tasks are asynchronous operations whose output is followed here
/// and forwarded to the job log.
/// </summary>
public sealed class MatDockClient : EngineClient
{
    public const string ManagedBy = "mattiverse";
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromMinutes(45);

    public MatDockClient(EngineLocator locator, IHttpClientFactory http)
        : base(locator, http)
    {
    }

    protected override string CatalogId => "matdock";

    protected override string KeyVariable => "MATDOCK_API_KEY";

    protected override string DisplayName => "MatDock";

    /// <summary>Makes sure MatDock manages the Docker engine of this host (an environment of type Local).</summary>
    public Task EnsureLocalEnvironmentAsync(CancellationToken ct = default)
        => SendAsync(HttpMethod.Put, "environments/local", new JsonObject(), null, ct);

    // ------------------------------------------------------------------ stacks

    /// <summary>Creates or updates the stack. With <paramref name="deploy"/> the stack is also started and the id of the operation is returned.</summary>
    public async Task<string?> UpsertStackAsync(StackSpec spec, bool deploy, bool pull, CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["composeYaml"] = spec.Compose,
            ["envContent"] = spec.Env,
            ["managedBy"] = ManagedBy,
            ["deploy"] = deploy,
            ["pull"] = pull,
        };
        var answer = await SendAsync(HttpMethod.Put, $"stacks/{Uri.EscapeDataString(spec.Name)}", body, null, ct);
        return answer?["operationId"]?.GetValue<string>();
    }

    /// <summary>The names of the stacks that carry <c>managedBy</c>.</summary>
    public async Task<List<string>> ListManagedStackNamesAsync(CancellationToken ct = default)
        => ((await GetAsync($"stacks?managedBy={ManagedBy}", ct) as JsonArray) ?? [])
            .OfType<JsonObject>()
            .Select(s => s["name"]?.GetValue<string>())
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .ToList();

    public async Task<StackSpec?> GetStackAsync(string name, string slug, CancellationToken ct = default)
    {
        try
        {
            var answer = await GetAsync($"stacks/{Uri.EscapeDataString(name)}", ct);
            return answer is null
                ? null
                : new StackSpec(name, slug, answer["composeYaml"]?.GetValue<string>() ?? string.Empty, answer["envContent"]?.GetValue<string>() ?? string.Empty);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("HTTP 404", StringComparison.Ordinal))
        {
            return null;
        }
    }

    /// <summary>Starts an action (<c>start</c>, <c>stop</c>, <c>restart</c>, <c>pull</c>, <c>redeploy</c>, <c>update</c>) and returns the operation id.</summary>
    public async Task<string> StartActionAsync(string name, string action, CancellationToken ct = default)
    {
        var answer = await SendAsync(HttpMethod.Post, $"stacks/{Uri.EscapeDataString(name)}/actions/{action}", new JsonObject { ["backupFirst"] = false }, null, ct);
        return answer?["operationId"]?.GetValue<string>() ?? throw new InvalidOperationException("MatDock did not start the operation.");
    }

    public async Task<string> DeleteStackAsync(string name, bool volumes, CancellationToken ct = default)
    {
        var answer = await SendAsync(HttpMethod.Delete, $"stacks/{Uri.EscapeDataString(name)}?volumes={(volumes ? "true" : "false")}", null, null, ct);
        return answer?["operationId"]?.GetValue<string>() ?? throw new InvalidOperationException("MatDock did not start the operation.");
    }

    // ------------------------------------------------------------------ backups

    public async Task<string> StartBackupAsync(string stack, bool stopContainers, string? note, CancellationToken ct = default)
    {
        var body = new JsonObject { ["stopContainers"] = stopContainers, ["note"] = note };
        var answer = await SendAsync(HttpMethod.Post, $"stacks/{Uri.EscapeDataString(stack)}/backups", body, null, ct);
        return answer?["operationId"]?.GetValue<string>() ?? throw new InvalidOperationException("MatDock did not start the backup.");
    }

    public async Task<JsonArray> ListBackupsAsync(string? stack, CancellationToken ct = default)
        => await GetAsync(stack is null ? "backups" : $"backups?stack={Uri.EscapeDataString(stack)}", ct) as JsonArray ?? [];

    public async Task<string> StartRestoreAsync(string backupId, bool backupCurrentFirst, CancellationToken ct = default)
    {
        var answer = await SendAsync(HttpMethod.Post, $"backups/{Uri.EscapeDataString(backupId)}/restore", new JsonObject { ["backupCurrentFirst"] = backupCurrentFirst }, null, ct);
        return answer?["operationId"]?.GetValue<string>() ?? throw new InvalidOperationException("MatDock did not start the restore.");
    }

    public Task DeleteBackupAsync(string backupId, CancellationToken ct = default)
        => SendAsync(HttpMethod.Delete, $"backups/{Uri.EscapeDataString(backupId)}", null, null, ct);

    public Task DownloadBackupAsync(string backupId, Stream destination, CancellationToken ct = default)
        => CopyToAsync($"backups/{Uri.EscapeDataString(backupId)}/download", destination, ct);

    public async Task<JsonArray> ListSchedulesAsync(CancellationToken ct = default)
        => await GetAsync("backup-schedules", ct) as JsonArray ?? [];

    public Task PutScheduleAsync(string name, JsonObject body, CancellationToken ct = default)
    {
        body["managedBy"] = ManagedBy;
        return SendAsync(HttpMethod.Put, $"backup-schedules/{Uri.EscapeDataString(name)}", body, null, ct);
    }

    public Task DeleteScheduleAsync(string name, CancellationToken ct = default)
        => SendAsync(HttpMethod.Delete, $"backup-schedules/{Uri.EscapeDataString(name)}", null, null, ct);

    // ------------------------------------------------------------------ operations

    /// <summary>Follows an operation to its end, forwarding the output to <paramref name="log"/>. Returns the operation's <c>result</c>.</summary>
    public async Task<JsonNode?> WaitOperationAsync(string operationId, Action<string> log, CancellationToken ct = default)
    {
        var offset = 0;
        var deadline = DateTime.UtcNow + OperationTimeout;
        while (DateTime.UtcNow < deadline)
        {
            var answer = await GetAsync($"operations/{Uri.EscapeDataString(operationId)}?offset={offset}", ct)
                ?? throw new InvalidOperationException("MatDock returned nothing for the operation.");

            if (answer["output"]?.GetValue<string>() is { Length: > 0 } output)
            {
                foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    log(line.TrimEnd('\r'));
                }
            }

            offset = answer["offset"]?.GetValue<int>() ?? offset;
            if (answer["finished"]?.GetValue<bool>() == true)
            {
                if (answer["status"]?.GetValue<string>() == "failed")
                {
                    throw new InvalidOperationException(answer["error"]?.GetValue<string>() ?? "The operation in MatDock failed.");
                }

                return answer["result"];
            }

            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }

        throw new InvalidOperationException("The operation in MatDock took too long.");
    }
}
