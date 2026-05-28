namespace MashiroLauncher.Core.Common;

public static class Paths
{
    public static string Root { get; } = ResolveRoot();

    public static string Data => Path.Combine(Root, "data");
    public static string Assets => Path.Combine(Data, "assets");
    public static string AssetIndexes => Path.Combine(Assets, "indexes");
    public static string AssetObjects => Path.Combine(Assets, "objects");
    public static string Libraries => Path.Combine(Data, "libraries");
    public static string Versions => Path.Combine(Data, "versions");
    public static string Runtimes => Path.Combine(Data, "runtimes");
    public static string Instances => Path.Combine(Data, "instances");
    public static string Logs => Path.Combine(Data, "logs");

    public static string VersionDir(string id) => Path.Combine(Versions, id);
    public static string VersionJson(string id) => Path.Combine(VersionDir(id), $"{id}.json");
    public static string VersionJar(string id) => Path.Combine(VersionDir(id), $"{id}.jar");

    public static string AssetIndexFile(string id) => Path.Combine(AssetIndexes, $"{id}.json");
    public static string AssetObject(string hash) => Path.Combine(AssetObjects, hash[..2], hash);

    public static string LibraryPath(string mavenRelativePath) =>
        Path.Combine(Libraries, mavenRelativePath.Replace('/', Path.DirectorySeparatorChar));

    public static string RuntimeDir(string component) => Path.Combine(Runtimes, component);

    public static string InstanceDir(string name) => Path.Combine(Instances, name);
    public static string InstanceGameDir(string name) => Path.Combine(InstanceDir(name), "game");

    public static void EnsureBaseDirectories()
    {
        foreach (var p in new[]
                 {
                     Data, Assets, AssetIndexes, AssetObjects,
                     Libraries, Versions, Runtimes, Instances, Logs
                 })
        {
            Directory.CreateDirectory(p);
        }
    }

    // Walk up from the running binary until we find the solution file, so that during dev
    // `data/` lives next to src/ and tests/. Falls back to BaseDirectory if not found.
    private static string ResolveRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "MashiroLauncher.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return AppContext.BaseDirectory;
    }
}
