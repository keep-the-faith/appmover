using System.Text;

namespace AppMover.Cli;

/// <summary>Package passwords: APPMOVER_PASSWORD (automation) or typed without echo. Never taken from the command line.</summary>
internal static class Passwords
{
    private const string Variable = "APPMOVER_PASSWORD";

    /// <summary>Password of an existing package, or null when none was given.</summary>
    public static string? Read(string prompt)
    {
        if (Environment.GetEnvironmentVariable(Variable) is { Length: > 0 } fromEnvironment)
            return fromEnvironment;
        var password = ReadHidden(prompt);
        return password.Length == 0 ? null : password;
    }

    /// <summary>New password, typed twice. Returns null (after printing why) when it is empty or the two differ.</summary>
    public static string? ReadNew()
    {
        if (Environment.GetEnvironmentVariable(Variable) is { Length: > 0 } fromEnvironment)
            return fromEnvironment;
        var password = ReadHidden("Package password: ");
        if (password.Length < 8)
        {
            Console.Error.WriteLine("The password must have at least 8 characters.");
            return null;
        }
        if (ReadHidden("Confirm the password: ") != password)
        {
            Console.Error.WriteLine("The passwords do not match.");
            return null;
        }
        return password;
    }

    private static string ReadHidden(string prompt)
    {
        Console.Write(prompt);
        if (Console.IsInputRedirected)
            return Console.ReadLine() ?? "";

        var text = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
                break;
            if (key.Key == ConsoleKey.Backspace)
            {
                if (text.Length > 0)
                    text.Length--;
            }
            else if (!char.IsControl(key.KeyChar))
            {
                text.Append(key.KeyChar);
            }
        }
        Console.WriteLine();
        return text.ToString();
    }
}
