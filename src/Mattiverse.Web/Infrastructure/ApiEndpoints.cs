using Mattiverse.Core.Jobs;
using Mattiverse.Core.Users;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;

namespace Mattiverse.Web.Infrastructure;

/// <summary>The few endpoints the pages' JavaScript uses (live job log, theme).</summary>
public static class ApiEndpoints
{
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

        // Remembers the theme on the account (the browser applies it itself right away) and refreshes the cookie claim. The toolkit's theme button posts
        // the choice as a form (theme=system|light|dark) with the anti-forgery token of the page.
        app.MapPost("/api/theme", async (HttpContext http, IAntiforgery antiforgery, UserService users, CancellationToken ct) =>
        {
            try
            {
                await antiforgery.ValidateRequestAsync(http);
            }
            catch (AntiforgeryValidationException)
            {
                return Results.BadRequest();
            }

            var theme = http.Request.Form["theme"].ToString();
            var userId = http.User.GetUserId();
            if (userId is null || theme is not ("system" or "light" or "dark"))
            {
                return Results.BadRequest();
            }

            var user = await users.SetThemeAsync(userId.Value, theme, ct);
            if (user is null)
            {
                return Results.NotFound();
            }

            await http.SignInAsync(AuthConstants.CookieScheme, PrincipalFactory.Build(user, http.User.GetSessionToken() ?? string.Empty));
            return Results.NoContent();
        }).RequireAuthorization();
    }
}
