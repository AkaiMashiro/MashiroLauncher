namespace Launcher.Core.Common;

public static class Log
{
    private static readonly Lock _gate = new();

    public static void Info(string message) => Write("INFO", message, ConsoleColor.Cyan);
    public static void Warn(string message) => Write("WARN", message, ConsoleColor.Yellow);
    public static void Error(string message) => Write("ERR ", message, ConsoleColor.Red);
    public static void Step(string message) => Write("STEP", message, ConsoleColor.Green);
    public static void Detail(string message)
    {
        lock (_gate) Console.WriteLine($"       {message}");
    }

    private static void Write(string level, string message, ConsoleColor color)
    {
        lock (_gate)
        {
            var prev = Console.ForegroundColor;
            Console.ForegroundColor = color;
            Console.Write($"[{level}] ");
            Console.ForegroundColor = prev;
            Console.WriteLine(message);
        }
    }
}
