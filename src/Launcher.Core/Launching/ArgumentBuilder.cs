using System.Text.RegularExpressions;
using Launcher.Core.Auth;
using Launcher.Core.Common;
using Launcher.Core.Versions.Libraries;
using Launcher.Core.Versions.Mojang;
using Launcher.Core.Versions.Rules;

namespace Launcher.Core.Launching;

public sealed record LaunchPlan(
    string JavaExecutable,
    IReadOnlyList<string> JvmArgs,
    string MainClass,
    IReadOnlyList<string> GameArgs,
    string Classpath,
    string GameDirectory);

public sealed class ArgumentBuilder
{
    public LaunchPlan Build(
        VersionJson version,
        IAccount account,
        string javaExecutable,
        string gameDirectory,
        string? loggingConfigPath,
        LaunchFeatures features = LaunchFeatures.None,
        LauncherSettings? settings = null)
    {
        settings ??= new LauncherSettings();
        Directory.CreateDirectory(gameDirectory);

        var ctx = RuleContext.Detect(features);
        // For merged overlays (Fabric), the client jar + natives live under the BASE version,
        // not under the merged id.
        var baseVersionId = version.InheritsFrom ?? version.Id;

        var libraryPaths = LibraryPlanner.ClasspathPaths(version.Libraries, ctx);
        var classpathEntries = libraryPaths.Append(Paths.VersionJar(baseVersionId));
        var classpath = string.Join(Path.PathSeparator, classpathEntries);

        var nativesDir = Path.Combine(Paths.VersionDir(baseVersionId), "natives");
        Directory.CreateDirectory(nativesDir);

        var subs = new Dictionary<string, string>
        {
            ["auth_player_name"]    = account.Username,
            ["version_name"]        = version.Id,
            ["game_directory"]      = gameDirectory,
            ["assets_root"]         = Paths.Assets,
            ["assets_index_name"]   = version.Assets,
            ["auth_uuid"]           = account.Uuid.ToString("N"),
            ["auth_access_token"]   = account.AccessToken,
            ["user_type"]           = account.UserType,
            ["version_type"]        = version.Type,
            ["launcher_name"]       = "MashiroLauncher",
            ["launcher_version"]    = "0.1",
            ["classpath"]           = classpath,
            ["user_properties"]     = "{}",
            ["clientid"]            = account.ClientId,
            ["auth_xuid"]           = account.Xuid,
            ["natives_directory"]   = nativesDir,
            ["library_directory"]   = Paths.Libraries,
            ["classpath_separator"] = Path.PathSeparator.ToString(),
        };

        // Memory + custom user-supplied JVM args go FIRST so version-supplied flags
        // (which may include their own GC tuning) can still override if needed.
        var jvm = new List<string>
        {
            $"-Xms{settings.MinMemoryMb}M",
            $"-Xmx{settings.MaxMemoryMb}M",
        };
        if (!string.IsNullOrWhiteSpace(settings.CustomJvmArgs))
        {
            foreach (var part in settings.CustomJvmArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                jvm.Add(part);
        }
        jvm.AddRange(FlattenArgs(version.Arguments.Jvm, ctx).Select(a => Substitute(a, subs)));
        if (version.Logging is { Client: var client } && loggingConfigPath is not null)
            jvm.Add(client.Argument.Replace("${path}", loggingConfigPath));

        var game = FlattenArgs(version.Arguments.Game, ctx)
            .Select(a => Substitute(a, subs))
            .ToList();

        return new LaunchPlan(
            javaExecutable,
            jvm,
            version.MainClass,
            game,
            classpath,
            gameDirectory);
    }

    private static IEnumerable<string> FlattenArgs(IReadOnlyList<ArgumentToken> tokens, RuleContext ctx)
    {
        foreach (var t in tokens)
        {
            switch (t)
            {
                case StringArgument s:
                    yield return s.Value;
                    break;
                case ConditionalArgument c when RuleEvaluator.Evaluate(c.Rules, ctx):
                    foreach (var v in c.Values) yield return v;
                    break;
            }
        }
    }

    private static readonly Regex TokenPattern =
        new(@"\$\{([^}]+)\}", RegexOptions.Compiled);

    public static string Substitute(string input, IReadOnlyDictionary<string, string> subs) =>
        TokenPattern.Replace(input, m =>
            subs.TryGetValue(m.Groups[1].Value, out var value) ? value : m.Value);
}
