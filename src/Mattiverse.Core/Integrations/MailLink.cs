using Mattiverse.Core.Apps;
using Mattiverse.Core.Catalog;
using Mattiverse.Core.Configuration;
using Mattiverse.Core.Data;
using Mattiverse.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Mattiverse.Core.Integrations;

/// <summary>
/// Where MatSMTP is for an app that sends mail: its address on the shared network, what its status said (<c>null</c> when it is installed but does not answer)
/// and the domain of the senders.
/// </summary>
public sealed record MailRelay(string Host, MatSmtpStatus? Status, string SenderDomain);

/// <summary>
/// Sends the mail of the apps through MatSMTP, the mail relay (MatDesign docs/PLATFORM.md, "Mail through MatSMTP"). Mattiverse does all of it, nobody types an
/// SMTP server: when an app that sends mail (<see cref="CatalogApp.Mail"/>) is installed or updated and MatSMTP is installed, MatSMTP gets a relay user for it
/// (id = the slug, <c>managedBy: mattiverse</c>, the sender as its only allowed sender) and the app gets its <c>MATSMTP_*</c> variables; without MatSMTP they are
/// empty and the app sends its mail itself, as before.
/// </summary>
public sealed class MailLink
{
    /// <summary>The domain of the senders when neither MatSMTP nor the settings of Mattiverse name one.</summary>
    public const string FallbackDomain = "localhost";

    private readonly MattiverseDbContext _db;
    private readonly MatSmtpClient _smtp;
    private readonly SystemConfigService _config;
    private readonly ComposeFiles _files;

    public MailLink(MattiverseDbContext db, MatSmtpClient smtp, SystemConfigService config, ComposeFiles files)
    {
        _db = db;
        _smtp = smtp;
        _config = config;
        _files = files;
    }

    /// <summary>Fills the mail variables of an app with <c>mail: {}</c>, before its stack is deployed. <paramref name="variables"/> is changed in place.</summary>
    public async Task ApplyAsync(InstalledApp app, CatalogApp catalog, Dictionary<string, string> variables, Action<string> log, CancellationToken ct)
    {
        if (catalog.Mail is null)
        {
            return;
        }

        await ApplyAsync(app, catalog, variables, await FindRelayAsync(ct), _smtp, log, ct);
    }

    /// <summary>
    /// The decision itself, without Docker or a database: no MatSMTP = the variables are cleared; MatSMTP installed but not answering = they stay as they are
    /// (it may be restarting; an app that has them keeps sending to it); MatSMTP answering = the relay user is made or changed and the variables are filled.
    /// The password is kept and renewed only when the app has none (or MatSMTP made the user anew).
    /// </summary>
    public static async Task ApplyAsync(
        InstalledApp app,
        CatalogApp catalog,
        Dictionary<string, string> variables,
        MailRelay? relay,
        MatSmtpClient smtp,
        Action<string> log,
        CancellationToken ct)
    {
        if (catalog.Mail is null)
        {
            return;
        }

        if (relay is null)
        {
            if (!string.IsNullOrEmpty(variables.GetValueOrDefault(ManagedVariables.MailHost)))
            {
                log("MatSMTP is not there (any more): the app sends its mail itself again.");
            }

            Clear(variables);
            return;
        }

        if (relay.Status is not { ApiVersion: >= 1 } status)
        {
            log("Warning: MatSMTP does not answer: the mail settings of the app stay as they are.");
            return;
        }

        var from = SenderOf(app, catalog, relay.SenderDomain);
        var hasSecret = !string.IsNullOrEmpty(variables.GetValueOrDefault(ManagedVariables.MailPassword))
                        && !string.IsNullOrEmpty(variables.GetValueOrDefault(ManagedVariables.MailUsername));
        var user = await smtp.UpsertRelayUserAsync(RelayUserIdOf(app), app.Name, [from], renewSecret: !hasSecret, ct);

        variables[ManagedVariables.MailHost] = relay.Host;
        variables[ManagedVariables.MailPort] = status.SmtpPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        variables[ManagedVariables.MailTls] = status.SmtpTls;
        variables[ManagedVariables.MailUsername] = user.Username;
        variables[ManagedVariables.MailFrom] = from;
        if (user.Password is not null)
        {
            variables[ManagedVariables.MailPassword] = user.Password;
        }
        else if (!hasSecret)
        {
            log("Warning: MatSMTP did not hand out a password for the relay user; the app may not be able to send.");
        }

        log($"Sends its mail through MatSMTP as {from} (relay user \"{user.Username}\").");
    }

