namespace ShyFtp.Cli;

public static class ConsoleUtil
{
    public static void WriteLine(string text = "") => Console.WriteLine(text);

    public static void Info(string text) => WriteColor(text, ConsoleColor.Gray);

    public static void Success(string text) => WriteColor(text, ConsoleColor.Green);

    public static void Warn(string text) => WriteColor(text, ConsoleColor.Yellow);

    public static void Error(string text) => WriteColor(text, ConsoleColor.Red);

    public static void Accent(string text) => WriteColor(text, ConsoleColor.Cyan);

    public static void Muted(string text) => WriteColor(text, ConsoleColor.DarkGray);

    private static void WriteColor(string text, ConsoleColor color)
    {
        var previous = Console.ForegroundColor;
        try
        {
            Console.ForegroundColor = color;
        }
        catch (IOException)
        {
        }

        Console.WriteLine(text);
        try
        {
            Console.ForegroundColor = previous;
        }
        catch (IOException)
        {
        }
    }

    public static bool Confirm(string prompt, bool defaultValue = false)
    {
        var suffix = defaultValue ? " [Y/n] " : " [y/N] ";
        Console.Write(prompt + suffix);
        var input = Console.ReadLine();
        if (string.IsNullOrWhiteSpace(input))
            return defaultValue;

        var t = input.Trim().ToLowerInvariant();
        return t is "y" or "yes";
    }
}