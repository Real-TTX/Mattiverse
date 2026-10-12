using System.ComponentModel.DataAnnotations;
using Mattiverse.Core.Entities;
using Mattiverse.Core.Security;
using Mattiverse.Core.Users;
using Mattiverse.Web.Controls;
using Mattiverse.Web.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Mattiverse.Web.Pages.Account;

public class ProfileModel : PageModel
{
    private readonly UserService _userService;
    private readonly IPasswordHasher _passwordHasher;

    public ProfileModel(UserService userService, IPasswordHasher passwordHasher)
    {
        _userService = userService;
        _passwordHasher = passwordHasher;
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    [BindProperty(SupportsGet = true)]
    public string? Tab { get; set; }

    public string Username { get; private set; } = string.Empty;
    public string DisplayName { get; private set; } = string.Empty;
    public UserRole Role { get; private set; }
    public bool MustChange { get; private set; }
    [TempData] public string? StatusMessage { get; set; }

    /// <summary>The password tab is shown when a change is required or the last submit failed.</summary>
    public bool PasswordTab => MustChange || Tab == "password" || !ModelState.IsValid;

    public TabBarModel Tabs => new()
    {
        Tabs =
        [
            new TabItem { Title = "General", Href = "/Account/Profile", Icon = "user", Active = !PasswordTab },
            new TabItem { Title = "Password", Href = "/Account/Profile?tab=password", Icon = "key", Active = PasswordTab },
        ],
    };

    public class InputModel
    {
        [Required(ErrorMessage = "Please enter your current password.")]
        [DataType(DataType.Password)]
        public string CurrentPassword { get; set; } = string.Empty;

        [Required(ErrorMessage = "Please enter a new password.")]
        [MinLength(8, ErrorMessage = "The password must be at least 8 characters.")]
        [DataType(DataType.Password)]
        public string NewPassword { get; set; } = string.Empty;

        [DataType(DataType.Password)]
        [Compare(nameof(NewPassword), ErrorMessage = "The passwords do not match.")]
        public string ConfirmPassword { get; set; } = string.Empty;
    }

    public async Task<IActionResult> OnGetAsync()
        => await LoadAsync() ? Page() : Forbid();

    public async Task<IActionResult> OnPostAsync()
    {
        if (!await LoadAsync())
        {
            return Forbid();
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        var userId = User.GetUserId()!.Value;
        var user = await _userService.GetAsync(userId, HttpContext.RequestAborted);
        if (user is null)
        {
            return Forbid();
        }

        if (!_passwordHasher.Verify(Input.CurrentPassword, user.PasswordHash))
        {
            ModelState.AddModelError("Input.CurrentPassword", "The current password is incorrect.");
            return Page();
        }

        await _userService.ChangePasswordAsync(userId, Input.NewPassword, HttpContext.RequestAborted);

        // Re-issue the cookie so the "must change password" flag is cleared immediately.
        user.MustChangePassword = false;
        await HttpContext.SignInAsync(AuthConstants.CookieScheme, PrincipalFactory.Build(user, User.GetSessionToken() ?? string.Empty));

        StatusMessage = "Password changed.";
        return RedirectToPage();
    }

    private async Task<bool> LoadAsync()
    {
        var userId = User.GetUserId();
        if (userId is null)
        {
            return false;
        }

        var user = await _userService.GetAsync(userId.Value, HttpContext.RequestAborted);
        if (user is null)
        {
            return false;
        }

        Username = user.Username;
        DisplayName = user.DisplayName;
        Role = user.Role;
        MustChange = user.MustChangePassword;
        return true;
    }
}