    /// <summary>Removes the relay user of an app that is being uninstalled (only what Mattiverse made).</summary>
    public async Task RemoveAsync(InstalledApp app, CatalogApp? catalog, Action<string> log, CancellationToken ct)
    {
        if (catalog?.Mail is null || await _smtp.GetSmtpStatusAsync(ct) is not { ApiVersion: >= 1 })
        {
            return;
        }

        await _smtp.DeleteRelayUserAsync(RelayUserIdOf(app), ct);
        log("Removed from MatSMTP.");
    }

    /// <summary>
    /// The headless setup of MatSMTP (after its install, and when the domain changes): the first-run setup is done and the senders get the domain of the settings
    /// (the one of the wizard). Without a domain MatSMTP keeps its own. Returns false when MatSMTP does not answer.
    /// </summary>
    public async Task<bool> ConfigureRelayAsync(Action<string> log, CancellationToken ct)
    {
        if (await _smtp.GetSmtpStatusAsync(ct) is not { ApiVersion: >= 1 })
        {
            return false;
        }

        var current = await _smtp.GetSettingsAsync(ct);
        var domain = (await _config.GetAsync(ct)).BaseDomain;
        var wanted = new MatSmtpRelaySettings(true, string.IsNullOrWhiteSpace(domain) ? current.DefaultSenderDomain : domain.Trim().ToLowerInvariant());
        if (wanted != current)
        {
            await _smtp.UpdateSettingsAsync(wanted, ct);
        }

        log(wanted.DefaultSenderDomain is { } senders
            ? $"MatSMTP is set up; the apps send as <app>@{senders}."
            : "MatSMTP is set up. Set a domain for the senders in the wizard or in MatSMTP.");
        return true;
    }

    /// <summary>Where MatSMTP is, or <c>null</c> when it is not installed.</summary>
    public async Task<MailRelay?> FindRelayAsync(CancellationToken ct)
    {
        var relayApp = await _db.InstalledApps.AsNoTracking()
            .FirstOrDefaultAsync(a => a.CatalogId == MatSmtpClient.CatalogIdValue && a.Lifecycle == AppLifecycle.Installed, ct);
        if (relayApp is null)
        {
            return null;
        }

        var status = await _smtp.GetSmtpStatusAsync(ct);
        string? domain = null;
        if (status is { ApiVersion: >= 1 })
        {
            try
            {
                domain = (await _smtp.GetSettingsAsync(ct)).DefaultSenderDomain;
            }
            catch (InvalidOperationException)
            {
                // The settings are not essential: the domain of Mattiverse's settings is used.
            }
        }

        if (string.IsNullOrWhiteSpace(domain))
        {
            domain = (await _config.GetAsync(ct)).BaseDomain;
        }

        // The apps reach MatSMTP on the shared network by the name of its container.
        return new MailRelay(_files.UiHost(relayApp), status, string.IsNullOrWhiteSpace(domain) ? FallbackDomain : domain.Trim().ToLowerInvariant());
    }

    /// <summary>The id of the relay user of an app in MatSMTP: its slug.</summary>
    public static string RelayUserIdOf(InstalledApp app) => app.Slug;

    /// <summary>The sender address of an app: <c>mail.sender</c> of its catalog entry or its slug, at the domain of the senders.</summary>
    public static string SenderOf(InstalledApp app, CatalogApp catalog, string domain)
        => $"{(string.IsNullOrWhiteSpace(catalog.Mail?.Sender) ? app.Slug : catalog.Mail.Sender.Trim())}@{domain}";

    private static void Clear(Dictionary<string, string> variables)
    {
        foreach (var key in ManagedVariables.Mail)
        {
            variables[key] = string.Empty;
        }
    }
}
