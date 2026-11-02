using StreamlinkVlcStudio.Infrastructure.Http;
using StreamlinkVlcStudio.Infrastructure.Logging;

internal static partial class CredentialSecurityTestCatalog
{
    private static async Task CredentialHeadersAsync()
    {
        string[] inputs =
        [
            """{"password":"private-value with spaces", "quality":"best"}""",
            "passwd='private-value with spaces' quality=best",
            "https://example.com/?password=private-value&quality=best",
            "session_id=private-value quality=best",
            "id_token=private-value quality=best",
            "api_key=private-value quality=best",
            "csrf_token=private-value quality=best",
            """{"TwitchOAuthToken":"private-value", "quality":"best"}""",
            """{"KickOAuthToken":"private-value", "quality":"best"}""",
            """{"KickRefreshToken":"private-value", "quality":"best"}""",
            """{"KickClientSecret":"private-value", "quality":"best"}""",
            """{"Cookie":"session=private-value; other=private-value", "quality":"best"}""",
            "Cookie: session=private-value; other=private-value\r\nquality=best",
            "Set-Cookie: session=private-value; Path=/; HttpOnly\nquality=best",
            "Authorization: Basic private-value\r\nquality=best",
            "Proxy-Authorization: Basic private-value\r\nquality=best",
            "X-CSRF-Token: private-value\r\nquality=best",
            "X-API-Key: private-value\r\nquality=best"
        ];
        var directory = Path.Combine(Path.GetTempPath(), "StreamStudioTests", "credential-headers-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (var logger = new FileAppLogger(directory))
            {
                var messages = new List<LogEntry>();
                logger.EntryWritten += (_, entry) => messages.Add(entry);
                foreach (var input in inputs)
                {
                    var result = FileAppLogger.Sanitize(input, 4096);
                    Assert.DoesNotContain("private-value", result);
                    Assert.Contains("[REDACTED]", result);
                    Assert.Contains("quality", result);
                    logger.Write(AppLogLevel.Warning, input, input, new InvalidOperationException(input));
                }
                await logger.FlushAsync();
                foreach (var entry in messages)
                {
                    Assert.DoesNotContain("private-value", entry.Source);
                    Assert.DoesNotContain("private-value", entry.Message);
                }
            }
            var content = string.Join("", Directory.GetFiles(directory, "studio*.log").Select(File.ReadAllText));
            Assert.DoesNotContain("private-value", content);
            Assert.Contains("quality", content);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task CurlIsolationAsync()
    {
        // Exercise the real system curl using only synthetic local files. A file URL must
        // fail before any bytes are read, and user curl configuration must not run.
        var curl = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "curl.exe");
        Assert.True(File.Exists(curl), "Windows system curl is required for the fallback security regression.");
        var directory = Path.Combine(Path.GetTempPath(), "StreamStudioTests", "curl-isolation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var source = Path.Combine(directory, "private.txt");
            await File.WriteAllTextAsync(source, "private-file-sentinel");
            foreach (var name in new[] { ".curlrc", "_curlrc" })
                await File.WriteAllTextAsync(Path.Combine(directory, name), "write-out = \"injected-config-sentinel\"\n");
            var info = BoundedProcessRunner.CreateRedirectedStartInfo(curl,
                KickCurlArguments.BuildJsonRequest(new Uri(source).AbsoluteUri, "https://kick.com/", maxTimeSeconds: 3));
            info.Environment["CURL_HOME"] = directory;
            var result = await new BoundedProcessRunner().RunAsync(info, TimeSpan.FromSeconds(5));
            Assert.Equal(false, result.TimedOut);
            Assert.True(result.ExitCode != 0, "The HTTPS fallback accepted a local file URL.");
            Assert.Equal("", result.StandardOutput);
            Assert.DoesNotContain("private-file-sentinel", result.StandardError);
            Assert.DoesNotContain("injected-config-sentinel", result.StandardError);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
