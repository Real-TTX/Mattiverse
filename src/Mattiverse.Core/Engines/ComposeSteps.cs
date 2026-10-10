using Mattiverse.Core.Apps;

namespace Mattiverse.Core.Engines;

/// <summary>Runs one <c>docker compose</c> step and turns a failure into a message a person can act on.</summary>
public static class ComposeSteps
{
    public static async Task RunAsync(ComposeRunner compose, string slug, IEnumerable<string> subcommand, string failureMessage, Action<string> log, CancellationToken ct)
    {
        var args = subcommand.ToList();
        var tail = new Queue<string>();
        var exit = await compose.StreamAsync(slug, args, line =>
        {
            log(line);
            tail.Enqueue(line);
            if (tail.Count > 6)
            {
                tail.Dequeue();
            }
        }, ct);

        if (exit != 0)
        {
            // The last distinct lines say what Docker complained about; the hint (if there is one) comes first so it is never cut off.
            var lines = tail.Select(l => l.Trim()).Where(l => l.Length > 0).Distinct().TakeLast(3).ToList();
            var detail = string.Join(" | ", lines);
            throw new InvalidOperationException($"{failureMessage}.{Hint(detail)} Docker said (docker compose {args[0]}, exit code {exit}): {detail}");
        }
    }

    /// <summary>Explains the Docker errors that users cannot be expected to know how to fix.</summary>
    public static string Hint(string dockerOutput)
    {
        if (dockerOutput.Contains("address pools have been fully subnetted", StringComparison.OrdinalIgnoreCase))
        {
            return " Hint: Docker has run out of network addresses. Remove networks you do not use ('docker network prune') or enlarge 'default-address-pools' in the Docker daemon settings.";
        }

        if (dockerOutput.Contains("port is already allocated", StringComparison.OrdinalIgnoreCase) ||
            dockerOutput.Contains("address already in use", StringComparison.OrdinalIgnoreCase))
        {
            return " Hint: a host port is already in use. Stop whatever uses it, or change the port in the install form (Advanced).";
        }

        if (dockerOutput.Contains("pull access denied", StringComparison.OrdinalIgnoreCase) ||
            dockerOutput.Contains("unauthorized", StringComparison.OrdinalIgnoreCase))
        {
            return " Hint: the image is private or does not exist. For a private image run 'docker login' on the host.";
        }

        return string.Empty;
    }
}
