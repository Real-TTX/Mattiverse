using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Mattiverse.Core.Catalog;
using Mattiverse.Core.Configuration;
using Mattiverse.Core.Engines;
using Mattiverse.Core.Entities;
using Mattiverse.Core.Integrations;
using Microsoft.Extensions.Logging.Abstractions;

namespace Mattiverse.Tests;

/// <summary>
/// The engines and the mail through MatSMTP (MatDesign docs/PLATFORM.md, "Mail through MatSMTP"): the five engines and their order, <c>mail: {}</c> in the catalog
/// and the variables it brings, the entry of MatSMTP, the apps that send mail, <see cref="MailLink"/> against a fake MatSMTP and <see cref="MatSmtpClient"/> against a stub.
/// </summary>
public class MailTests
{
    private const string Minimal = """
        x-mattiverse:
          id: demo
          name: Demo
          mail: {}
        services:
          web:
            image: nginx
        """;

    private static readonly string[] MailApps = ["matdock", "matbu", "matmon", "matcms"];

    private static CatalogService CreateCatalog()
        => new(new AppPaths(Path.Combine(Path.GetTempPath(), "mattiverse-tests-" + Guid.NewGuid().ToString("N"))), NullLogger<CatalogService>.Instance);

    // ------------------------------------------------------------------ the engines

    [Fact]
    public void The_five_engines_are_installed_MatDock_first_and_MatSMTP_after_MatIdentity()
    {
        Assert.Equal(["matdock", "matcad", "matddns", "matidentity", "matsmtp"], EngineIds.All);
        Assert.True(EngineIds.IsEngine("matsmtp"));
        Assert.True(EngineIds.IsEngine("matidentity"));
        Assert.False(EngineIds.IsEngine("matsplit"));
        Assert.False(EngineIds.IsEngine(null));
        Assert.Equal(EngineIds.MatIdentity, MatIdentityClient.CatalogIdValue);
        Assert.Equal(EngineIds.MatSmtp, MatSmtpClient.CatalogIdValue);
    }

    [Fact]
    public void Every_engine_is_a_core_entry_of_the_catalog_with_a_provisioner_and_an_icon()
    {
        var catalog = CreateCatalog();
        foreach (var id in EngineIds.All)
        {
            var app = catalog.Get(id);
            Assert.NotNull(app);
            Assert.True(app.Core, $"{id}: core");
            Assert.Equal(id, app.Provision);
            Assert.Equal(id + ".svg", app.Icon);
        }

        // Nothing else is an engine.
        Assert.All(catalog.GetAll().Where(a => a.Core), a => Assert.True(EngineIds.IsEngine(a.Id), a.Id));
    }

    [Fact]
    public void MatSMTP_is_in_the_catalog_as_the_mail_relay()
    {
        var smtp = CreateCatalog().Get(MatSmtpClient.CatalogIdValue);
        Assert.NotNull(smtp);
        Assert.Equal(AppRoles.Mail, smtp.Role);
        Assert.Equal("Infrastructure", smtp.Category);
        Assert.True(smtp.Platform);
        Assert.NotNull(smtp.Identity);
        Assert.Null(smtp.Mail);
        Assert.Equal("matsmtp", smtp.Ui!.Service);
        Assert.Equal(2525, smtp.Ui.Port);
        Assert.Equal(2525, smtp.Ui.HostPort);

        var key = Assert.Single(smtp.Variables, v => v.Key == MatSmtpClient.KeyVariableName);
        Assert.True(key.Hidden && key.Generate && key.IsSecret);
        var admin = Assert.Single(smtp.Variables, v => v.Key == "ADMIN_PASSWORD");
        Assert.True(admin.Generate && admin.IsSecret);
        Assert.Contains(smtp.Variables, v => v.Key == ManagedVariables.ApiKey && v.Hidden && v.Generate);

        Assert.Contains("MATSMTP_API_KEY: ${MATSMTP_API_KEY}", smtp.ComposeYaml);
        Assert.Contains("MATSMTP_ADMIN_PASSWORD: ${ADMIN_PASSWORD}", smtp.ComposeYaml);
        Assert.Contains("- matsmtp-data:/data", smtp.ComposeYaml);

        // The apps hand in their mail on the shared network: the SMTP port of the container (2025) is never published, only the submission port for devices.
        var published = Assert.Single(smtp.FixedPorts);
        Assert.Equal(2587, published.HostPort);
        Assert.DoesNotContain(smtp.FixedPorts, p => p.HostPort is 25 or 2025);
    }

