using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace StreamlinkVlcStudio.Core.Parsing;

public static class CommandLineTokenizer
{
    private const string SyntheticExecutableName = "StreamlinkVlcStudio.exe";

    public static IReadOnlyList<string> Tokenize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        if (value.Contains('\0'))
        {
            throw new ArgumentException("Custom arguments cannot contain a null character.", nameof(value));
        }

        return OperatingSystem.IsWindows()
            ? TokenizeWithWindowsRules(value)
            : TokenizePortable(value);
    }

    private static string[] TokenizeWithWindowsRules(string value)
    {
        var argumentsPointer = CommandLineToArgvW($"{SyntheticExecutableName} {value}", out var argumentCount);
        if (argumentsPointer == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "The custom Streamlink arguments could not be parsed.");
        }

        try
        {
            if (argumentCount <= 1)
            {
                return [];
            }

            var result = new string[argumentCount - 1];
            for (var index = 1; index < argumentCount; index++)
            {
                var valuePointer = Marshal.ReadIntPtr(argumentsPointer, index * IntPtr.Size);
                result[index - 1] = Marshal.PtrToStringUni(valuePointer) ?? "";
            }

            return result;
        }
        finally
        {
            _ = LocalFree(argumentsPointer);
        }
    }

    // Mirrors the documented Microsoft C runtime rules on non-Windows hosts so Core remains
    // testable and custom argument behavior does not change across build environments.
    private static List<string> TokenizePortable(string value)
    {
        var result = new List<string>();
        var index = 0;
        while (true)
        {
            while (index < value.Length && char.IsWhiteSpace(value[index]))
            {
                index++;
            }

            if (index >= value.Length)
            {
                return result;
            }

            var current = new StringBuilder();
            var inQuotes = false;
            while (index < value.Length)
            {
                if (char.IsWhiteSpace(value[index]) && !inQuotes)
                {
                    break;
                }

                var slashCount = 0;
                while (index < value.Length && value[index] == '\\')
                {
                    slashCount++;
                    index++;
                }

                if (index < value.Length && value[index] == '"')
                {
                    current.Append('\\', slashCount / 2);
                    if ((slashCount & 1) != 0)
                    {
                        current.Append('"');
                        index++;
                        continue;
                    }

                    if (inQuotes && index + 1 < value.Length && value[index + 1] == '"')
                    {
                        current.Append('"');
                        index += 2;
                        continue;
                    }

                    inQuotes = !inQuotes;
                    index++;
                    continue;
                }

                current.Append('\\', slashCount);
                if (index >= value.Length || (char.IsWhiteSpace(value[index]) && !inQuotes))
                {
                    break;
                }

                current.Append(value[index++]);
            }

            result.Add(current.ToString());
        }
    }

    [DllImport("shell32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int argumentCount);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
