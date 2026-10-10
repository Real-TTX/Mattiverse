using Mattiverse.Core.Entities;
using Microsoft.Extensions.Caching.Memory;

namespace Mattiverse.Web.Support;

/// <summary>Can the app be shown inside Mattiverse (an iframe)? If not: why not.</summary>
public sealed record FrameCheck(bool Embeddable, string? Reason);

/// <summary>
/// Looks at the response headers of an app to see whether it allows being embedded (<c>X-Frame-Options</c>,
/// <c>Content-Security-Policy: frame-ancestors</c>). Apps that forbid it are opened in a new tab instead of showing a blank frame.
/// </summary>
public sealed class FrameProbe
{
    private static readonly TimeSpan CacheTime = TimeSpan.FromMinutes(5);

    private readonly AppUrls _urls;
    private readonly IHttpClientFactory _http;
    private readonly IMemoryCache _cache;

    public FrameProbe(AppUrls urls, IHttpClientFactory http, IMemoryCache cache)
    {
        _urls = urls;
        _http = http;
        _cache = cache;
    }

    public async Task<FrameCheck> CheckAsync(InstalledApp app, string openUrl, bool pageIsHttps, CancellationToken ct = default)
    {
        // A secure page (Mattiverse behind HTTPS) cannot show a plain-HTTP frame: the browser blocks it.
        if (pageIsHttps && openUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            return new FrameCheck(false, "The app is served over plain HTTP, which a secure page cannot embed. Give the app a domain with the reverse proxy to embed it here.");
        }

        var key = $"frame:{app.Slug}:{app.HostPort}:{app.PublicUrl}";
        if (_cache.TryGetValue(key, out FrameCheck? cached) && cached is not null)
        {
            return cached;
        }

        var result = await ProbeAsync(app, ct);
        // Only remember positive answers: an app that is still starting must be checked again.
        if (result.Embeddable)
        {
            _cache.Set(key, result, CacheTime);
        }

        return result;
    }

    private async Task<FrameCheck> ProbeAsync(InstalledApp app, CancellationToken ct)
    {
        var url = await _urls.InternalUrlAsync(app, ct);
        if (url is null)
        {
            return new FrameCheck(false, "The app has no web interface.");
        }

        try
        {
            var client = _http.CreateClient("probe");
            client.Timeout = TimeSpan.FromSeconds(4);
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);

            if (response.Headers.TryGetValues("X-Frame-Options", out var xfo))
            {
                var value = string.Join(',', xfo).Trim();
                if (!value.Equals("ALLOWALL", StringComparison.OrdinalIgnoreCase))
                {
                    return new FrameCheck(false, $"The app forbids being embedded (X-Frame-Options: {value}).");
                }
            }

            if (response.Headers.TryGetValues("Content-Security-Policy", out var csp))
            {
                foreach (var policy in csp)
                {
                    var directive = policy.Split(';').Select(d => d.Trim()).FirstOrDefault(d => d.StartsWith("frame-ancestors", StringComparison.OrdinalIgnoreCase));
                    if (directive is not null && !directive.Contains('*'))
                    {
                        return new FrameCheck(false, "The app forbids being embedded (Content-Security-Policy: frame-ancestors).");
                    }
                }
            }

            return new FrameCheck(true, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new FrameCheck(false, "The app does not answer right now. Is it running?");
        }
    }
}