    [Fact]
    public void MatIdentity_is_an_engine_now()
    {
        var identity = CreateCatalog().Get(MatIdentityClient.CatalogIdValue);
        Assert.NotNull(identity);
        Assert.True(identity.Core);
    }

    // ------------------------------------------------------------------ mail: {} in the catalog

    [Fact]
    public void An_app_that_sends_mail_gets_the_six_variables_hidden_and_none_generated()
    {
        var app = CatalogParser.Parse(Minimal, "test");

        Assert.NotNull(app.Mail);
        Assert.Null(app.Mail.Sender);
        foreach (var key in ManagedVariables.Mail)
        {
            var variable = Assert.Single(app.Variables, v => v.Key == key);
            Assert.True(variable.Hidden, key);
            Assert.False(variable.Generate, key);
            Assert.Equal(key == ManagedVariables.MailPassword, variable.IsSecret);
        }

        Assert.Equal(["MATSMTP_HOST", "MATSMTP_PORT", "MATSMTP_USERNAME", "MATSMTP_PASSWORD", "MATSMTP_FROM", "MATSMTP_TLS"], ManagedVariables.Mail);
    }

    [Fact]
    public void Mail_needs_no_web_service_and_no_platform()
    {
        var app = CatalogParser.Parse(Minimal, "test");
        Assert.Null(app.Ui);
        Assert.DoesNotContain(app.Variables, v => v.Key == ManagedVariables.ApiKey);
    }

    [Fact]
    public void An_app_without_mail_gets_none_of_them()
        => Assert.DoesNotContain(CatalogParser.Parse(Minimal.Replace("  mail: {}\n", string.Empty), "test").Variables, v => v.Key.StartsWith("MATSMTP_", StringComparison.Ordinal));

    [Fact]
    public void The_sender_can_be_named_and_must_be_the_part_before_the_at()
    {
        Assert.Equal("noreply", CatalogParser.Parse(Minimal.Replace("mail: {}", "mail:\n    sender: noreply"), "test").Mail!.Sender);
        Assert.Throws<FormatException>(() => CatalogParser.Parse(Minimal.Replace("mail: {}", "mail:\n    sender: a@b.com"), "test"));
        Assert.Throws<FormatException>(() => CatalogParser.Parse(Minimal.Replace("mail: {}", "mail:\n    sender: \"Bad Name\""), "test"));
    }

    [Fact]
    public void An_entry_that_declares_a_mail_variable_itself_keeps_its_own()
    {
        var app = CatalogParser.Parse(Minimal.Replace("  mail: {}\n", "  mail: {}\n  variables:\n    - key: MATSMTP_FROM\n      label: Sender\n      default: shop@example.com\n"), "test");
        var from = Assert.Single(app.Variables, v => v.Key == ManagedVariables.MailFrom);
        Assert.False(from.Hidden);
        Assert.Equal("shop@example.com", from.Default);
    }

    [Theory]
    [InlineData("matdock")]
    [InlineData("matbu")]
    [InlineData("matmon")]
    [InlineData("matcms")]
    public void The_app_sends_its_mail_through_MatSMTP(string id)
    {
        var app = CreateCatalog().Get(id);
        Assert.NotNull(app);
        Assert.NotNull(app.Mail);
        foreach (var key in ManagedVariables.Mail)
        {
            Assert.Contains($"{key}: ${{{key}:-}}", app.ComposeYaml);
        }
    }

    [Fact]
    public void Only_the_apps_that_send_mail_say_so()
        => Assert.Equal(MailApps.Order(), CreateCatalog().GetAll().Where(a => a.Mail is not null).Select(a => a.Id).Order());

    // ------------------------------------------------------------------ MailLink against a fake MatSMTP

    private sealed class FakeSmtp : MatSmtpClient
    {
        public FakeSmtp()
            : base(null!, null!)
        {
        }

        public List<(string Id, string DisplayName, IReadOnlyList<string> Senders, bool Renew)> Upserts { get; } = new();

        public List<string> Deleted { get; } = new();

        public bool Exists { get; set; }

        public int Issued { get; private set; }

        public override Task<RelayUser> UpsertRelayUserAsync(string id, string displayName, IReadOnlyList<string> allowedSenders, bool renewSecret, CancellationToken ct)
        {
            Upserts.Add((id, displayName, allowedSenders, renewSecret));
            var created = !Exists;
            Exists = true;
            string? password = null;
            if (created || renewSecret)
            {
                Issued++;
                password = "pw-" + Issued;
            }

            return Task.FromResult(new RelayUser(id, "relay-" + id, password, created));
        }

