namespace Mattiverse.Core.Configuration;

/// <summary>Typed access to <c>config/system.json</c>.</summary>
public sealed class SystemConfigService
{
    private readonly JsonConfigStore _store;

    public SystemConfigService(JsonConfigStore store)
    {
        _store = store;
    }

    public Task<SystemConfig> GetAsync(CancellationToken ct = default)
        => _store.GetAsync<SystemConfig>(SystemConfig.FileName, ct);

    public Task UpdateAsync(Action<SystemConfig> change, CancellationToken ct = default)
        => _store.UpdateAsync(SystemConfig.FileName, change, ct);
}
