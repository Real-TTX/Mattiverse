namespace Mattiverse.Web.Infrastructure;

/// <summary>Names for the authentication scheme and the custom claims Mattiverse stores in the cookie.</summary>
public static class AuthConstants
{
    public const string CookieScheme = "Mattiverse";

    public const string SessionTokenClaim = "mattiverse:session-token";
    public const string DisplayNameClaim = "mattiverse:display-name";
    public const string MustChangePasswordClaim = "mattiverse:must-change-password";
    public const string ThemeClaim = "mattiverse:theme";
}
