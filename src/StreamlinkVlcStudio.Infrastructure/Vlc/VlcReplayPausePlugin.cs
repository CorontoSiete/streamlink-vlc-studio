using System.Security.Cryptography;
using StreamlinkVlcStudio.Infrastructure.Io;

namespace StreamlinkVlcStudio.Infrastructure.Vlc;

internal static class VlcReplayPausePlugin
{
    private static readonly object Gate = new();
    private static string? preparedRoot;

    internal static string Prepare()
    {
        lock (Gate)
        {
            if (preparedRoot is not null)
            {
                return preparedRoot;
            }

            var plugins = new[]
            {
                ReadPlugin("BundledReplayPause", "libstudio_replay_pause_plugin.dll"),
                ReadPlugin("BundledAdaptiveReplay", "libstudio_adaptive_plugin.dll")
            };
            using var bundleHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (var plugin in plugins) bundleHash.AppendData(plugin.Hash);
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "StreamStudio", "vlc-replay-pause", Convert.ToHexString(bundleHash.GetHashAndReset()));
            var directory = Path.Combine(root, "demux");
            Directory.CreateDirectory(directory);
            foreach (var (name, bytes, hash) in plugins)
            {
                var path = Path.Combine(directory, name);
                var expectedHash = Convert.ToHexString(hash);
                if (FileHash.MatchesSha256(path, expectedHash)) continue;
                var temporary = Path.Combine(directory, $"{Guid.NewGuid():N}.tmp");
                try
                {
                    File.WriteAllBytes(temporary, bytes);
                    // Other processes may be extracting the same immutable, content-addressed DLL.
                    try { File.Move(temporary, path, overwrite: true); }
                    catch (IOException) when (FileHash.MatchesSha256(path, expectedHash))
                    { }
                }
                finally
                {
                    AtomicFile.TryDeleteTemporaryFile(temporary);
                }
            }

            preparedRoot = root;
            return root;
        }
    }

    private static (string Name, byte[] Bytes, byte[] Hash) ReadPlugin(string folder, string name)
    {
        using var resource = typeof(VlcReplayPausePlugin).Assembly.GetManifestResourceStream(
            $"StreamlinkVlcStudio.Infrastructure.Vlc.{folder}.{name}")
            ?? throw new FileNotFoundException($"The bundled replay plugin {name} is missing.");
        using var buffer = new MemoryStream();
        resource.CopyTo(buffer);
        var bytes = buffer.ToArray();
        return (name, bytes, SHA256.HashData(bytes));
    }
}
