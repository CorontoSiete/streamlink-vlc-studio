using System.Security.Cryptography;

namespace StreamlinkVlcStudio.Infrastructure.Vlc;

internal static class VlcReplayPausePlugin
{
    private const string FileName = "libstudio_replay_pause_plugin.dll";
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

            using var resource = typeof(VlcReplayPausePlugin).Assembly.GetManifestResourceStream(
                $"StreamlinkVlcStudio.Infrastructure.Vlc.BundledReplayPause.{FileName}")
                ?? throw new FileNotFoundException("The bundled replay pause plugin is missing.");
            using var buffer = new MemoryStream();
            resource.CopyTo(buffer);
            var bytes = buffer.ToArray();
            var hash = SHA256.HashData(bytes);
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "StreamStudio", "vlc-replay-pause", Convert.ToHexString(hash));
            var directory = Path.Combine(root, "demux");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, FileName);
            if (!File.Exists(path) || !SHA256.HashData(File.ReadAllBytes(path)).AsSpan().SequenceEqual(hash))
            {
                var temporary = Path.Combine(directory, $"{Guid.NewGuid():N}.tmp");
                try
                {
                    File.WriteAllBytes(temporary, bytes);
                    // Other processes may be extracting the same immutable, content-addressed DLL.
                    try { File.Move(temporary, path, overwrite: true); }
                    catch (IOException) when (File.Exists(path) &&
                        SHA256.HashData(File.ReadAllBytes(path)).AsSpan().SequenceEqual(hash))
                    { }
                }
                finally
                {
                    File.Delete(temporary);
                }
            }

            preparedRoot = root;
            return root;
        }
    }
}
