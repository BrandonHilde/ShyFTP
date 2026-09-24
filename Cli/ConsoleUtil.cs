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

    public static string ReadSecret(string prompt)
    {
        Console.Write(prompt + ": ");
        if (Console.IsInputRedirected)
            return Console.ReadLine() ?? "";

        var buffer = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(true);
            if (key.Key == ConsoleKey.Enter)
                break;
            if (key.Key == ConsoleKey.Backspace)
            {
                if (buffer.Length > 0)
                    buffer.Length--;
                continue;
            }

            if (!char.IsControl(key.KeyChar))
                buffer.Append(key.KeyChar);
        }

        Console.WriteLine();
        return buffer.ToString();
    }
}