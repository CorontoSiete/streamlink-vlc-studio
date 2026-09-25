namespace StreamlinkVlcStudio.Maintenance;

internal sealed record CommandLineOptions(
    bool Quiet,
    bool PurgeUserData,
    bool PurgeUserDataOnly,
    bool Staged,
    string? InstallDirectory,
    int ParentProcessId,
    string? StageNonce,
    string? LogPath)
{
    internal static CommandLineOptions Parse(string[] args)
    {
        var quiet = false;
        var purgeUserData = true;
        var purgeUserDataOnly = false;
        var staged = false;
        string? installDirectory = null;
        string? stageNonce = null;
        string? logPath = null;
        var parentProcessId = 0;

        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (Matches(argument, "/q", "/quiet", "--quiet"))
            {
                quiet = true;
            }
            else if (Matches(argument, "/purge-user-data", "--purge-user-data"))
            {
                purgeUserData = true;
            }
            else if (Matches(argument, "/preserve-user-data", "--preserve-user-data"))
            {
                purgeUserData = false;
            }
            else if (Matches(argument, "--purge-user-data-only"))
            {
                purgeUserDataOnly = true;
            }
            else if (Matches(argument, "--staged"))
            {
                staged = true;
            }
            else if (Matches(argument, "--install-directory"))
            {
                installDirectory = ReadValue(args, ref index, argument);
            }
            else if (Matches(argument, "--parent-pid"))
            {
                var value = ReadValue(args, ref index, argument);
                if (!int.TryParse(value, out parentProcessId) || parentProcessId <= 0)
                {
                    throw new ArgumentException("--parent-pid requires a positive process identifier.");
                }
            }
            else if (Matches(argument, "--stage-nonce"))
            {
                stageNonce = ReadValue(args, ref index, argument);
            }
            else if (Matches(argument, "--log-path"))
            {
                logPath = ReadValue(args, ref index, argument);
            }
            else
            {
                throw new ArgumentException($"Unknown maintenance argument: {argument}");
            }
        }

        if (staged &&
            (string.IsNullOrWhiteSpace(installDirectory) ||
             string.IsNullOrWhiteSpace(stageNonce) ||
             string.IsNullOrWhiteSpace(logPath) ||
             parentProcessId <= 0))
        {
            throw new ArgumentException("The staged maintenance handshake is incomplete.");
        }

        if (purgeUserDataOnly && staged)
        {
            throw new ArgumentException("Data-only cleanup cannot use the staged uninstall mode.");
        }

        return new CommandLineOptions(
            quiet,
            purgeUserData,
            purgeUserDataOnly,
            staged,
            installDirectory,
            parentProcessId,
            stageNonce,
            logPath);
    }

    private static bool Matches(string value, params string[] expected)
    {
        return expected.Any(item => string.Equals(value, item, StringComparison.OrdinalIgnoreCase));
    }

    private static string ReadValue(string[] args, ref int index, string option)
    {
        if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
        {
            throw new ArgumentException($"{option} requires a value.");
        }

        return args[index];
    }
}
