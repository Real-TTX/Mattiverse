using Mattiverse.Core.Jobs;
using Mattiverse.Core.Users;
using Microsoft.AspNetCore.Authentication;

namespace Mattiverse.Web.Infrastructure;

/// <summary>The few JSON endpoints the pages' JavaScript uses (live job log, theme).</summary>
public static class ApiEndpoints
{
    public sealed record ThemeRequest(string Theme);

    public static void MapJobsApi(this WebApplication app)
    {
        // The live log of a job; polled by the progress views. Admins only: logs show what runs on the host.
        app.MapGet("/api/jobs/{id:long}/log", async (long id, int? offset, JobService jobs, CancellationToken ct) =>
        {
            var chunk = await jobs.ReadLogAsync(id, offset ?? 0, ct);
            return chunk is null
                ? Results.NotFound()
                : Results.Json(new
                {
                    status = chunk.Status.ToString(),
                    text = chunk.Text,
                    offset = chunk.Offset,
                    finished = chunk.Finished,
                    error = chunk.Error,
                });
        }).RequireAuthorization("AdminOnly");

        // Remembers the theme on the account (the browser applies it itself right away) and refreshes the cookie claim.
        app.MapPost("/api/theme", async (ThemeRequest request, HttpContext http, UserService users, CancellationToken ct) =>
        {
            var userId = http.User.GetUserId();
            if (userId is null || request.Theme is not ("system" or "light" or "dark"))
            {
                return Results.BadRequest();
            }

            var user = await users.SetThemeAsync(userId.Value, request.Theme, ct);
            if (user is null)
            {
                return Results.NotFound();
            }

            await http.SignInAsync(AuthConstants.CookieScheme, PrincipalFactory.Build(user, http.User.GetSessionToken() ?? string.Empty));
            return Results.NoContent();
        }).RequireAuthorization();
    }
}
