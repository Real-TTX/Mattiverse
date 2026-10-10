using Mattiverse.Core.Auth;
using Mattiverse.Web.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Mattiverse.Web.Pages.Account;

[AllowAnonymous]
public class LogoutModel : PageModel
{
    private readonly SessionService _sessionService;

    public LogoutModel(SessionService sessionService)
    {
        _sessionService = sessionService;
    }

    public IActionResult OnGet() => RedirectToPage("/Account/Login");

    public async Task<IActionResult> OnPostAsync()
    {
        var token = User.GetSessionToken();
        if (!string.IsNullOrEmpty(token))
        {
            await _sessionService.InvalidateAsync(token, HttpContext.RequestAborted);
        }

        await HttpContext.SignOutAsync(AuthConstants.CookieScheme);
        return RedirectToPage("/Account/Login");
    }
}
