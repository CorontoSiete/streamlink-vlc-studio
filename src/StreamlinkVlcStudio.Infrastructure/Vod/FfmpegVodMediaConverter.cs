using System.Diagnostics;
using System.Globalization;
using System.Text;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Infrastructure.Processes;

namespace StreamlinkVlcStudio.Infrastructure.Vod;

internal interface IVodMediaConverter
{
    Task<OfflineVodPackage> ConvertAsync(string sourceDirectory, OfflineVodPackage source, string outputDirectory,
        VodDownloadFormat format, string streamlinkPath, CancellationToken cancellationToken);
}

internal sealed class FfmpegVodMediaConverter : IVodMediaConverter
{
    public async Task<OfflineVodPackage> ConvertAsync(string sourceDirectory, OfflineVodPackage source,
        string outputDirectory, VodDownloadFormat format, string streamlinkPath, CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(format)) throw new ArgumentOutOfRangeException(nameof(format));
        var executable = ExecutableResolver.FindFfmpeg(streamlinkPath) ?? throw new FileNotFoundException(
            "FFmpeg is required to convert VODs. Install Streamlink with FFmpeg, or add ffmpeg.exe to PATH, then retry.");
        cancellationToken.ThrowIfCancellationRequested();
        OfflineVodPackage.RequireRegularPath(outputDirectory);
        Directory.CreateDirectory(outputDirectory);
        var optimized = format == VodDownloadFormat.Mp4;
        var result = await RunAsync(CreateStartInfo(executable, sourceDirectory, source, outputDirectory, format, optimized),
            cancellationToken).ConfigureAwait(false);
        // The H.264-only guard rejects other video codecs. Retry the unchanged
        // stream-copy path so those VODs retain the existing MP4 behavior.
        if (optimized && !IsComplete(result, source.Duration))
            result = await RunAsync(CreateStartInfo(executable, sourceDirectory, source, outputDirectory, format, false),
                cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new InvalidDataException("FFmpeg could not convert this VOD. " + result.Diagnostics.Trim());
        if (!IsComplete(result, source.Duration))
            throw new InvalidDataException("The converted video does not contain the full VOD. The original download has been kept.");

        OfflineVodPackage package;
        if (format == VodDownloadFormat.Mp4)
            package = await OfflineVodPackage.CreateMp4Async(outputDirectory, source.Duration, cancellationToken).ConfigureAwait(false);
        else
        {
            var content = await File.ReadAllTextAsync(Path.Combine(outputDirectory, OfflineVodPackage.ManifestName),
                cancellationToken).ConfigureAwait(false);
            var playlist = OfflineHlsPlaylist.Parse(content, new Uri("https://offline.invalid/index.m3u8"), requireLocalAssets: true);
            package = await OfflineVodPackage.CreateAsync(outputDirectory, playlist, cancellationToken).ConfigureAwait(false);
        }
        await OfflineVodPackage.ValidateAsync(outputDirectory, cancellationToken).ConfigureAwait(false);
        return package;
    }

    private static bool IsComplete(ConversionResult result, TimeSpan expectedDuration) =>
        result.ExitCode == 0 && result.Duration > TimeSpan.Zero &&
        Math.Abs((result.Duration - expectedDuration).TotalSeconds) <= 2;

    private static ProcessStartInfo CreateStartInfo(string executable, string sourceDirectory, OfflineVodPackage source,
        string outputDirectory, VodDownloadFormat format, bool removeH264Filler)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        // MPEG-TS continuity errors (including Twitch's muted-segment padding) can
        // flag intact media packets as corrupt. -xerror makes those recoverable
        // demuxer warnings fatal even during stream copy. Keep the coded
        // pictures and audio; reject fatal failures, empty streams and short output.
        foreach (var argument in new[] { "-hide_banner", "-nostdin", "-y", "-loglevel", "warning",
            "-abort_on", "empty_output+empty_output_stream",
            "-progress", "pipe:1", "-nostats", "-protocol_whitelist", "file,crypto,data", "-fflags", "+genpts" })
            startInfo.ArgumentList.Add(argument);
        if (source.Format == VodDownloadFormat.Hls)
        {
            startInfo.ArgumentList.Add("-allowed_extensions");
            startInfo.ArgumentList.Add("ALL");
        }
        foreach (var argument in new[] { "-i", Path.Combine(sourceDirectory, source.MediaFileName),
            "-map", "0:v:0?", "-map", "0:a?", "-c", "copy", "-map_metadata", "-1", "-map_chapters", "-1",
            "-avoid_negative_ts", "make_zero" }) startInfo.ArgumentList.Add(argument);
        if (format == VodDownloadFormat.Mp4)
        {
            if (removeH264Filler)
            {
                // H.264 NAL type 12 is filler, not a coded picture. The first
                // filter accepts H.264 only; any other codec is retried above.
                startInfo.ArgumentList.Add("-bsf:v");
                startInfo.ArgumentList.Add("h264_metadata=aud=pass,filter_units=remove_types=12");
            }
            foreach (var argument in new[] { "-movflags", "+faststart", "-f", "mp4",
                Path.Combine(outputDirectory, OfflineVodPackage.Mp4FileName) }) startInfo.ArgumentList.Add(argument);
        }
        else
        {
            foreach (var argument in new[] { "-hls_time", "10", "-hls_playlist_type", "vod", "-hls_segment_filename",
                Path.Combine(outputDirectory, "asset-%06d.ts"), "-f", "hls",
                Path.Combine(outputDirectory, OfflineVodPackage.ManifestName) }) startInfo.ArgumentList.Add(argument);
        }
        return startInfo;
    }

    private static async Task<ConversionResult> RunAsync(ProcessStartInfo startInfo, CancellationToken cancellationToken)
    {
        using var process = RedirectedProcessOwner.Start(startInfo);
        var progress = ReadProgressAsync(process.StandardOutput);
        var diagnostics = ReadDiagnosticsAsync(process.StandardError);
        using var registration = cancellationToken.Register(process.Terminate);
        try
        {
            await process.Process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var duration = await progress.ConfigureAwait(false);
            var error = await diagnostics.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return new ConversionResult(process.Process.ExitCode, duration, error);
        }
        catch
        {
            await process.StopAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            try { await Task.WhenAll(progress, diagnostics).ConfigureAwait(false); }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException) { }
            throw;
        }
    }

    private sealed record ConversionResult(int ExitCode, TimeSpan Duration, string Diagnostics);

    private static async Task<TimeSpan> ReadProgressAsync(StreamReader reader)
    {
        long microseconds = 0;
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            if (line.StartsWith("out_time_us=", StringComparison.Ordinal) &&
                long.TryParse(line.AsSpan("out_time_us=".Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                microseconds = Math.Max(microseconds, value);
        return TimeSpan.FromSeconds(Math.Min(microseconds / 1_000_000d, TimeSpan.FromDays(15).TotalSeconds));
    }

    private static async Task<string> ReadDiagnosticsAsync(StreamReader reader)
    {
        var tail = new StringBuilder();
        var buffer = new char[1024];
        int count;
        while ((count = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
        {
            tail.Append(buffer, 0, count);
            if (tail.Length > 4096) tail.Remove(0, tail.Length - 4096);
        }
        return tail.ToString();
    }
}
