using System.Net.Http.Headers;

internal static class UpdateModernizationTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("signed updater verifies release and reuses its 24-hour cache", SignedReleaseAndCadenceAsync),
        ("signed updater rejects tampering newer protocols and asset mismatch", SignedReleaseRejectionsAsync),
        ("update snoozing is version-specific and expires after 24 hours", UpdateSnoozing),
        ("update helper maps real installer completion codes", UpdateExitCodes)
    ];

    private static async Task SignedReleaseAndCadenceAsync()
    {
        using var rsa = RSA.Create(3072);
        var fixture = SignedReleaseFixture.Create(rsa, protocol: 1);
        using var client = new HttpClient(fixture.Handler);
        var root = NewTemporaryDirectory();
        try
        {
            using var service = new StagedAppUpdateService(
                new MemoryLogger(),
                client,
                root,
                Path.Combine(root, "updates"),
                () => new DateTimeOffset(2026, 8, 19, 12, 0, 0, TimeSpan.Zero),
                detectInstallKind: () => AppInstallKind.Zip,
                getCurrentVersion: () => new Version(1, 7, 0),
                trustedKey: rsa.ExportParameters(false));

            var first = await service.CheckAsync(UpdateCheckReason.Manual);
            Assert.True(first.IsUpdateAvailable);
            Assert.True(first.IsNotifyOnly);
            Assert.Equal(new Version(1, 8, 0), first.Release!.Version);
            Assert.Equal(AppUpdatePhase.NotifyOnly, service.State.Phase);
            Assert.Equal(1, fixture.Handler.ApiRequests);

            var cached = await service.CheckAsync(UpdateCheckReason.Startup);
            Assert.True(cached.IsUpdateAvailable);
            Assert.Equal(1, fixture.Handler.ApiRequests);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task SignedReleaseRejectionsAsync()
    {
        using var rsa = RSA.Create(3072);
        foreach (var fixture in new[]
        {
            SignedReleaseFixture.Create(rsa, protocol: 1, corruptSignature: true),
            SignedReleaseFixture.Create(rsa, protocol: 1, wrongKeyId: true),
            SignedReleaseFixture.Create(rsa, protocol: 2),
            SignedReleaseFixture.Create(rsa, protocol: 1, mismatchedSetupLength: true)
        })
        {
            using var client = new HttpClient(fixture.Handler);
            var root = NewTemporaryDirectory();
            try
            {
                using var service = new StagedAppUpdateService(
                    new MemoryLogger(),
                    client,
                    root,
                    Path.Combine(root, "updates"),
                    detectInstallKind: () => AppInstallKind.Managed,
                    getCurrentVersion: () => new Version(1, 7, 0),
                    trustedKey: rsa.ExportParameters(false));
                await Assert.ThrowsAsync<Exception>(() => service.CheckAsync(UpdateCheckReason.Manual));
                Assert.Equal(AppUpdatePhase.Failed, service.State.Phase);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static Task UpdateSnoozing()
    {
        var settings = new UpdateSettings
        {
            SnoozedVersion = "1.8.0",
            SnoozedUntilUtc = new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero)
        };
        Assert.True(settings.IsSnoozed(new Version(1, 8, 0), new DateTimeOffset(2026, 8, 19, 12, 0, 0, TimeSpan.Zero)));
        Assert.Equal(false, settings.IsSnoozed(new Version(1, 8, 1), new DateTimeOffset(2026, 8, 19, 12, 0, 0, TimeSpan.Zero)));
        Assert.Equal(false, settings.IsSnoozed(new Version(1, 8, 0), new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero)));
        return Task.CompletedTask;
    }

    private static Task UpdateExitCodes()
    {
        Assert.Equal(AppUpdateCompletionOutcome.Succeeded, UpdateHelperRunner.MapExitCode(0));
        Assert.Equal(AppUpdateCompletionOutcome.SucceededRebootRequired, UpdateHelperRunner.MapExitCode(3010));
        Assert.Equal(AppUpdateCompletionOutcome.Canceled, UpdateHelperRunner.MapExitCode(1602));
        Assert.Equal(AppUpdateCompletionOutcome.Canceled, UpdateHelperRunner.MapExitCode(1223));
        Assert.Equal(AppUpdateCompletionOutcome.Failed, UpdateHelperRunner.MapExitCode(5));
        return Task.CompletedTask;
    }

    private static string NewTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "StreamlinkVlcStudio-update-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed record SignedReleaseFixture(UpdateHttpHandler Handler)
    {
        public static SignedReleaseFixture Create(
            RSA rsa,
            int protocol,
            bool corruptSignature = false,
            bool mismatchedSetupLength = false,
            bool wrongKeyId = false)
        {
            var setup = Encoding.UTF8.GetBytes("setup-package");
            var zip = Encoding.UTF8.GetBytes("zip-package");
            var manifest = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = 1,
                protocolVersion = protocol,
                channel = "stable",
                prerelease = false,
                version = "1.8.0",
                tag = "v1.8.0",
                commit = new string('a', 40),
                repository = "CorontoSiete/streamlink-vlc-studio",
                releasePage = "https://github.com/CorontoSiete/streamlink-vlc-studio/releases/tag/v1.8.0",
                keyId = wrongKeyId
                    ? new string('0', 64)
                    : Convert.ToHexString(SHA256.HashData(rsa.ExportSubjectPublicKeyInfo())).ToLowerInvariant(),
                dependencyMinimums = new Dictionary<string, string> { ["streamlink"] = "8.2.1", ["vlc"] = "3.0.23" },
                setup = new { name = StagedAppUpdateService.SetupAssetName, length = setup.LongLength, sha256 = Convert.ToHexString(SHA256.HashData(setup)).ToLowerInvariant() },
                zip = new { name = StagedAppUpdateService.ZipAssetName, length = zip.LongLength, sha256 = Convert.ToHexString(SHA256.HashData(zip)).ToLowerInvariant() }
            });
            var signature = rsa.SignData(manifest, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
            if (corruptSignature) signature[0] ^= 0x80;
            var handler = new UpdateHttpHandler(manifest, signature, setup, zip, mismatchedSetupLength);
            return new(handler);
        }
    }

    private sealed class UpdateHttpHandler(
        byte[] manifest,
        byte[] signature,
        byte[] setup,
        byte[] zip,
        bool mismatchedSetupLength) : HttpMessageHandler
    {
        public int ApiRequests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/releases/latest", StringComparison.Ordinal))
            {
                ApiRequests++;
                var setupSize = setup.LongLength + (mismatchedSetupLength ? 1 : 0);
                var json = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    tag_name = "v1.8.0",
                    draft = false,
                    prerelease = false,
                    assets = new object[]
                    {
                        Asset(StagedAppUpdateService.UpdateManifestName, manifest.LongLength, "manifest"),
                        Asset(StagedAppUpdateService.UpdateSignatureName, signature.LongLength, "signature"),
                        Asset(StagedAppUpdateService.SetupAssetName, setupSize, "setup"),
                        Asset(StagedAppUpdateService.ZipAssetName, zip.LongLength, "zip")
                    }
                });
                var apiResponse = Response(json, "application/json");
                apiResponse.RequestMessage = request;
                return Task.FromResult(apiResponse);
            }

            var response = path switch
            {
                "/manifest" => Response(manifest),
                "/signature" => Response(signature),
                "/setup" => Response(setup),
                "/zip" => Response(zip),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
            response.RequestMessage = request;
            return Task.FromResult(response);
        }

        private static object Asset(string name, long size, string path) => new
        {
            name,
            size,
            browser_download_url = "https://downloads.example/" + path
        };

        private static HttpResponseMessage Response(byte[] bytes, string mediaType = "application/octet-stream")
        {
            var content = new ByteArrayContent(bytes);
            content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }
    }
}