        public override Task DeleteRelayUserAsync(string id, CancellationToken ct)
        {
            Deleted.Add(id);
            return Task.CompletedTask;
        }
    }

    private static readonly MatSmtpStatus Answering = new("1.0.0", 1, 2025, "none");

    private static MailRelay Relay(MatSmtpStatus? status = null, string domain = "example.com")
        => new("mattiverse-matsmtp-matsmtp", status ?? Answering, domain);

    private static CatalogApp SendsMail(string? sender = null) => new() { Id = "matbu", Name = "MatBu", Mail = new CatalogMail { Sender = sender } };

    private static InstalledApp App(string slug = "matbu") => new() { Slug = slug, Name = "MatBu", CatalogId = "matbu" };

    private static Dictionary<string, string> Empty() => ManagedVariables.Mail.ToDictionary(k => k, _ => string.Empty);

    [Fact]
    public async Task With_MatSMTP_the_app_gets_a_relay_user_and_all_six_variables()
    {
        var smtp = new FakeSmtp();
        var variables = Empty();
        var log = new List<string>();

        await MailLink.ApplyAsync(App(), SendsMail(), variables, Relay(new MatSmtpStatus("1.0.0", 1, 2025, "starttls")), smtp, log.Add, default);

        var upsert = Assert.Single(smtp.Upserts);
        Assert.Equal("matbu", upsert.Id);
        Assert.Equal("MatBu", upsert.DisplayName);
        Assert.Equal(["matbu@example.com"], upsert.Senders);
        Assert.True(upsert.Renew);

        Assert.Equal("mattiverse-matsmtp-matsmtp", variables[ManagedVariables.MailHost]);
        Assert.Equal("2025", variables[ManagedVariables.MailPort]);
        Assert.Equal("relay-matbu", variables[ManagedVariables.MailUsername]);
        Assert.Equal("pw-1", variables[ManagedVariables.MailPassword]);
        Assert.Equal("matbu@example.com", variables[ManagedVariables.MailFrom]);
        Assert.Equal("starttls", variables[ManagedVariables.MailTls]);
        Assert.Contains(log, l => l.Contains("matbu@example.com", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_password_is_kept_and_renewed_only_when_it_is_missing()
    {
        var smtp = new FakeSmtp();
        var variables = Empty();
        await MailLink.ApplyAsync(App(), SendsMail(), variables, Relay(), smtp, _ => { }, default);

        // An update: the user exists, the app has its password, nothing is renewed.
        await MailLink.ApplyAsync(App(), SendsMail(), variables, Relay(), smtp, _ => { }, default);
        Assert.False(smtp.Upserts[1].Renew);
        Assert.Equal("pw-1", variables[ManagedVariables.MailPassword]);

        // The password got lost: it is renewed.
        variables[ManagedVariables.MailPassword] = string.Empty;
        await MailLink.ApplyAsync(App(), SendsMail(), variables, Relay(), smtp, _ => { }, default);
        Assert.True(smtp.Upserts[2].Renew);
        Assert.Equal("pw-2", variables[ManagedVariables.MailPassword]);
    }

    [Fact]
    public async Task A_MatSMTP_that_lost_the_user_hands_out_a_new_password_and_it_is_taken()
    {
        var smtp = new FakeSmtp();
        var variables = Empty();
        await MailLink.ApplyAsync(App(), SendsMail(), variables, Relay(), smtp, _ => { }, default);

        smtp.Exists = false;
        await MailLink.ApplyAsync(App(), SendsMail(), variables, Relay(), smtp, _ => { }, default);
        Assert.False(smtp.Upserts[1].Renew);
        Assert.Equal("pw-2", variables[ManagedVariables.MailPassword]);
    }

    [Fact]
    public async Task Without_MatSMTP_the_variables_are_cleared_and_nobody_is_asked()
    {
        var smtp = new FakeSmtp();
        var variables = Empty();
        await MailLink.ApplyAsync(App(), SendsMail(), variables, Relay(), smtp, _ => { }, default);

        var log = new List<string>();
        await MailLink.ApplyAsync(App(), SendsMail(), variables, relay: null, smtp, log.Add, default);

        Assert.All(ManagedVariables.Mail, key => Assert.Equal(string.Empty, variables[key]));
        Assert.Single(smtp.Upserts);
        Assert.Contains(log, l => l.Contains("sends its mail itself", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_MatSMTP_that_does_not_answer_leaves_the_variables_as_they_are()
    {
        var smtp = new FakeSmtp();
        var variables = Empty();
        await MailLink.ApplyAsync(App(), SendsMail(), variables, Relay(), smtp, _ => { }, default);
        var before = new Dictionary<string, string>(variables);

        await MailLink.ApplyAsync(App(), SendsMail(), variables, Relay(new MatSmtpStatus(string.Empty, 0, 2025, "none")), smtp, _ => { }, default);
        await MailLink.ApplyAsync(App(), SendsMail(), variables, new MailRelay("host", null, "example.com"), smtp, _ => { }, default);

        Assert.Equal(before, variables);
        Assert.Single(smtp.Upserts);
    }

    [Fact]
    public async Task An_app_without_mail_is_left_alone()
    {
        var smtp = new FakeSmtp();
        var variables = new Dictionary<string, string> { ["OTHER"] = "x" };
        await MailLink.ApplyAsync(App(), new CatalogApp { Id = "matsplit", Name = "MatSplit" }, variables, Relay(), smtp, _ => { }, default);
        await MailLink.ApplyAsync(App(), new CatalogApp { Id = "matsplit", Name = "MatSplit" }, variables, relay: null, smtp, _ => { }, default);

        Assert.Empty(smtp.Upserts);
        Assert.Equal(new Dictionary<string, string> { ["OTHER"] = "x" }, variables);
    }

    [Fact]
    public async Task The_sender_follows_the_catalog_and_the_slug_of_the_app()
    {
        var smtp = new FakeSmtp();
        var variables = Empty();
        await MailLink.ApplyAsync(App("backup-2"), SendsMail(), variables, Relay(domain: "home.example"), smtp, _ => { }, default);
        Assert.Equal("backup-2@home.example", variables[ManagedVariables.MailFrom]);
        Assert.Equal("backup-2", smtp.Upserts[0].Id);

        await MailLink.ApplyAsync(App(), SendsMail("noreply"), variables, Relay(), smtp, _ => { }, default);
        Assert.Equal("noreply@example.com", variables[ManagedVariables.MailFrom]);
        Assert.Equal(["noreply@example.com"], smtp.Upserts[1].Senders);
    }

    // ------------------------------------------------------------------ MatSmtpClient against a stub

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, (HttpStatusCode Status, string Json)> _answers = new();

        public List<(HttpMethod Method, string Path, string Query, string? Key, JsonNode? Body)> Requests { get; } = new();

        public StubHandler Answer(string method, string path, string json, HttpStatusCode status = HttpStatusCode.OK)
        {
            _answers[$"{method} {path}"] = (status, json);
            return this;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath.Replace("/api/v1/", string.Empty);
            var text = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.Method, path, request.RequestUri.Query, request.Headers.TryGetValues("X-Api-Key", out var keys) ? keys.Single() : null,
                string.IsNullOrEmpty(text) ? null : JsonNode.Parse(text)));
            var (status, json) = _answers.TryGetValue($"{request.Method} {path}", out var answer) ? answer : (HttpStatusCode.OK, "{}");
            return new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class StubLocator : EngineLocator
    {
        public StubLocator()
            : base(null!, null!, null!, null!, null!)
        {
        }

        public string? AskedFor { get; private set; }

        public override Task<EngineEndpoint?> FindAsync(string catalogId, string keyVariable, CancellationToken ct = default)
        {
            AskedFor = $"{catalogId} {keyVariable}";
            return Task.FromResult<EngineEndpoint?>(new EngineEndpoint(null!, "http://matsmtp.test", "the-key"));
        }
    }

    [Fact]
    public async Task The_client_reads_the_status_with_the_key_of_MatSMTP()
    {
        var handler = new StubHandler().Answer("GET", "status", """{"app":"matsmtp","version":"1.4.0","apiVersion":1,"smtpPort":2025,"smtpTls":"STARTTLS"}""");
        var locator = new StubLocator();
        var status = await new MatSmtpClient(locator, new StubFactory(handler)).GetSmtpStatusAsync();

        Assert.Equal(new MatSmtpStatus("1.4.0", 1, 2025, "starttls"), status);
        Assert.Equal("matsmtp MATSMTP_API_KEY", locator.AskedFor);
        Assert.Equal("the-key", handler.Requests[0].Key);
    }

    [Theory]
    [InlineData("""{"version":"1","apiVersion":1}""", 2025, "none")]
    [InlineData("""{"version":"1","apiVersion":1,"smtpPort":0,"smtpTls":"ssl"}""", 2025, "none")]
    [InlineData("""{"version":"1","apiVersion":1,"smtpPort":2465,"smtpTls":"tls"}""", 2465, "tls")]
    public void A_status_without_a_port_or_with_an_unknown_tls_mode_has_the_defaults(string json, int port, string tls)
    {
        var status = MatSmtpClient.ParseStatus(JsonNode.Parse(json));
        Assert.Equal(port, status!.SmtpPort);
        Assert.Equal(tls, status.SmtpTls);
    }

    [Fact]
    public async Task A_MatSMTP_that_does_not_answer_has_no_status()
    {
        var handler = new StubHandler().Answer("GET", "status", """{"error":"The API is off."}""", HttpStatusCode.ServiceUnavailable);
        Assert.Null(await new MatSmtpClient(new StubLocator(), new StubFactory(handler)).GetSmtpStatusAsync());
    }

    [Fact]
    public async Task The_relay_user_is_put_with_the_marker_and_the_password_is_read()
    {
        var handler = new StubHandler().Answer("PUT", "relay-users/matbu", """{"id":"matbu","username":"matbu","password":"s3cret","created":true}""");
        var user = await new MatSmtpClient(new StubLocator(), new StubFactory(handler)).UpsertRelayUserAsync("matbu", "MatBu", ["matbu@example.com"], renewSecret: false, default);

        Assert.Equal(new RelayUser("matbu", "matbu", "s3cret", true), user);
        var (method, _, _, _, body) = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Put, method);
        Assert.Equal("MatBu", body!["displayName"]!.GetValue<string>());
        Assert.Equal("matbu@example.com", body["allowedSenders"]![0]!.GetValue<string>());
        Assert.Equal("mattiverse", body["managedBy"]!.GetValue<string>());
        Assert.False(body["renewSecret"]!.GetValue<bool>());
    }

    [Fact]
    public async Task An_existing_user_comes_back_without_a_password()
    {
        var handler = new StubHandler().Answer("PUT", "relay-users/matbu", """{"id":"matbu","username":"matbu","created":false}""");
        var user = await new MatSmtpClient(new StubLocator(), new StubFactory(handler)).UpsertRelayUserAsync("matbu", "MatBu", ["matbu@example.com"], renewSecret: false, default);
        Assert.Null(user.Password);
        Assert.False(user.Created);
    }

    [Fact]
    public async Task Deleting_names_the_marker_and_a_missing_user_is_no_error()
    {
        var handler = new StubHandler().Answer("DELETE", "relay-users/gone", """{"error":"Not found."}""", HttpStatusCode.NotFound);
        var client = new MatSmtpClient(new StubLocator(), new StubFactory(handler));

        await client.DeleteRelayUserAsync("gone", default);
        Assert.Equal("?managedBy=mattiverse", handler.Requests[0].Query);
    }

    [Fact]
    public async Task A_user_Mattiverse_did_not_make_is_not_deleted()
    {
        var handler = new StubHandler().Answer("DELETE", "relay-users/shop", """{"error":"Not managed by mattiverse."}""", HttpStatusCode.Conflict);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new MatSmtpClient(new StubLocator(), new StubFactory(handler)).DeleteRelayUserAsync("shop", default));
        Assert.Contains("not made by Mattiverse", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_settings_are_read_and_written()
    {
        var handler = new StubHandler()
            .Answer("GET", "settings", """{"setupCompleted":false,"defaultSenderDomain":""}""")
            .Answer("PUT", "settings", """{"setupCompleted":true,"defaultSenderDomain":"example.com"}""");
        var client = new MatSmtpClient(new StubLocator(), new StubFactory(handler));

        Assert.Equal(new MatSmtpRelaySettings(false, null), await client.GetSettingsAsync(default));
        Assert.Equal(new MatSmtpRelaySettings(true, "example.com"), await client.UpdateSettingsAsync(new MatSmtpRelaySettings(true, "example.com"), default));
        var body = handler.Requests[1].Body!;
        Assert.True(body["setupCompleted"]!.GetValue<bool>());
        Assert.Equal("example.com", body["defaultSenderDomain"]!.GetValue<string>());
    }

    [Fact]
    public async Task The_relay_users_of_Mattiverse_are_listed()
    {
        var handler = new StubHandler().Answer("GET", "relay-users", """[{"id":"matbu","managedBy":"mattiverse"},{"id":"printer"},{"id":"matdock","managedBy":"mattiverse"}]""");
        Assert.Equal(["matbu", "matdock"], await new MatSmtpClient(new StubLocator(), new StubFactory(handler)).ListRelayUsersAsync(default));
    }
}
