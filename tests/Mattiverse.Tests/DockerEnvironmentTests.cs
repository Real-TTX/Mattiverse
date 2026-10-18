using Mattiverse.Core.Docker;

namespace Mattiverse.Tests;

/// <summary>
/// The docker process of Mattiverse sees only what the CLI needs. `docker compose` prefers a variable of its process over the `.env` of a stack:
/// a key or a password in the environment of Mattiverse would otherwise end up in every app it deploys (MatDock had exactly that).
/// </summary>
public class DockerEnvironmentTests
{
    [Fact]
    public void Only_what_the_docker_cli_needs_reaches_the_process()
    {
        var env = new Dictionary<string, string?>
        {
            ["PATH"] = "/usr/bin",
            ["HOME"] = "/home/app",
            ["DOCKER_HOST"] = "unix:///var/run/docker.sock",
            ["USERPROFILE"] = @"C:\Users\x",
            ["MAT_API_KEY"] = "secret",
            ["MATIDENTITY_CLIENT_SECRET"] = "secret",
            ["Mattiverse__DataPath"] = "/data",
            ["ADMIN_PASSWORD"] = "secret",
        };

        DockerCli.KeepOnlySafeVariables(env);

        Assert.Equal(["DOCKER_HOST", "HOME", "PATH", "USERPROFILE"], env.Keys.Order(StringComparer.Ordinal));
    }
}
