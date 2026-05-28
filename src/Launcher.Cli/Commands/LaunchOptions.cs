using Launcher.Core.Auth;

namespace Launcher.Cli.Commands;

internal sealed record LaunchOptions(string VersionId, IAccount Account, string? InstanceName)
{
    public static LaunchOptions Parse(string[] args, string verbName)
    {
        string? versionId = null;
        string username = "Player";
        var offline = false;
        string? instance = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--offline":
                    offline = true;
                    break;
                case "--name" when i + 1 < args.Length:
                    username = args[++i];
                    break;
                case "--instance" when i + 1 < args.Length:
                    instance = args[++i];
                    break;
                default:
                    if (versionId is null) versionId = args[i];
                    else throw new ArgumentException($"Unexpected argument: {args[i]}");
                    break;
            }
        }

        if (versionId is null)
            throw new ArgumentException($"Usage: cli {verbName} <version> [--offline --name <name>] [--instance <name>]");
        if (!offline)
            throw new NotSupportedException("Microsoft authentication is Phase 2. Pass --offline for now.");

        return new LaunchOptions(versionId, new OfflineAccount(username), instance);
    }
}
