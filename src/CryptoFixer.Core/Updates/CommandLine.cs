using System.Text;

namespace CryptoFixer.Core.Updates;

public static class CommandLine
{
    /// <summary>Joins arguments into a Windows command line that parses back into the same arguments (CommandLineToArgvW rules).</summary>
    public static string Join(IEnumerable<string> arguments) => string.Join(' ', arguments.Select(Quote));

    public static string Quote(string argument)
    {
        ArgumentNullException.ThrowIfNull(argument);

        if (argument.Length > 0 && !argument.Any(c => char.IsWhiteSpace(c) || c == '"'))
        {
            return argument;
        }

        var result = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var c in argument)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            // Backslashes are literal unless they precede a quote; then each one must be escaped, as must the quote.
            result.Append('\\', c == '"' ? backslashes * 2 + 1 : backslashes);
            result.Append(c);
            backslashes = 0;
        }

        result.Append('\\', backslashes * 2);
        return result.Append('"').ToString();
    }
}
