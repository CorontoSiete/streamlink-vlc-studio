internal static partial class ApplicationTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> ChatSettingsAndPredictions { get; } =
    [
    ("limits Kick initial recent chat backfill to the newest messages", async () =>
    {
        var startedAt = new DateTimeOffset(2026, 6, 1, 20, 0, 0, TimeSpan.Zero);
        var payload = JsonSerializer.Serialize(new
        {
            data = new
            {
                messages = Enumerable.Range(0, 30)
                    .Select(index => new
                    {
                        id = $"recent-{index}",
                        content = $"recent message {index}",
                        created_at = startedAt.AddSeconds(index).ToString("O", CultureInfo.InvariantCulture),
                        sender = new { username = "viewer" }
                    })
                    .ToArray(),
                cursor = "older-page"
            }
        });
        using var httpClient = new HttpClient(new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        }));
        await using var client = new KickChatClient(new ChatSettings(), new MemoryLogger(), httpClient);
        SetKickClientBackfillState(client, "streamer", "123", "668");
        var received = new List<ChatMessage>();
        client.MessageReceived += (_, message) => received.Add(message);

        var method = typeof(KickChatClient).GetMethod(
            "BackfillRecentChatAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var result = await ((Task<ChatHistoryBackfillResult>)method!.Invoke(
            client,
            ["streamer", "668", null, null, 25, CancellationToken.None])!);

        Assert.Equal(25, result.LoadedMessageCount);
        Assert.Equal(25, received.Count);
        Assert.Equal("recent-5", received[0].MessageId);
        Assert.Equal("recent-29", received[^1].MessageId);
    }),
    ("builds Kick recent chat cursor page URL", () =>
    {
        var url = KickChatApi.BuildRecentMessagesUrl("668", "cursor-1", startTimeUtc: null);
        var arguments = StreamlinkVlcStudio.Infrastructure.Http.KickCurlArguments
            .BuildJsonRequest(url, "https://kick.com/channel")
            .ToArray();

        Assert.True(arguments.Any(argument =>
            argument == "https://kick.com/api/v2/channels/668/messages?cursor=cursor-1"));
        return Task.CompletedTask;
    }),
    ("builds Kick recent chat start time URL", () =>
    {
        var startTime = new DateTimeOffset(2026, 6, 1, 16, 4, 5, 123, TimeSpan.FromHours(-4));
        var url = KickChatApi.BuildRecentMessagesUrl("123", cursor: null, startTime);
        var arguments = StreamlinkVlcStudio.Infrastructure.Http.KickCurlArguments
            .BuildJsonRequest(url, "https://kick.com/streamer")
            .ToArray();

        Assert.True(arguments.Any(argument =>
            argument == "https://kick.com/api/v2/channels/123/messages?start_time=2026-06-01T20%3A04%3A05.123Z"));
        var refererIndex = Array.IndexOf(arguments, "--referer");
        Assert.True(refererIndex >= 0);
        Assert.Equal("https://kick.com/streamer", arguments[refererIndex + 1]);
        return Task.CompletedTask;
    }),
    ("reads Kick channel id separately from chatroom id", () =>
    {
        using var document = JsonDocument.Parse("""
        {
          "id": 123,
          "chatroom": { "id": 668 },
          "user": { "id": 9876 }
        }
        """);
        var parserType = typeof(KickChatClient).Assembly.GetType(
            "StreamlinkVlcStudio.Infrastructure.Chat.KickChannelInfoJson",
            throwOnError: true)!;
        var method = parserType.GetMethod(
            "Read",
            BindingFlags.Static | BindingFlags.Public);
        Assert.NotNull(method);

        var info = method!.Invoke(null, [document.RootElement])!;

        Assert.Equal("123", info.GetType().GetProperty("ChannelId")!.GetValue(info));
        Assert.Equal("668", info.GetType().GetProperty("ChatroomId")!.GetValue(info));
        Assert.Equal(9876L, info.GetType().GetProperty("BroadcasterUserId")!.GetValue(info));
        return Task.CompletedTask;
    }),
    ("reads Kick broadcaster envelope metadata", () =>
    {
        using var document = JsonDocument.Parse("""
        {
          "id": 123,
          "chatroom": { "id": 668 },
          "broadcaster": { "user_id": 9876 }
        }
        """);

        var info = KickChannelInfoJson.Read(document.RootElement);

        Assert.Equal("123", info.ChannelId);
        Assert.Equal("668", info.ChatroomId);
        Assert.Equal(9876L, info.BroadcasterUserId);
        Assert.Equal<string?>(null, KickChannelInfoJson.NormalizeNumericId("not-a-number"));
        return Task.CompletedTask;
    }),
    ("Kick timestamp backfill uses channel id path and referrer", async () =>
    {
        var requestUris = new List<string>();
        var referrers = new List<string?>();
        using var httpClient = new HttpClient(new FakeHttpMessageHandler(request =>
        {
            requestUris.Add(request.RequestUri!.AbsoluteUri);
            referrers.Add(request.Headers.Referrer?.AbsoluteUri);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                {
                  "data": {
                    "messages": [
                      {
                        "id": "kick-start-time-1",
                        "content": "timestamp backfill hello",
                        "created_at": "2026-06-01T20:04:06Z",
                        "sender": { "username": "viewer" }
                      }
                    ]
                  }
                }
                """, Encoding.UTF8, "application/json")
            };
        }));
        await using var client = new KickChatClient(new ChatSettings(), new MemoryLogger(), httpClient);
        SetKickClientBackfillState(client, "streamer", "123", "668");
        var received = new List<ChatMessage>();
        client.MessageReceived += (_, message) => received.Add(message);

        var fromTimestamp = new DateTimeOffset(2026, 6, 1, 16, 4, 5, 123, TimeSpan.FromHours(-4));
        var result = await client.BackfillRecentChatRangeAsync(
            fromTimestamp,
            fromTimestamp.AddSeconds(30));

        Assert.Equal(true, result.Attempted);
        Assert.Equal(1, result.LoadedMessageCount);
        Assert.Equal(false, result.CoveredRequestedRange);
        Assert.SequenceEqual(
            new[] { "https://kick.com/api/v2/channels/123/messages?start_time=2026-06-01T20%3A04%3A05.123Z" },
            requestUris);
        Assert.SequenceEqual(new[] { "https://kick.com/streamer" }, referrers.Select(referrer => referrer ?? ""));
        Assert.Equal("timestamp backfill hello", received.Single().Message);
    }),
    ("Kick timestamp backfill falls back to chatroom id path", async () =>
    {
        var previousCurl = Environment.GetEnvironmentVariable("STREAMLINK_KICK_CURL");
        var curlDirectory = Path.Combine(Path.GetTempPath(), "svs-kick-chatroom-fallback-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(curlDirectory);
            var curlPath = Path.Combine(curlDirectory, "curl.cmd");
            var curlMarkerPath = Path.Combine(curlDirectory, "invoked.txt");
            await File.WriteAllTextAsync(
                curlPath,
                $"@echo off{Environment.NewLine}type nul > \"{curlMarkerPath}\"{Environment.NewLine}exit /b 1");
            Environment.SetEnvironmentVariable("STREAMLINK_KICK_CURL", curlPath);

            var requestUris = new List<string>();
            using var httpClient = new HttpClient(new FakeHttpMessageHandler(request =>
            {
                requestUris.Add(request.RequestUri!.AbsoluteUri);
                if (request.RequestUri!.AbsolutePath.Contains("/channels/123/messages", StringComparison.Ordinal))
                {
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"data":{"messages":[]}}""", Encoding.UTF8, "application/json")
                };
            }));
            await using var client = new KickChatClient(new ChatSettings(), new MemoryLogger(), httpClient);
            SetKickClientBackfillState(client, "streamer", "123", "668");

            var fromTimestamp = new DateTimeOffset(2026, 6, 1, 20, 4, 5, 123, TimeSpan.Zero);
            var result = await client.BackfillRecentChatRangeAsync(
                fromTimestamp,
                fromTimestamp.AddSeconds(30));

            Assert.Equal(true, result.Attempted);
            Assert.Equal(0, result.LoadedMessageCount);
            Assert.Equal(false, result.CoveredRequestedRange);
            Assert.Equal<DateTimeOffset?>(null, result.CoveredFromTimestampUtc);
            Assert.Equal<DateTimeOffset?>(null, result.CoveredThroughTimestampUtc);
            Assert.True(File.Exists(curlMarkerPath));
            Assert.SequenceEqual(
                new[]
                {
                    "https://kick.com/api/v2/channels/123/messages?start_time=2026-06-01T20%3A04%3A05.123Z",
                    "https://kick.com/api/v2/channels/668/messages?start_time=2026-06-01T20%3A04%3A05.123Z"
                },
                requestUris);
        }
        finally
        {
            Environment.SetEnvironmentVariable("STREAMLINK_KICK_CURL", previousCurl);
            if (Directory.Exists(curlDirectory))
            {
                Directory.Delete(curlDirectory, recursive: true);
            }
        }
    }),
    ("Kick timestamp backfill verifies empty only after all start_time candidates are empty", async () =>
    {
        var requestUris = new List<string>();
        using var httpClient = new HttpClient(new FakeHttpMessageHandler(request =>
        {
            requestUris.Add(request.RequestUri!.AbsoluteUri);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"data":{"messages":[]}}""", Encoding.UTF8, "application/json")
            };
        }));
        await using var client = new KickChatClient(new ChatSettings(), new MemoryLogger(), httpClient);
        SetKickClientBackfillState(client, "streamer", "123", "668");

        var fromTimestamp = new DateTimeOffset(2026, 6, 1, 20, 4, 5, 123, TimeSpan.Zero);
        var throughTimestamp = fromTimestamp.AddSeconds(30);
        var result = await client.BackfillRecentChatRangeAsync(fromTimestamp, throughTimestamp);

        Assert.Equal(true, result.Attempted);
        Assert.Equal(0, result.LoadedMessageCount);
        Assert.Equal(true, result.CoveredRequestedRange);
        Assert.Equal(fromTimestamp, result.CoveredFromTimestampUtc);
        Assert.Equal(throughTimestamp, result.CoveredThroughTimestampUtc);
        Assert.SequenceEqual(
            new[]
            {
                "https://kick.com/api/v2/channels/123/messages?start_time=2026-06-01T20%3A04%3A05.123Z",
                "https://kick.com/api/v2/channels/668/messages?start_time=2026-06-01T20%3A04%3A05.123Z"
            },
            requestUris);
    }),
    ("Kick timestamp backfill follows start_time cursor pages through requested range", async () =>
    {
        var requestUris = new List<string>();
        using var httpClient = new HttpClient(new FakeHttpMessageHandler(request =>
        {
            requestUris.Add(request.RequestUri!.AbsoluteUri);
            var query = request.RequestUri.Query;
            if (query.Contains("cursor=cursor-1", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                    {
                      "data": {
                        "messages": [
                          {
                            "id": "kick-cursor-page-2",
                            "content": "cursor page two",
                            "created_at": "2026-06-01T20:04:20Z",
                            "sender": { "username": "viewer-two" }
                          }
                        ],
                        "cursor": "cursor-2"
                      }
                    }
                    """, Encoding.UTF8, "application/json")
                };
            }

            if (query.Contains("cursor=cursor-2", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                    {
                      "data": {
                        "messages": [
                          {
                            "id": "kick-cursor-page-3",
                            "content": "cursor page three",
                            "created_at": "2026-06-01T20:04:40Z",
                            "sender": { "username": "viewer-three" }
                          }
                        ]
                      }
                    }
                    """, Encoding.UTF8, "application/json")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                {
                  "data": {
                    "messages": [
                      {
                        "id": "kick-cursor-page-1",
                        "content": "cursor page one",
                        "created_at": "2026-06-01T20:04:06Z",
                        "sender": { "username": "viewer-one" }
                      }
                    ],
                    "cursor": "cursor-1"
                  }
                }
                """, Encoding.UTF8, "application/json")
            };
        }));
        await using var client = new KickChatClient(new ChatSettings(), new MemoryLogger(), httpClient);
        SetKickClientBackfillState(client, "streamer", "123", "668");
        var received = new List<ChatMessage>();
        client.MessageReceived += (_, message) => received.Add(message);

        var fromTimestamp = new DateTimeOffset(2026, 6, 1, 20, 4, 5, TimeSpan.Zero);
        var throughTimestamp = new DateTimeOffset(2026, 6, 1, 20, 4, 35, TimeSpan.Zero);
        var result = await client.BackfillRecentChatRangeAsync(fromTimestamp, throughTimestamp);

        Assert.Equal(true, result.Attempted);
        Assert.Equal(2, result.LoadedMessageCount);
        Assert.Equal(true, result.CoveredRequestedRange);
        Assert.Equal(fromTimestamp, result.CoveredFromTimestampUtc);
        Assert.Equal(throughTimestamp, result.CoveredThroughTimestampUtc);
        Assert.SequenceEqual(
            new[]
            {
                "https://kick.com/api/v2/channels/123/messages?start_time=2026-06-01T20%3A04%3A05.000Z",
                "https://kick.com/api/v2/channels/123/messages?cursor=cursor-1",
                "https://kick.com/api/v2/channels/123/messages?cursor=cursor-2"
            },
            requestUris);
        Assert.SequenceEqual(
            new[] { "cursor page one", "cursor page two" },
            received.Select(message => message.Message).ToArray());
    }),
    ("Kick timestamp backfill stops when cursor pages move older", async () =>
    {
        var requestUris = new List<string>();
        using var httpClient = new HttpClient(new FakeHttpMessageHandler(request =>
        {
            requestUris.Add(request.RequestUri!.AbsoluteUri);
            var query = request.RequestUri.Query;
            if (query.Contains("cursor=older-cursor", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                    {
                      "data": {
                        "messages": [
                          {
                            "id": "kick-older-cursor-page",
                            "content": "older cursor page",
                            "created_at": "2026-06-01T20:04:12Z",
                            "sender": { "username": "older-viewer" }
                          }
                        ],
                        "cursor": "should-not-be-requested"
                      }
                    }
                    """, Encoding.UTF8, "application/json")
                };
            }

            if (query.Contains("cursor=should-not-be-requested", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"data":{"messages":[]}}""", Encoding.UTF8, "application/json")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                {
                  "data": {
                    "messages": [
                      {
                        "id": "kick-visible-cursor-page",
                        "content": "visible cursor page",
                        "created_at": "2026-06-01T20:04:20Z",
                        "sender": { "username": "visible-viewer" }
                      }
                    ],
                    "cursor": "older-cursor"
                  }
                }
                """, Encoding.UTF8, "application/json")
            };
        }));
        await using var client = new KickChatClient(new ChatSettings(), new MemoryLogger(), httpClient);
        SetKickClientBackfillState(client, "streamer", "123", "668");
        var received = new List<ChatMessage>();
        client.MessageReceived += (_, message) => received.Add(message);

        var fromTimestamp = new DateTimeOffset(2026, 6, 1, 20, 4, 5, TimeSpan.Zero);
        var throughTimestamp = new DateTimeOffset(2026, 6, 1, 20, 4, 35, TimeSpan.Zero);
        var result = await client.BackfillRecentChatRangeAsync(fromTimestamp, throughTimestamp);

        Assert.Equal(true, result.Attempted);
        Assert.Equal(1, result.LoadedMessageCount);
        Assert.Equal(false, result.CoveredRequestedRange);
        Assert.Equal(fromTimestamp, result.CoveredFromTimestampUtc);
        Assert.Equal(new DateTimeOffset(2026, 6, 1, 20, 4, 20, TimeSpan.Zero), result.CoveredThroughTimestampUtc);
        Assert.SequenceEqual(
            new[]
            {
                "https://kick.com/api/v2/channels/123/messages?start_time=2026-06-01T20%3A04%3A05.000Z",
                "https://kick.com/api/v2/channels/123/messages?cursor=older-cursor"
            },
            requestUris);
        Assert.SequenceEqual(
            new[] { "visible cursor page" },
            received.Select(message => message.Message).ToArray());
    }),
    ("Kick timestamp backfill keeps distinct messages with equal cursor timestamps", async () =>
    {
        var requestUris = new List<string>();
        using var httpClient = new HttpClient(new FakeHttpMessageHandler(request =>
        {
            requestUris.Add(request.RequestUri!.AbsoluteUri);
            var isSecondPage = request.RequestUri.Query.Contains("cursor=equal-timestamp", StringComparison.Ordinal);
            var body = isSecondPage
                ? """
                  {
                    "data": {
                      "messages": [
                        {
                          "id": "equal-timestamp-2",
                          "content": "equal timestamp page two",
                          "created_at": "2026-06-01T20:04:20Z",
                          "sender": { "username": "viewer-two" }
                        }
                      ]
                    }
                  }
                  """
                : """
                  {
                    "data": {
                      "messages": [
                        {
                          "id": "equal-timestamp-1",
                          "content": "equal timestamp page one",
                          "created_at": "2026-06-01T20:04:20Z",
                          "sender": { "username": "viewer-one" }
                        }
                      ],
                      "cursor": "equal-timestamp"
                    }
                  }
                  """;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        }));
        await using var client = new KickChatClient(new ChatSettings(), new MemoryLogger(), httpClient);
        SetKickClientBackfillState(client, "streamer", "123", "668");
        var received = new List<ChatMessage>();
        client.MessageReceived += (_, message) => received.Add(message);

        var fromTimestamp = new DateTimeOffset(2026, 6, 1, 20, 4, 5, TimeSpan.Zero);
        var throughTimestamp = new DateTimeOffset(2026, 6, 1, 20, 4, 35, TimeSpan.Zero);
        var result = await client.BackfillRecentChatRangeAsync(fromTimestamp, throughTimestamp);

        Assert.Equal(2, result.LoadedMessageCount);
        Assert.Equal(false, result.CoveredRequestedRange);
        Assert.Equal(new DateTimeOffset(2026, 6, 1, 20, 4, 20, TimeSpan.Zero), result.CoveredThroughTimestampUtc);
        Assert.Equal(2, requestUris.Count);
        Assert.SequenceEqual(
            new[] { "equal timestamp page one", "equal timestamp page two" },
            received.Select(message => message.Message).ToArray());
    }),
    ("Kick timestamp backfill keeps nonempty cursor exhaustion partial before requested range end", async () =>
    {
        var requestUris = new List<string>();
        using var httpClient = new HttpClient(new FakeHttpMessageHandler(request =>
        {
            requestUris.Add(request.RequestUri!.AbsoluteUri);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                {
                  "data": {
                    "messages": [
                      {
                        "id": "kick-partial-page-1",
                        "content": "partial page one",
                        "created_at": "2026-06-01T20:04:06Z",
                        "sender": { "username": "viewer-one" }
                      }
                    ]
                  }
                }
                """, Encoding.UTF8, "application/json")
            };
        }));
        await using var client = new KickChatClient(new ChatSettings(), new MemoryLogger(), httpClient);
        SetKickClientBackfillState(client, "streamer", "123", "668");
        var received = new List<ChatMessage>();
        client.MessageReceived += (_, message) => received.Add(message);

        var fromTimestamp = new DateTimeOffset(2026, 6, 1, 20, 4, 5, TimeSpan.Zero);
        var throughTimestamp = new DateTimeOffset(2026, 6, 1, 20, 4, 35, TimeSpan.Zero);
        var result = await client.BackfillRecentChatRangeAsync(fromTimestamp, throughTimestamp);

        Assert.Equal(true, result.Attempted);
        Assert.Equal(1, result.LoadedMessageCount);
        Assert.Equal(false, result.CoveredRequestedRange);
        Assert.Equal(fromTimestamp, result.CoveredFromTimestampUtc);
        Assert.Equal(new DateTimeOffset(2026, 6, 1, 20, 4, 6, TimeSpan.Zero), result.CoveredThroughTimestampUtc);
        Assert.SequenceEqual(
            new[] { "https://kick.com/api/v2/channels/123/messages?start_time=2026-06-01T20%3A04%3A05.000Z" },
            requestUris);
        Assert.Equal("partial page one", received.Single().Message);
    }),
    ("Kick timestamp backfill stops at seekback cap with retryable partial coverage", async () =>
    {
        var requestUris = new List<string>();
        var firstTimestamp = new DateTimeOffset(2026, 6, 1, 20, 4, 6, TimeSpan.Zero);
        var payload = new StringBuilder();
        payload.Append("{\"data\":{\"messages\":[");
        for (var index = 0; index < 2_500; index++)
        {
            if (index > 0)
            {
                payload.Append(',');
            }

            var timestamp = firstTimestamp.AddSeconds(index);
            payload.Append("{\"id\":\"kick-cap-");
            payload.Append(index.ToString(CultureInfo.InvariantCulture));
            payload.Append("\",\"content\":\"cap message ");
            payload.Append(index.ToString(CultureInfo.InvariantCulture));
            payload.Append("\",\"created_at\":\"");
            payload.Append(timestamp.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
            payload.Append("\",\"sender\":{\"username\":\"viewer\"}}");
        }

        payload.Append("],\"cursor\":\"cursor-after-cap\"}}");
        using var httpClient = new HttpClient(new FakeHttpMessageHandler(request =>
        {
            requestUris.Add(request.RequestUri!.AbsoluteUri);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload.ToString(), Encoding.UTF8, "application/json")
            };
        }));
        await using var client = new KickChatClient(new ChatSettings(), new MemoryLogger(), httpClient);
        SetKickClientBackfillState(client, "streamer", "123", "668");
        var received = new List<ChatMessage>();
        client.MessageReceived += (_, message) => received.Add(message);

        var fromTimestamp = new DateTimeOffset(2026, 6, 1, 20, 4, 5, TimeSpan.Zero);
        var throughTimestamp = fromTimestamp.AddHours(2);
        var result = await client.BackfillRecentChatRangeAsync(fromTimestamp, throughTimestamp);

        Assert.Equal(true, result.Attempted);
        Assert.Equal(2_500, result.LoadedMessageCount);
        Assert.Equal(false, result.CoveredRequestedRange);
        Assert.Equal(fromTimestamp, result.CoveredFromTimestampUtc);
        Assert.Equal(firstTimestamp.AddSeconds(2_499), result.CoveredThroughTimestampUtc);
        Assert.Equal(2_500, received.Count);
        Assert.SequenceEqual(
            new[] { "https://kick.com/api/v2/channels/123/messages?start_time=2026-06-01T20%3A04%3A05.000Z" },
            requestUris);
    }),
    ("Kick timestamp backfill becomes retryable when client is disposed mid-request", async () =>
    {
        var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var httpClient = new HttpClient(new AsyncHttpMessageHandler(async (_, cancellationToken) =>
        {
            requestStarted.TrySetResult();
            await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"data":{"messages":[]}}""", Encoding.UTF8, "application/json")
            };
        }));
        var client = new KickChatClient(new ChatSettings(), new MemoryLogger(), httpClient);
        SetKickClientBackfillState(client, "streamer", "123", "668");

        var fromTimestamp = new DateTimeOffset(2026, 6, 1, 20, 4, 5, TimeSpan.Zero);
        var backfillTask = client.BackfillRecentChatRangeAsync(fromTimestamp, fromTimestamp.AddSeconds(30));
        await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await client.DisposeAsync();

        var result = await backfillTask.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(false, result.CoveredRequestedRange);
        Assert.Equal<DateTimeOffset?>(null, result.CoveredFromTimestampUtc);
        Assert.Equal<DateTimeOffset?>(null, result.CoveredThroughTimestampUtc);
    }),
    ("Kick timestamp backfill tries chatroom id after empty channel id page", async () =>
    {
        var requestUris = new List<string>();
        using var httpClient = new HttpClient(new FakeHttpMessageHandler(request =>
        {
            requestUris.Add(request.RequestUri!.AbsoluteUri);
            if (request.RequestUri!.AbsolutePath.Contains("/channels/123/messages", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"data":{"messages":[]}}""", Encoding.UTF8, "application/json")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                {
                  "data": {
                    "messages": [
                      {
                        "id": "kick-chatroom-start-time",
                        "content": "chatroom id timestamp message",
                        "created_at": "2026-06-01T20:04:10Z",
                        "sender": { "username": "viewer" }
                      }
                    ]
                  }
                }
                """, Encoding.UTF8, "application/json")
            };
        }));
        await using var client = new KickChatClient(new ChatSettings(), new MemoryLogger(), httpClient);
        SetKickClientBackfillState(client, "streamer", "123", "668");
        var received = new List<ChatMessage>();
        client.MessageReceived += (_, message) => received.Add(message);

        var fromTimestamp = new DateTimeOffset(2026, 6, 1, 20, 4, 5, TimeSpan.Zero);
        var result = await client.BackfillRecentChatRangeAsync(fromTimestamp, fromTimestamp.AddSeconds(30));

        Assert.Equal(true, result.Attempted);
        Assert.Equal(1, result.LoadedMessageCount);
        Assert.Equal(false, result.CoveredRequestedRange);
        Assert.SequenceEqual(
            new[]
            {
                "https://kick.com/api/v2/channels/123/messages?start_time=2026-06-01T20%3A04%3A05.000Z",
                "https://kick.com/api/v2/channels/668/messages?start_time=2026-06-01T20%3A04%3A05.000Z"
            },
            requestUris);
        Assert.Equal("chatroom id timestamp message", received.Single().Message);
    }),
    ("parses Kick Pusher escaped multilingual chat message", () =>
    {
        var emoji = char.ConvertFromUtf32(0x1F602);
        var payload = """
        {
          "event": "App\\Events\\ChatMessageEvent",
          "data": "{\"id\":\"kick-unicode\",\"content\":\"\\u3053\\u3093\\u306B\\u3061\\u306F \\uD83D\\uDE02 \\u0645\\u0631\\u062D\\u0628\\u0627\",\"sender\":{\"username\":\"\\u89D6\\u8074\\u8005\"}}"
        }
        """;

        var message = KickPusherParser.TryParse(payload, "channel");

        Assert.NotNull(message);
        Assert.Equal("\u89D6\u8074\u8005", message!.Username);
        Assert.Equal("\u3053\u3093\u306B\u3061\u306F " + emoji + " \u0645\u0631\u062D\u0628\u0627", message.Message);
        Assert.Equal("kick-unicode", message.MessageId);
        return Task.CompletedTask;
    }),
    ("parses Kick sender badge objects with image urls", () =>
    {
        var payload = """
        {
          "event": "App\\Events\\ChatMessageEvent",
          "data": "{\"content\":\"hello kick\",\"sender\":{\"username\":\"viewer\",\"badges\":[{\"id\":\"vip\",\"name\":\"VIP\",\"info\":\"\",\"imageUrl\":\"https://files.kick.com/badge.png\"}]}}"
        }
        """;
        var message = KickPusherParser.TryParse(payload, "channel");
        Assert.NotNull(message);
        Assert.NotNull(message!.Badges);
        Assert.Equal(1, message.Badges!.Count);
        Assert.Equal("vip", message.Badges[0].Id);
        Assert.Equal("VIP", message.Badges[0].Title);
        Assert.Equal("https://files.kick.com/badge.png", message.Badges[0].ImageUrl);
        return Task.CompletedTask;
    }),
    ("parses Kick badge nested image urls", () =>
    {
        var payload = """
        {
          "event": "App\\Events\\ChatMessageEvent",
          "data": "{\"content\":\"hello kick\",\"sender\":{\"username\":\"viewer\",\"identity\":{\"badges\":[{\"type\":\"moderator\",\"title\":\"Moderator\",\"images\":{\"url\":\"https://files.kick.com/badges/moderator.png\"}}]}}}"
        }
        """;
        var message = KickPusherParser.TryParse(payload, "channel");
        Assert.NotNull(message);
        Assert.NotNull(message!.Badges);
        Assert.Equal(1, message.Badges!.Count);
        Assert.Equal("moderator", message.Badges[0].Id);
        Assert.Equal("Moderator", message.Badges[0].Title);
        Assert.Equal("https://files.kick.com/badges/moderator.png", message.Badges[0].ImageUrl);
        return Task.CompletedTask;
    }),
    ("parses Kick badges_v2 image urls and sort order", () =>
    {
        var payload = """
        {
          "event": "App\\Events\\ChatMessageEvent",
          "data": "{\"content\":\"level badge\",\"sender\":{\"username\":\"viewer\",\"identity\":{\"badges_v2\":[{\"name\":\"level\",\"badge_type\":\"global\",\"image_url\":\"https://ext.cdn.kick.com/chat/badges/10_804bf82a-c167-4184-a613-dfeb5f8bd1f0.png\",\"metadata\":{\"level\":10},\"sort_order\":1}],\"badges\":[{\"type\":\"subscriber\",\"text\":\"Subscriber\",\"count\":10,\"sort_order\":3}]}}}"
        }
        """;
        var message = KickPusherParser.TryParse(payload, "channel");
        Assert.NotNull(message);
        Assert.NotNull(message!.Badges);
        Assert.Equal(2, message.Badges!.Count);
        Assert.Equal("level", message.Badges[0].Id);
        Assert.Equal("10", message.Badges[0].Version);
        Assert.Equal("Level 10", message.Badges[0].Title);
        Assert.Equal("https://ext.cdn.kick.com/chat/badges/10_804bf82a-c167-4184-a613-dfeb5f8bd1f0.png", message.Badges[0].ImageUrl);
        Assert.Equal("subscriber", message.Badges[1].Id);
        Assert.Equal("10", message.Badges[1].Version);
        Assert.Equal("10-Month Subscriber", message.Badges[1].Title);
        return Task.CompletedTask;
    }),
    ("merges duplicate Kick badge metadata without discarding richer fields", () =>
    {
        var payload = """
        {
          "event": "App\\Events\\ChatMessageEvent",
          "data": "{\"content\":\"duplicate badge\",\"sender\":{\"username\":\"viewer\",\"identity\":{\"badges_v2\":[{\"name\":\"subscriber\",\"metadata\":{\"months\":12},\"sort_order\":1}],\"badges\":[{\"type\":\"subscriber\",\"image_url\":\"https://files.kick.com/subscriber.png\",\"sort_order\":3}]}}}"
        }
        """;

        var message = KickPusherParser.TryParse(payload, "channel");

        Assert.NotNull(message);
        Assert.NotNull(message!.Badges);
        Assert.Equal(1, message.Badges!.Count);
        Assert.Equal("subscriber", message.Badges[0].Id);
        Assert.Equal("12", message.Badges[0].Version);
        Assert.Equal("12-Month Subscriber", message.Badges[0].Title);
        Assert.Equal("https://files.kick.com/subscriber.png", message.Badges[0].ImageUrl);
        return Task.CompletedTask;
    }),
    ("prefers richer Kick badge titles when duplicate metadata arrives later", () =>
    {
        var payload = """
        {
          "event": "App\\Events\\ChatMessageEvent",
          "data": "{\"content\":\"duplicate title\",\"sender\":{\"username\":\"viewer\",\"identity\":{\"badges_v2\":[{\"type\":\"subscriber\"}],\"badges\":[{\"type\":\"subscriber\",\"count\":12}]}}}"
        }
        """;

        var message = KickPusherParser.TryParse(payload, "channel");

        Assert.NotNull(message);
        Assert.NotNull(message!.Badges);
        Assert.Equal(1, message.Badges!.Count);
        Assert.Equal("12-Month Subscriber", message.Badges[0].Title);
        Assert.Equal("12", message.Badges[0].Version);
        return Task.CompletedTask;
    }),
    ("bundled Kick badge manifest includes VLC overlay role badge images", () =>
    {
        var manifestPath = BundledBadgeAssets.FindKickBadgeManifestPath();
        if (string.IsNullOrWhiteSpace(manifestPath) || !File.Exists(manifestPath))
        {
            throw new InvalidOperationException($"Missing Kick badge manifest: {manifestPath}");
        }

        using var document = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
        Assert.True(document.RootElement.TryGetProperty("entries", out var entries));
        Assert.Equal(JsonValueKind.Array, entries.ValueKind);

        var root = Path.GetDirectoryName(manifestPath)!;
        var rootFullPath = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries.EnumerateArray())
        {
            var id = entry.GetProperty("id").GetString();
            var version = entry.GetProperty("version").GetString();
            var image = entry.GetProperty("image").GetString();
            Assert.Equal(false, string.IsNullOrWhiteSpace(id));
            Assert.Equal(false, string.IsNullOrWhiteSpace(version));
            Assert.Equal(false, string.IsNullOrWhiteSpace(image));

            var imagePath = Path.GetFullPath(Path.Combine(root, image!.Replace('/', Path.DirectorySeparatorChar)));
            if (!imagePath.StartsWith(rootFullPath, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Kick badge image escaped manifest root: {imagePath}");
            }
            if (!File.Exists(imagePath))
            {
                throw new InvalidOperationException($"Missing Kick badge image: {imagePath}");
            }
            seen.Add($"{id}/{version}");
        }

        foreach (var key in new[]
        {
            "broadcaster/1",
            "moderator/1",
            "mod/1",
            "vip/1",
            "og/1",
            "verified/1",
            "sub_gifter/1",
            "subscriber/1"
        })
        {
            if (!seen.Contains(key))
            {
                throw new InvalidOperationException($"Missing Kick badge manifest entry: {key}");
            }
        }

        return Task.CompletedTask;
    }),
    ("bundled badge assets extract Twitch and Kick image files from app resources", () =>
    {
        var twitchManifestPath = ExtractBundledBadgeManifestPath("TwitchBadges");
        AssertManifestParsesFromUtf8Bytes(twitchManifestPath);
        AssertManifestImageExists(twitchManifestPath, "global/moderator/1.png");
        AssertManifestImageExists(twitchManifestPath, "global/premium/1.png");
        AssertManifestEntryTitle(twitchManifestPath, "la-velada-iv", "La Velada del Año IV");
        AssertManifestEntryTitle(
            twitchManifestPath,
            "lego-batman-legacy-of-the-dark-knight",
            "LEGO® Batman™: Legacy of the Dark Knight");
        AssertManifestEntryTitle(twitchManifestPath, "pokemon-30th-anniversary", "Pokémon 30th");
        AssertManifestEntryTitle(
            twitchManifestPath,
            "pokemon-legends-z-a-chikorita",
            "Pokémon Legends: Z-A Chikorita");
        AssertManifestEntryTitle(
            twitchManifestPath,
            "pokemon-legends-z-a-tepig",
            "Pokémon Legends: Z-A Tepig");
        AssertManifestEntryTitle(
            twitchManifestPath,
            "pokemon-legends-z-a-totodile",
            "Pokémon Legends: Z-A Totodile");

        var kickManifestPath = ExtractBundledBadgeManifestPath("KickBadges");
        AssertManifestParsesFromUtf8Bytes(kickManifestPath);
        AssertManifestImageExists(kickManifestPath, "global/moderator.png");
        AssertManifestImageExists(kickManifestPath, "global/og.png");
        AssertManifestImageExists(kickManifestPath, "global/sub_gifter.png");
        return Task.CompletedTask;
    }),
    ("parses Kick gifted subscription badges as gift badges", () =>
    {
        var payload = """
        {
          "event": "App\\Events\\ChatMessageEvent",
          "data": "{\"content\":\"gift badge\",\"sender\":{\"username\":\"viewer\",\"identity\":{\"badges\":[{\"type\":\"gifted_subscription\",\"text\":\"Sub Gifter\",\"count\":25}]}}}"
        }
        """;
        var message = KickPusherParser.TryParse(payload, "channel");
        Assert.NotNull(message);
        Assert.NotNull(message!.Badges);
        Assert.Equal(1, message.Badges!.Count);
        Assert.Equal("sub_gifter", message.Badges[0].Id);
        Assert.Equal("25", message.Badges[0].Version);
        Assert.Equal("Sub Gifter", message.Badges[0].Title);
        return Task.CompletedTask;
    }),
    ("parses Kick emote markers", () =>
    {
        var payload = """
        {
          "event": "App\\Events\\ChatMessageEvent",
          "data": "{\"content\":\"hello [emote:123:KEKW] chat\",\"sender\":{\"username\":\"viewer\"}}"
        }
        """;
        var message = KickPusherParser.TryParse(payload, "channel");
        Assert.NotNull(message);
        Assert.NotNull(message!.Emotes);
        Assert.Equal(1, message.Emotes!.Count);
        Assert.Equal("KEKW", message.Emotes[0].Code);
        Assert.Equal(6, message.Emotes[0].StartIndex);
        Assert.Equal(22, message.Emotes[0].EndIndex);
        Assert.Contains("/emotes/123/fullsize", message.Emotes[0].ImageUrl!);
        return Task.CompletedTask;
    }),
    ("ignores malformed Kick Pusher frames", () =>
    {
        Assert.Equal<ChatMessage?>(null, KickPusherParser.TryParse(null, "channel"));
        Assert.Equal<ChatMessage?>(null, KickPusherParser.TryParse("   ", "channel"));
        Assert.Equal<ChatMessage?>(null, KickPusherParser.TryParse("{not json", "channel"));
        Assert.Equal<ChatMessage?>(null, KickPusherParser.TryParse("[]", "channel"));
        Assert.Equal<ChatMessage?>(null, KickPusherParser.TryParse("""{"event":"App\\Events\\ChatMessageEvent","data":"{not json"}""", "channel"));

        var malformedSender = KickPusherParser.TryParse(
            """{"event":"App\\Events\\ChatMessageEvent","data":{"content":"valid message","sender":[]}}""",
            "channel");
        Assert.NotNull(malformedSender);
        Assert.Equal("unknown", malformedSender!.Username);
        Assert.NotNull(malformedSender.Badges);
        Assert.Equal(0, malformedSender.Badges!.Count);
        return Task.CompletedTask;
    }),
    ("quality option displays its label", () =>
    {
        Assert.Equal("Best", QualityOption.Defaults[0].ToString());
        Assert.Equal("Audio only", QualityOption.Defaults.Single(option => option.Id == "audio_only").ToString());
        return Task.CompletedTask;
    }),
    ("round trips JSON settings", async () =>
    {
        var temp = Path.Combine(Path.GetTempPath(), $"svs-settings-{Guid.NewGuid():N}.json");
        var service = new JsonSettingsService(temp);
        var settings = new AppSettings
        {
            StreamlinkPath = @"C:\Tools\streamlink.exe",
            VlcDirectory = @"C:\Program Files\VideoLAN\VLC",
            DefaultPlatform = PlatformKind.Kick,
            MultiStreamEnabled = true,
            KeepHomeCardRightGap = false,
            CustomStreamlinkArguments = "--retry-streams 10"
        };
        Assert.True(settings.Chat.SetKickChatroomId("xqc", "123"));
        Assert.True(settings.Chat.SetKickBroadcasterUserId("xqc", "456"));
        settings.Chat.TwitchUsername = "mybot";
        settings.Chat.TwitchOAuthToken = "oauth:twitch-token";
        settings.Chat.TwitchTokenExpiresAtUtc = new DateTimeOffset(2026, 5, 30, 19, 0, 0, TimeSpan.Zero);
        settings.Chat.TwitchClientId = "twitch-client-id";
        settings.Chat.KickUsername = "kickbot";
        settings.Chat.KickOAuthToken = "kick-token";
        settings.Chat.KickRefreshToken = "kick-refresh-token";
        settings.Chat.KickTokenExpiresAtUtc = new DateTimeOffset(2026, 5, 30, 18, 0, 0, TimeSpan.Zero);
        settings.Chat.KickClientId = "kick-client-id";
        settings.Chat.KickClientSecret = "kick-client-secret";
        settings.Chat.KickSendAsBot = true;
        settings.Chat.Layout = ChatLayout.Docked;
        settings.Chat.VlcOverlayDirectory = @"C:\Tools\vlc-overlay";
        settings.Chat.VlcOverlayFontSize = 16;
        settings.FollowedChannels.KickChannelSlugs = ["xqc", "some-channel"];
        settings.FollowedChannels.NotifyWhenLive = false;
        settings.RecentStreams =
        [
            new RecentStreamSettings
            {
                Platform = PlatformKind.Twitch,
                Channel = "albralelie",
                DisplayName = "albralelie",
                ThumbnailUrl = "https://static-cdn.jtvnw.net/previews-ttv/live_user_albralelie-440x248.jpg",
                LastQuality = "720p60",
                LastWatchedAtUtc = new DateTimeOffset(2026, 5, 30, 20, 0, 0, TimeSpan.Zero)
            }
        ];
        settings.StreamVolumes["Twitch:xqc"] = 37;
        settings.StreamVolumes["Kick:xqc"] = 54;
        settings.StreamVlcOverlayFontSizes["Twitch:xqc"] = 22;
        settings.StreamVlcOverlayFontSizes["Kick:xqc"] = 18;
        settings.StreamPictureInPictureTopBarVisibility["Twitch:xqc"] = false;
        settings.StreamPictureInPictureTopBarVisibility["Kick:xqc"] = true;
        settings.PictureInPictureWindowLocation = new PictureInPictureWindowLocation(240, 320, 640, 360)
        {
            IsFullscreen = true,
            FullscreenMode = PictureInPictureFullscreenMode.MultiView,
            FullscreenScreen = new PictureInPictureFullscreenScreen(@"\\.\DISPLAY7", 10, 20, 1920, 1080)
        };

        await service.SaveAsync(settings);
        var loaded = await service.LoadAsync();
        File.Delete(temp);

        Assert.Equal(PlatformKind.Kick, loaded.DefaultPlatform);
        Assert.True(loaded.MultiStreamEnabled);
        Assert.Equal(false, loaded.KeepHomeCardRightGap);
        Assert.Equal("--retry-streams 10", loaded.CustomStreamlinkArguments);
        Assert.Equal("123", loaded.Chat.KickChatroomIds["xqc"]);
        Assert.Equal("456", loaded.Chat.KickBroadcasterUserIds["xqc"]);
        Assert.Equal("mybot", loaded.Chat.TwitchUsername);
        Assert.Equal("oauth:twitch-token", loaded.Chat.TwitchOAuthToken);
        Assert.Equal(new DateTimeOffset(2026, 5, 30, 19, 0, 0, TimeSpan.Zero), loaded.Chat.TwitchTokenExpiresAtUtc);
        Assert.Equal("twitch-client-id", loaded.Chat.TwitchClientId);
        Assert.Equal("kickbot", loaded.Chat.KickUsername);
        Assert.Equal("kick-token", loaded.Chat.KickOAuthToken);
        Assert.Equal("kick-refresh-token", loaded.Chat.KickRefreshToken);
        Assert.Equal(new DateTimeOffset(2026, 5, 30, 18, 0, 0, TimeSpan.Zero), loaded.Chat.KickTokenExpiresAtUtc);
        Assert.Equal("kick-client-id", loaded.Chat.KickClientId);
        Assert.Equal("kick-client-secret", loaded.Chat.KickClientSecret);
        Assert.True(loaded.Chat.KickSendAsBot);
        Assert.Equal(ChatLayout.Docked, loaded.Chat.Layout);
        Assert.Equal(@"C:\Tools\vlc-overlay", loaded.Chat.VlcOverlayDirectory);
        Assert.Equal(16d, loaded.Chat.VlcOverlayFontSize);
        Assert.SequenceEqual(new[] { "xqc", "some-channel" }, loaded.FollowedChannels.KickChannelSlugs);
        Assert.Equal(false, loaded.FollowedChannels.NotifyWhenLive);
        Assert.Equal(1, loaded.RecentStreams.Count);
        Assert.Equal(PlatformKind.Twitch, loaded.RecentStreams[0].Platform);
        Assert.Equal("albralelie", loaded.RecentStreams[0].Channel);
        Assert.Equal("https://www.twitch.tv/albralelie", loaded.RecentStreams[0].Url);
        Assert.Equal("https://static-cdn.jtvnw.net/previews-ttv/live_user_albralelie-440x248.jpg", loaded.RecentStreams[0].ThumbnailUrl);
        Assert.Equal("720p60", loaded.RecentStreams[0].LastQuality);
        Assert.Equal(new DateTimeOffset(2026, 5, 30, 20, 0, 0, TimeSpan.Zero), loaded.RecentStreams[0].LastWatchedAtUtc);
        Assert.Equal(37, loaded.StreamVolumes["Twitch:xqc"]);
        Assert.Equal(54, loaded.StreamVolumes["Kick:xqc"]);
        Assert.Equal(22d, loaded.StreamVlcOverlayFontSizes["Twitch:xqc"]);
        Assert.Equal(18d, loaded.StreamVlcOverlayFontSizes["Kick:xqc"]);
        Assert.Equal(false, loaded.StreamPictureInPictureTopBarVisibility["TWITCH:XQC"]);
        Assert.True(loaded.StreamPictureInPictureTopBarVisibility["kick:XQC"]);
        Assert.NotNull(loaded.PictureInPictureWindowLocation);
        Assert.Equal(240d, loaded.PictureInPictureWindowLocation!.Left);
        Assert.Equal(320d, loaded.PictureInPictureWindowLocation.Top);
        Assert.Equal(640d, loaded.PictureInPictureWindowLocation.Width);
        Assert.Equal(360d, loaded.PictureInPictureWindowLocation.Height);
        Assert.True(loaded.PictureInPictureWindowLocation.IsFullscreen);
        Assert.Equal(PictureInPictureFullscreenMode.MultiView, loaded.PictureInPictureWindowLocation.FullscreenMode);
        Assert.NotNull(loaded.PictureInPictureWindowLocation.FullscreenScreen);
        Assert.Equal(@"\\.\DISPLAY7", loaded.PictureInPictureWindowLocation.FullscreenScreen!.DeviceName);
        Assert.Equal(10d, loaded.PictureInPictureWindowLocation.FullscreenScreen.Left);
        Assert.Equal(20d, loaded.PictureInPictureWindowLocation.FullscreenScreen.Top);
        Assert.Equal(1920d, loaded.PictureInPictureWindowLocation.FullscreenScreen.Width);
        Assert.Equal(1080d, loaded.PictureInPictureWindowLocation.FullscreenScreen.Height);
    }),
    ("legacy JSON settings keep Windows toast notifications enabled", async () =>
    {
        var temp = Path.Combine(Path.GetTempPath(), $"svs-settings-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(
                temp,
                """
                {
                  "FollowedChannels": {
                    "KickChannelSlugs": ["xqc"]
                  }
                }
                """);

            var loaded = await new JsonSettingsService(temp).LoadAsync();

            Assert.True(loaded.FollowedChannels.NotifyWhenLive);
            Assert.SequenceEqual(new[] { "xqc" }, loaded.FollowedChannels.KickChannelSlugs);
        }
        finally
        {
            File.Delete(temp);
        }
    }),
    ("picture-in-picture top bar settings normalize keys case-insensitively", () =>
    {
        var settings = new AppSettings
        {
            StreamPictureInPictureTopBarVisibility = new Dictionary<string, bool>
            {
                ["  Twitch:SomeChannel  "] = false,
                ["Kick:SomeChannel"] = true,
                ["   "] = false
            }
        };

        Assert.Equal(2, settings.StreamPictureInPictureTopBarVisibility.Count);
        Assert.Equal(false, settings.StreamPictureInPictureTopBarVisibility["twitch:somechannel"]);
        Assert.True(settings.StreamPictureInPictureTopBarVisibility["KICK:SOMECHANNEL"]);
        Assert.Equal(false, settings.StreamPictureInPictureTopBarVisibility.ContainsKey(""));

        settings.StreamPictureInPictureTopBarVisibility = null!;
        Assert.Equal(0, settings.StreamPictureInPictureTopBarVisibility.Count);
        return Task.CompletedTask;
    }),
    ("chat font size settings clamp invalid values", () =>
    {
        var chat = new ChatSettings
        {
            FontSize = double.NaN,
            VlcOverlayFontSize = 100
        };
        var settings = new AppSettings
        {
            StreamVlcOverlayFontSizes = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                ["Twitch:xqc"] = 100,
                ["Kick:xqc"] = double.NaN
            }
        };

        Assert.Equal(ChatSettings.DefaultFontSize, chat.FontSize);
        Assert.Equal(ChatSettings.MaximumFontSize, chat.VlcOverlayFontSize);
        Assert.Equal(ChatSettings.MaximumFontSize, settings.StreamVlcOverlayFontSizes["Twitch:xqc"]);
        Assert.Equal(false, settings.StreamVlcOverlayFontSizes.ContainsKey("Kick:xqc"));

        chat.VlcOverlayFontSize = 0;
        Assert.Equal(ChatSettings.MinimumFontSize, chat.VlcOverlayFontSize);
        return Task.CompletedTask;
    }),
    ("dock width settings clamp invalid values", () =>
    {
        var chat = new ChatSettings
        {
            DockWidth = ChatSettings.MinimumDockWidth - 1
        };
        Assert.Equal(ChatSettings.MinimumDockWidth, chat.DockWidth);

        chat.DockWidth = ChatSettings.MaximumDockWidth + 1;
        Assert.Equal(ChatSettings.MaximumDockWidth, chat.DockWidth);

        chat.DockWidth = double.NaN;
        Assert.Equal(ChatSettings.DefaultDockWidth, chat.DockWidth);

        chat.DockWidth = double.PositiveInfinity;
        Assert.Equal(ChatSettings.DefaultDockWidth, chat.DockWidth);

        Assert.Equal(ChatSettings.DefaultDockWidth, ChatSettings.NormalizeDockWidth(double.NegativeInfinity));
        return Task.CompletedTask;
    }),
    ("canceled JSON settings save leaves previous settings file intact", async () =>
    {
        var temp = Path.Combine(Path.GetTempPath(), $"svs-settings-{Guid.NewGuid():N}.json");
        try
        {
            var service = new JsonSettingsService(temp);
            await service.SaveAsync(new AppSettings { DefaultQuality = "best" });

            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                service.SaveAsync(new AppSettings { DefaultQuality = "worst" }, cancellation.Token));

            var loaded = await service.LoadAsync();
            Assert.Equal("best", loaded.DefaultQuality);
        }
        finally
        {
            File.Delete(temp);
        }
    }),
    ("malformed JSON settings are preserved and defaults are loaded", async () =>
    {
        var directory = Path.Combine(Path.GetTempPath(), "StreamlinkVlcStudioTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var settingsPath = Path.Combine(directory, "settings.json");
        var malformed = """{"DefaultPlatform":"not-a-platform"}""";

        try
        {
            await File.WriteAllTextAsync(settingsPath, malformed);
            var service = new JsonSettingsService(settingsPath);

            var loaded = await service.LoadAsync();
            var backups = Directory.GetFiles(directory, "settings.json.invalid-*");

            Assert.Equal(PlatformKind.Twitch, loaded.DefaultPlatform);
            Assert.Equal("best", loaded.DefaultQuality);
            Assert.Equal(false, File.Exists(settingsPath));
            Assert.Equal(1, backups.Length);
            Assert.Equal(malformed, await File.ReadAllTextAsync(backups[0]));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }),
    ("main view model unsubscribes logger on dispose", async () =>
    {
        var settings = new AppSettings();
        var logger = new MemoryLogger();
        var viewModel = TestViewModels.CreateMain(
            settings,
            new FakeSettingsService(settings),
            new FakeStreamlinkService(),
            new FakePlaybackEngineFactory(),
            new FakeChatClientFactory(),
            logger,
            action => action());

        viewModel.Initialize();
        logger.Write(AppLogLevel.Info, "Test", "before dispose");
        Assert.Equal(1, viewModel.AppLogLines.Count);

        await viewModel.DisposeAsync();
        logger.Write(AppLogLevel.Info, "Test", "after dispose");
        Assert.Equal(1, viewModel.AppLogLines.Count);
    }),
    ("clear Twitch token command reports settings save failures", async () =>
    {
        var settings = new AppSettings();
        settings.Chat.TwitchOAuthToken = "oauth:twitch-token";
        var settingsService = new FakeSettingsService(settings)
        {
            SaveException = new IOException("settings write failed")
        };
        var logger = new MemoryLogger();
        var viewModel = TestViewModels.CreateMain(
            settings,
            settingsService,
            new FakeStreamlinkService(),
            new FakePlaybackEngineFactory(),
            new FakeChatClientFactory(),
            logger,
            action => action());

        await viewModel.ClearTwitchTokenCommand.ExecuteAsync();

        Assert.Contains("settings write failed", viewModel.StatusMessage);
        Assert.True(logger.Entries.Any(entry =>
            entry.Level == AppLogLevel.Warning &&
            entry.Source == "TwitchOAuth" &&
            entry.Message.Contains("Failed to clear Twitch token", StringComparison.Ordinal)));
        await viewModel.DisposeAsync();
    }),
    ("resolves quoted Streamlink environment path", () =>
    {
        var previous = Environment.GetEnvironmentVariable("STREAMLINK_PATH");
        var directory = Path.Combine(Path.GetTempPath(), "StreamlinkVlcStudioTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var streamlinkPath = Path.Combine(directory, "streamlink.exe");
        File.WriteAllText(streamlinkPath, "");

        try
        {
            Environment.SetEnvironmentVariable("STREAMLINK_PATH", $"\"{streamlinkPath}\"");
            Assert.Equal(streamlinkPath, ExecutableResolver.FindStreamlink());
        }
        finally
        {
            Environment.SetEnvironmentVariable("STREAMLINK_PATH", previous);
            Directory.Delete(directory, recursive: true);
        }

        return Task.CompletedTask;
    }),
    ("resolves VLC directory from root or plugin environment path", () =>
    {
        var previous = Environment.GetEnvironmentVariable("VLC_PLUGIN_PATH");
        var directory = Path.Combine(Path.GetTempPath(), "StreamlinkVlcStudioTests", Guid.NewGuid().ToString("N"));
        var vlcDirectory = Path.Combine(directory, "VLC");
        var pluginDirectory = Path.Combine(vlcDirectory, "plugins");
        Directory.CreateDirectory(pluginDirectory);
        File.WriteAllText(Path.Combine(vlcDirectory, "libvlc.dll"), "");

        try
        {
            Environment.SetEnvironmentVariable("VLC_PLUGIN_PATH", $"\"{vlcDirectory}\"");
            Assert.Equal(vlcDirectory, ExecutableResolver.FindVlcDirectory());

            Environment.SetEnvironmentVariable("VLC_PLUGIN_PATH", $"\"{pluginDirectory}\"");
            Assert.Equal(vlcDirectory, ExecutableResolver.FindVlcDirectory());
        }
        finally
        {
            Environment.SetEnvironmentVariable("VLC_PLUGIN_PATH", previous);
            Directory.Delete(directory, recursive: true);
        }

        return Task.CompletedTask;
    }),
    ("recent streams are normalized newest first and unique per channel", () =>
    {
        var settings = new AppSettings();

        settings.RecentStreams =
        [
            new RecentStreamSettings
            {
                Platform = PlatformKind.Kick,
                Channel = "xqc",
                ThumbnailUrl = "//files.kick.com/xqc.jpg",
                LastWatchedAtUtc = new DateTimeOffset(2026, 5, 30, 19, 0, 0, TimeSpan.Zero)
            },
            new RecentStreamSettings
            {
                Platform = PlatformKind.Twitch,
                Channel = "albralelie",
                DisplayName = "Albralelie",
                ThumbnailUrl = " https://static-cdn.jtvnw.net/previews-ttv/live_user_albralelie-440x248.jpg ",
                LastQuality = "1080p",
                LastWatchedAtUtc = new DateTimeOffset(2026, 5, 30, 21, 0, 0, TimeSpan.Zero)
            },
            new RecentStreamSettings
            {
                Platform = PlatformKind.Twitch,
                Channel = "ALBRALELIE",
                LastWatchedAtUtc = new DateTimeOffset(2026, 5, 30, 18, 0, 0, TimeSpan.Zero)
            }
        ];

        Assert.Equal(2, settings.RecentStreams.Count);
        Assert.Equal(PlatformKind.Twitch, settings.RecentStreams[0].Platform);
        Assert.Equal("albralelie", settings.RecentStreams[0].Channel);
        Assert.Equal("Albralelie", settings.RecentStreams[0].DisplayName);
        Assert.Equal("1080p", settings.RecentStreams[0].LastQuality);
        Assert.Equal("https://www.twitch.tv/albralelie", settings.RecentStreams[0].Url);
        Assert.Equal("https://static-cdn.jtvnw.net/previews-ttv/live_user_albralelie-440x248.jpg", settings.RecentStreams[0].ThumbnailUrl);
        Assert.Equal(PlatformKind.Kick, settings.RecentStreams[1].Platform);
        Assert.Equal("xqc", settings.RecentStreams[1].Channel);
        Assert.Equal("https://kick.com/xqc", settings.RecentStreams[1].Url);
        Assert.Equal("https://files.kick.com/xqc.jpg", settings.RecentStreams[1].ThumbnailUrl);
        return Task.CompletedTask;
    }),
    ("validates Twitch OAuth token scopes", async () =>
    {
        using var httpClient = new HttpClient(new FakeHttpMessageHandler(request =>
        {
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("token-value", request.Headers.Authorization?.Parameter);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                {
                  "client_id": "twitch-client-id",
                  "user_id": "141981764",
                  "login": "MyBot",
                  "scopes": ["chat:read", "chat:edit", "user:read:follows", "channel:manage:predictions"],
                  "expires_in": 3600
                }
                """)
            };
        }));

        var token = await TwitchOAuthService.ValidateTokenAsync(httpClient, "oauth:token-value");

        Assert.Equal("mybot", token.Login);
        Assert.Equal("141981764", token.UserId);
        Assert.Equal("twitch-client-id", token.ClientId);
        Assert.True(token.CanReadChat);
        Assert.True(token.CanWriteChat);
        Assert.True(token.CanReadFollows);
        Assert.True(token.CanManagePredictions);
        Assert.NotNull(token.ExpiresAtUtc);
    }),
    ("creates Twitch prediction through Helix with documented JSON body", async () =>
    {
        using var httpClient = new HttpClient(new FakeHttpMessageHandler(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("api.twitch.tv", request.RequestUri!.Host);
            Assert.Equal("/helix/predictions", request.RequestUri.AbsolutePath);
            Assert.Equal("Bearer twitch-token", request.Headers.Authorization?.ToString());
            Assert.SequenceEqual(["twitch-client-id"], request.Headers.GetValues("Client-Id"));

            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            using var bodyDocument = JsonDocument.Parse(body);
            var root = bodyDocument.RootElement;
            Assert.Equal("141981764", root.GetProperty("broadcaster_id").GetString());
            Assert.Equal("Will this work?", root.GetProperty("title").GetString());
            Assert.Equal(60, root.GetProperty("prediction_window").GetInt32());
            Assert.SequenceEqual(
                ["Yes", "No"],
                root.GetProperty("outcomes").EnumerateArray().Select(item => item.GetProperty("title").GetString() ?? ""));

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                {
                  "data": [
                    {
                      "id": "prediction-1",
                      "broadcaster_id": "141981764",
                      "broadcaster_login": "streamer",
                      "broadcaster_name": "Streamer",
                      "title": "Will this work?",
                      "outcomes": [
                        { "id": "outcome-1", "title": "Yes", "color": "BLUE", "users": 0, "channel_points": 0, "top_predictors": null },
                        { "id": "outcome-2", "title": "No", "color": "PINK", "users": 0, "channel_points": 0, "top_predictors": null }
                      ],
                      "prediction_window": 60,
                      "status": "ACTIVE",
                      "created_at": "2026-06-03T12:00:00.123456789Z",
                      "locked_at": null,
                      "ended_at": null
                    }
                  ],
                  "pagination": {}
                }
                """, Encoding.UTF8, "application/json")
            };
        }));
        var client = new TwitchPredictionApiClient(httpClient);

        var prediction = await client.CreatePredictionAsync(
            "141981764",
            new TwitchPredictionCreateRequest(" Will this work? ", [" Yes ", "No"], 60),
            "oauth:twitch-token",
            "twitch-client-id");

        Assert.Equal("prediction-1", prediction.Id);
        Assert.Equal(TwitchPredictionStatus.Active, prediction.Status);
        Assert.Equal(2, prediction.Outcomes.Count);
        Assert.Equal("Yes", prediction.Outcomes[0].Title);
        Assert.NotNull(prediction.StartedAtUtc);
    }),
    ("resolves Twitch prediction through Helix with winning outcome body", async () =>
    {
        using var httpClient = new HttpClient(new FakeHttpMessageHandler(request =>
        {
            Assert.Equal(HttpMethod.Patch, request.Method);
            Assert.Equal("api.twitch.tv", request.RequestUri!.Host);
            Assert.Equal("/helix/predictions", request.RequestUri.AbsolutePath);
            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            using var bodyDocument = JsonDocument.Parse(body);
            var root = bodyDocument.RootElement;
            Assert.Equal("141981764", root.GetProperty("broadcaster_id").GetString());
            Assert.Equal("prediction-1", root.GetProperty("id").GetString());
            Assert.Equal("RESOLVED", root.GetProperty("status").GetString());
            Assert.Equal("outcome-1", root.GetProperty("winning_outcome_id").GetString());

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                {
                  "data": [
                    {
                      "id": "prediction-1",
                      "broadcaster_id": "141981764",
                      "broadcaster_login": "streamer",
                      "broadcaster_name": "Streamer",
                      "title": "Will this work?",
                      "winning_outcome_id": "outcome-1",
                      "outcomes": [
                        {
                          "id": "outcome-1",
                          "title": "Yes",
                          "color": "BLUE",
                          "users": 2,
                          "channel_points": 1500,
                          "top_predictors": [
                            {
                              "user_id": "user-1",
                              "user_login": "winner",
                              "user_name": "Winner",
                              "channel_points_used": 1000,
                              "channel_points_won": 1300
                            }
                          ]
                        }
                      ],
                      "prediction_window": 60,
                      "status": "RESOLVED",
                      "created_at": "2026-06-03T12:00:00Z",
                      "locked_at": "2026-06-03T12:00:30Z",
                      "ended_at": "2026-06-03T12:01:00Z"
                    }
                  ]
                }
                """, Encoding.UTF8, "application/json")
            };
        }));
        var client = new TwitchPredictionApiClient(httpClient);

        var prediction = await client.ResolvePredictionAsync(
            "141981764",
            "prediction-1",
            "outcome-1",
            "twitch-token",
            "twitch-client-id");

        Assert.Equal(TwitchPredictionStatus.Resolved, prediction.Status);
        Assert.Equal("outcome-1", prediction.WinningOutcomeId);
        Assert.Equal(1, prediction.Outcomes[0].TopPredictors.Count);
        Assert.Equal(1300, prediction.Outcomes[0].TopPredictors[0].ChannelPointsWon);
    }),
    ("validates Twitch prediction documented limits before Helix calls", async () =>
    {
        var requestCount = 0;
        using var httpClient = new HttpClient(new FakeHttpMessageHandler(_ =>
        {
            requestCount++;
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));
        var client = new TwitchPredictionApiClient(httpClient);

        await Assert.ThrowsAsync<ArgumentException>(() => client.CreatePredictionAsync(
            "141981764",
            new TwitchPredictionCreateRequest(new string('x', 46), ["Yes", "No"], 60),
            "twitch-token",
            "twitch-client-id"));
        await Assert.ThrowsAsync<ArgumentException>(() => client.CreatePredictionAsync(
            "141981764",
            new TwitchPredictionCreateRequest("Question?", ["Only one"], 60),
            "twitch-token",
            "twitch-client-id"));
        await Assert.ThrowsAsync<ArgumentException>(() => client.CreatePredictionAsync(
            "141981764",
            new TwitchPredictionCreateRequest("Question?", ["Yes", new string('x', 26)], 60),
            "twitch-token",
            "twitch-client-id"));
        await Assert.ThrowsAsync<ArgumentException>(() => client.CreatePredictionAsync(
            "141981764",
            new TwitchPredictionCreateRequest("Question?", ["Yes", "No"], 29),
            "twitch-token",
            "twitch-client-id"));

        Assert.Equal(0, requestCount);
    }),
    ("maps Twitch prediction API error messages", async () =>
    {
        using var httpClient = new HttpClient(new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("""{"error":"Bad Request","message":"A prediction is already active."}""")
            }));
        var client = new TwitchPredictionApiClient(httpClient);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.LockPredictionAsync(
            "141981764",
            "prediction-1",
            "twitch-token",
            "twitch-client-id"));

        Assert.Contains("A prediction is already active.", exception.Message);
    }),
    ("parses Twitch prediction EventSub payloads and ignores duplicate message IDs", () =>
    {
        var parser = new TwitchPredictionEventSubParser();
        var begin = """
        {
          "metadata": {
            "message_id": "message-1",
            "message_type": "notification",
            "message_timestamp": "2026-06-03T12:00:00Z"
          },
          "payload": {
            "subscription": { "type": "channel.prediction.begin" },
            "event": {
              "id": "prediction-1",
              "broadcaster_user_id": "141981764",
              "broadcaster_user_login": "streamer",
              "broadcaster_user_name": "Streamer",
              "title": "Will this work?",
              "outcomes": [
                { "id": "outcome-1", "title": "Yes", "color": "blue", "users": 1, "channel_points": 100, "top_predictors": [] },
                { "id": "outcome-2", "title": "No", "color": "pink", "users": 0, "channel_points": 0, "top_predictors": null }
              ],
              "started_at": "2026-06-03T12:00:00.123456789Z",
              "locks_at": "2026-06-03T12:02:00.123456789Z"
            }
          }
        }
        """;

        Assert.True(parser.TryParse(begin, out var beginMessage));
        Assert.Equal(false, beginMessage.IsDuplicate);
        Assert.NotNull(beginMessage.Prediction);
        Assert.Equal(TwitchPredictionStatus.Active, beginMessage.Prediction!.Status);
        Assert.Equal("prediction-1", beginMessage.Prediction.Id);
        Assert.Equal(120, beginMessage.Prediction.PredictionWindowSeconds);

        Assert.True(parser.TryParse(begin, out var duplicateMessage));
        Assert.Equal(true, duplicateMessage.IsDuplicate);
        Assert.Equal<TwitchPrediction?>(null, duplicateMessage.Prediction);

        var end = """
        {
          "metadata": {
            "message_id": "message-2",
            "message_type": "notification",
            "message_timestamp": "2026-06-03T12:03:00Z"
          },
          "payload": {
            "subscription": { "type": "channel.prediction.end" },
            "event": {
              "id": "prediction-1",
              "broadcaster_user_id": "141981764",
              "broadcaster_user_login": "streamer",
              "broadcaster_user_name": "Streamer",
              "title": "Will this work?",
              "winning_outcome_id": "outcome-1",
              "outcomes": [
                {
                  "id": "outcome-1",
                  "title": "Yes",
                  "color": "blue",
                  "users": 2,
                  "channel_points": 1500,
                  "top_predictors": [
                    {
                      "user_id": "user-1",
                      "user_login": "winner",
                      "user_name": "Winner",
                      "channel_points_used": 1000,
                      "channel_points_won": 1300
                    }
                  ]
                }
              ],
              "status": "resolved",
              "started_at": "2026-06-03T12:00:00Z",
              "ended_at": "2026-06-03T12:03:00Z"
            }
          }
        }
        """;

        Assert.True(parser.TryParse(end, out var endMessage));
        Assert.NotNull(endMessage.Prediction);
        Assert.Equal(TwitchPredictionStatus.Resolved, endMessage.Prediction!.Status);
        Assert.Equal("outcome-1", endMessage.Prediction.WinningOutcomeId);
        Assert.Equal(1, endMessage.Prediction.Outcomes[0].TopPredictors.Count);
        Assert.Equal(1300, endMessage.Prediction.Outcomes[0].TopPredictors[0].ChannelPointsWon);
        return Task.CompletedTask;
    }),
    ("ignores malformed Twitch prediction EventSub payloads", () =>
    {
        var parser = new TwitchPredictionEventSubParser();

        Assert.Equal(false, parser.TryParse("{", out var message));
        Assert.Equal(TwitchEventSubMessage.Empty, message);
        return Task.CompletedTask;
    }),
    ("parses Twitch prediction EventSub welcome and reconnect sessions", () =>
    {
        var parser = new TwitchPredictionEventSubParser();
        const string welcome = """
        {
          "metadata": { "message_id": "welcome-1", "message_type": "session_welcome" },
          "payload": {
            "session": {
              "id": "session-1",
              "keepalive_timeout_seconds": 10,
              "reconnect_url": null
            }
          }
        }
        """;
        const string reconnect = """
        {
          "metadata": { "message_id": "reconnect-1", "message_type": "session_reconnect" },
          "payload": {
            "session": {
              "id": "session-1",
              "keepalive_timeout_seconds": null,
              "reconnect_url": "wss://eventsub.wss.twitch.tv/ws?reconnect=1"
            }
          }
        }
        """;

        Assert.True(parser.TryParse(welcome, out var welcomeMessage));
        Assert.Equal("session-1", welcomeMessage.SessionId);
        Assert.Equal<int?>(10, welcomeMessage.KeepaliveTimeoutSeconds);

        Assert.True(parser.TryParse(reconnect, out var reconnectMessage));
        Assert.Equal("wss://eventsub.wss.twitch.tv/ws?reconnect=1", reconnectMessage.ReconnectUrl);
        return Task.CompletedTask;
    }),
    ("Twitch prediction cards only show open predictions without changing raw chat", async () =>
    {
        var settings = new AppSettings();
        var chatFactory = new FakeChatClientFactory();
        var tab = TestViewModels.CreateTab(
            StreamInputParser.Parse("streamer", PlatformKind.Twitch),
            "best",
            new FakeStreamlinkService(),
            new FakePlaybackEngineFactory(),
            chatFactory,
            new MemoryLogger(),
            action => action());

        await tab.RestartChatAsync(settings);
        chatFactory.Client.SetPredictionAccess(new TwitchPredictionAccessState(
            true,
            true,
            "Prediction controls enabled.",
            "broadcaster-1",
            "streamer",
            "broadcaster-1"));

        var active = CreateTestPrediction(
            "prediction-1",
            TwitchPredictionStatus.Active,
            "Will this work?",
            CreateTestPredictionOutcomes());

        chatFactory.Client.ReceivePrediction(active);
        chatFactory.Client.ReceivePrediction(active with
        {
            Outcomes =
            [
                new TwitchPredictionOutcome("outcome-1", "Yes", "blue", 2, 250, []),
                new TwitchPredictionOutcome("outcome-2", "No", "pink", 1, 50, [])
            ]
        });

        var cards = tab.DockedChatFeedItems.OfType<TwitchPredictionFeedItemViewModel>().ToArray();
        Assert.Equal(1, cards.Length);
        Assert.Equal("prediction-1", cards[0].PredictionId);
        Assert.Contains("300", cards[0].TotalText);
        Assert.Equal(true, cards[0].IsOpen);
        Assert.Equal(0, tab.ChatMessages.Count);
        Assert.Equal(0, tab.DockedChatMessages.Count);

        chatFactory.Client.ReceivePrediction(active with
        {
            Status = TwitchPredictionStatus.Locked,
            LocksAtUtc = DateTimeOffset.UtcNow
        });
        cards = tab.DockedChatFeedItems.OfType<TwitchPredictionFeedItemViewModel>().ToArray();
        Assert.Equal(1, cards.Length);
        Assert.Equal("prediction-1", cards[0].PredictionId);
        Assert.Equal(true, cards[0].IsOpen);
        Assert.Equal("Locked", cards[0].StatusText);

        chatFactory.Client.ReceivePrediction(active with
        {
            WinningOutcomeId = "outcome-1",
            Status = TwitchPredictionStatus.Resolved,
            EndedAtUtc = DateTimeOffset.UtcNow
        });
        tab.TwitchPredictionTitle = "Next one?";
        cards = tab.DockedChatFeedItems.OfType<TwitchPredictionFeedItemViewModel>().ToArray();

        Assert.Equal(0, cards.Length);
        Assert.Equal(true, tab.CanStartTwitchPrediction);

        var canceling = CreateTestPrediction(
            "prediction-2",
            TwitchPredictionStatus.Active,
            "Cancel this?",
            CreateTestPredictionOutcomes());
        chatFactory.Client.ReceivePrediction(canceling);
        cards = tab.DockedChatFeedItems.OfType<TwitchPredictionFeedItemViewModel>().ToArray();
        Assert.Equal(1, cards.Length);
        Assert.Equal("prediction-2", cards[0].PredictionId);

        chatFactory.Client.ReceivePrediction(canceling with
        {
            Status = TwitchPredictionStatus.Canceled,
            EndedAtUtc = DateTimeOffset.UtcNow
        });
        cards = tab.DockedChatFeedItems.OfType<TwitchPredictionFeedItemViewModel>().ToArray();
        Assert.Equal(0, cards.Length);

        chatFactory.Client.ReceivePrediction(CreateTestPrediction(
            "prediction-3",
            TwitchPredictionStatus.Unknown,
            "Already closed?",
            CreateTestPredictionOutcomes()));
        cards = tab.DockedChatFeedItems.OfType<TwitchPredictionFeedItemViewModel>().ToArray();
        Assert.Equal(0, cards.Length);
        Assert.Equal(0, tab.ChatMessages.Count);
        Assert.Equal(0, tab.DockedChatMessages.Count);

        await tab.DisposeAsync();
    }),
    ("Twitch prediction active ID replaces stale prediction card", async () =>
    {
        var settings = new AppSettings();
        var chatFactory = new FakeChatClientFactory();
        var tab = TestViewModels.CreateTab(
            StreamInputParser.Parse("streamer", PlatformKind.Twitch),
            "best",
            new FakeStreamlinkService(),
            new FakePlaybackEngineFactory(),
            chatFactory,
            new MemoryLogger(),
            action => action());

        await tab.RestartChatAsync(settings);
        chatFactory.Client.SetPredictionAccess(new TwitchPredictionAccessState(
            true,
            true,
            "Prediction controls enabled.",
            "broadcaster-1",
            "streamer",
            "broadcaster-1"));

        chatFactory.Client.ReceivePrediction(CreateTestPrediction(
            "prediction-1",
            TwitchPredictionStatus.Active,
            "First one?",
            CreateTestPredictionOutcomes()));
        chatFactory.Client.ReceivePrediction(CreateTestPrediction(
            "prediction-2",
            TwitchPredictionStatus.Active,
            "Second one?",
            CreateTestPredictionOutcomes(2, 200, 1, 100)));

        var cards = tab.DockedChatFeedItems.OfType<TwitchPredictionFeedItemViewModel>().ToArray();
        Assert.Equal(1, cards.Length);
        Assert.Equal("prediction-2", cards[0].PredictionId);
        Assert.Contains("300", cards[0].TotalText);
        Assert.Equal(0, tab.ChatMessages.Count);
        Assert.Equal(0, tab.DockedChatMessages.Count);
        await tab.DisposeAsync();
    }),
    ("Twitch prediction start command requires manage access", async () =>
    {
        var settings = new AppSettings();
        var chatFactory = new FakeChatClientFactory();
        var tab = TestViewModels.CreateTab(
            StreamInputParser.Parse("streamer", PlatformKind.Twitch),
            "best",
            new FakeStreamlinkService(),
            new FakePlaybackEngineFactory(),
            chatFactory,
            new MemoryLogger(),
            action => action());

        tab.TwitchPredictionTitle = "Will this work?";
        await tab.RestartChatAsync(settings);

        Assert.Equal(false, tab.CanStartTwitchPrediction);

        chatFactory.Client.SetPredictionAccess(new TwitchPredictionAccessState(
            true,
            false,
            "Reconnect Twitch to grant channel:manage:predictions."));
        Assert.Equal(false, tab.CanStartTwitchPrediction);

        chatFactory.Client.SetPredictionAccess(new TwitchPredictionAccessState(
            true,
            true,
            "Prediction controls enabled.",
            "broadcaster-1",
            "streamer",
            "broadcaster-1"));
        Assert.Equal(true, tab.CanStartTwitchPrediction);

        await tab.StartTwitchPredictionCommand.ExecuteAsync();

        Assert.Equal(1, chatFactory.Client.PredictionCreateRequests.Count);
        Assert.Equal("Will this work?", chatFactory.Client.PredictionCreateRequests[0].Title);
        Assert.SequenceEqual(["Yes", "No"], chatFactory.Client.PredictionCreateRequests[0].Outcomes);
        Assert.Equal(false, tab.ChatMessages.Any(message => message.Message == "Will this work?"));
        await tab.DisposeAsync();
    }),
    ("explains Twitch Client ID is not an OAuth token", async () =>
    {
        using var httpClient = new HttpClient(new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.Unauthorized)));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => TwitchOAuthService.ValidateTokenAsync(httpClient, "client-id-value"));

        Assert.Contains("Client ID alone cannot send chat", exception.Message);
    }),
    ("gets Twitch viewer count from Helix streams", async () =>
    {
        var settings = new AppSettings();
        settings.Chat.TwitchClientId = "twitch-client-id";
        settings.Chat.TwitchOAuthToken = "oauth:viewer-count-token";
        using var httpClient = new HttpClient(new FakeHttpMessageHandler(request =>
        {
            if (request.RequestUri!.Host == "id.twitch.tv")
            {
                Assert.Equal("Bearer viewer-count-token", request.Headers.Authorization?.ToString());
                return CreateTwitchTokenValidationResponse("twitch-client-id");
            }

            Assert.Equal("api.twitch.tv", request.RequestUri.Host);
            Assert.Equal("/helix/streams", request.RequestUri.AbsolutePath);
            Assert.Contains("user_login=summit1g", request.RequestUri.Query);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("viewer-count-token", request.Headers.Authorization?.Parameter);
            Assert.SequenceEqual(["twitch-client-id"], request.Headers.GetValues("Client-Id"));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                {
                  "data": [
                    {
                      "user_login": "summit1g",
                      "viewer_count": 1234,
                      "game_name": "Just Chatting",
                      "title": "Late-night ranked grind"
                    }
                  ]
                }
                """)
            };
        }));

        var service = new ViewerCountService(new MemoryLogger(), httpClient);
        var result = await service.GetViewerCountAsync(
            StreamInputParser.Parse("summit1g", PlatformKind.Twitch),
            settings);

        Assert.Equal(ViewerCountState.Available, result.State);
        Assert.Equal(1234, result.ViewerCount);
        Assert.Equal("Just Chatting", result.CategoryName);
        Assert.Equal("Late-night ranked grind", result.StreamTitle);
    }),
    ("reports an empty Twitch category when the channel has none set", async () =>
    {
        var settings = new AppSettings();
        settings.Chat.TwitchClientId = "twitch-client-id";
        settings.Chat.TwitchOAuthToken = "oauth:viewer-empty-category-token";
        using var httpClient = new HttpClient(new FakeHttpMessageHandler(request =>
        {
            if (request.RequestUri!.Host == "id.twitch.tv")
            {
                return CreateTwitchTokenValidationResponse("twitch-client-id");
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                {
                  "data": [
                    {
                      "user_login": "summit1g",
                      "viewer_count": 1234,
                      "game_name": ""
                    }
                  ]
                }
                """)
            };
        }));

        var service = new ViewerCountService(new MemoryLogger(), httpClient);
        var result = await service.GetViewerCountAsync(
            StreamInputParser.Parse("summit1g", PlatformKind.Twitch),
            settings);

        Assert.Equal(ViewerCountState.Available, result.State);
        Assert.Equal("", result.CategoryName);
    }),
    ("treats empty Twitch streams response as offline", async () =>
    {
        var settings = new AppSettings();
        settings.Chat.TwitchClientId = "twitch-client-id";
        settings.Chat.TwitchOAuthToken = "viewer-offline-token";
        using var httpClient = new HttpClient(new FakeHttpMessageHandler(request =>
        {
            if (request.RequestUri!.Host == "id.twitch.tv")
            {
                return CreateTwitchTokenValidationResponse("twitch-client-id");
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"data":[]}""")
            };
        }));

        var service = new ViewerCountService(new MemoryLogger(), httpClient);
        var result = await service.GetViewerCountAsync(
            StreamInputParser.Parse("summit1g", PlatformKind.Twitch),
            settings);

        Assert.Equal(ViewerCountState.Offline, result.State);
        Assert.Equal<int?>(null, result.ViewerCount);
    }),
    ("gets Kick viewer count from channel stream data", async () =>
    {
        var settings = new AppSettings();
        settings.Chat.KickOAuthToken = "Bearer kick-token";
        using var httpClient = new HttpClient(new FakeHttpMessageHandler(request =>
        {
            Assert.Equal("api.kick.com", request.RequestUri!.Host);
            Assert.Equal("/public/v1/channels", request.RequestUri.AbsolutePath);
            Assert.Contains("slug=xqc", request.RequestUri.Query);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("kick-token", request.Headers.Authorization?.Parameter);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                {
                  "data": [
                    {
                      "slug": "xqc",
                      "category": {
                        "id": 15,
                        "name": "Grand Theft Auto V"
                      },
                      "stream": {
                        "is_live": true,
                        "viewer_count": 5678,
                        "stream_title": "Kick ranked grind"
                      }
                    }
                  ]
                }
                """)
            };
        }));

        var service = new ViewerCountService(new MemoryLogger(), httpClient);
        var result = await service.GetViewerCountAsync(
            StreamInputParser.Parse("https://kick.com/xqc", PlatformKind.Twitch),
            settings);

        Assert.Equal(ViewerCountState.Available, result.State);
        Assert.Equal(5678, result.ViewerCount);
        Assert.Equal("Grand Theft Auto V", result.CategoryName);
        Assert.Equal("Kick ranked grind", result.StreamTitle);
    }),
    ("reports an empty Kick category when the channel has none set", async () =>
    {
        var settings = new AppSettings();
        settings.Chat.KickOAuthToken = "Bearer kick-token";
        using var httpClient = new HttpClient(new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                {
                  "data": [
                    {
                      "slug": "xqc",
                      "category": null,
                      "stream": {
                        "is_live": true,
                        "viewer_count": 5678
                      }
                    }
                  ]
                }
                """)
            }));

        var service = new ViewerCountService(new MemoryLogger(), httpClient);
        var result = await service.GetViewerCountAsync(
            StreamInputParser.Parse("https://kick.com/xqc", PlatformKind.Twitch),
            settings);

        Assert.Equal(ViewerCountState.Available, result.State);
        Assert.Equal("", result.CategoryName);
    }),
    ("treats Kick null stream data as offline", async () =>
    {
        var settings = new AppSettings();
        settings.Chat.KickOAuthToken = "kick-token";
        using var httpClient = new HttpClient(new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                {
                  "data": [
                    {
                      "slug": "xqc",
                      "stream": null
                    }
                  ]
                }
                """)
            }));

        var service = new ViewerCountService(new MemoryLogger(), httpClient);
        var result = await service.GetViewerCountAsync(
            StreamInputParser.Parse("https://kick.com/xqc", PlatformKind.Twitch),
            settings);

        Assert.Equal(ViewerCountState.Offline, result.State);
        Assert.Equal<int?>(null, result.ViewerCount);
    }),
    ("gets Twitch stream metadata thumbnail from Helix streams", async () =>
    {
        var settings = new AppSettings();
        settings.Chat.TwitchClientId = "twitch-client-id";
        settings.Chat.TwitchOAuthToken = "oauth:metadata-token";
        using var httpClient = new HttpClient(new FakeHttpMessageHandler(request =>
        {
            if (request.RequestUri!.Host == "id.twitch.tv")
            {
                Assert.Equal("Bearer metadata-token", request.Headers.Authorization?.ToString());
                return CreateTwitchTokenValidationResponse("twitch-client-id");
            }

            if (request.RequestUri!.AbsolutePath == "/helix/users")
            {
                Assert.Contains("login=summit1g", request.RequestUri.Query);
                Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
                Assert.Equal("metadata-token", request.Headers.Authorization?.Parameter);
                Assert.SequenceEqual(["twitch-client-id"], request.Headers.GetValues("Client-Id"));
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                    {
                      "data": [
                        {
                          "login": "summit1g",
                          "profile_image_url": "https://static-cdn.jtvnw.net/jtv_user_pictures/summit-profile.png"
                        }
                      ]
                    }
                    """, Encoding.UTF8, "application/json")
                };
            }

            Assert.Equal("api.twitch.tv", request.RequestUri!.Host);
            Assert.Equal("/helix/streams", request.RequestUri.AbsolutePath);
            Assert.Contains("user_login=summit1g", request.RequestUri.Query);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("metadata-token", request.Headers.Authorization?.Parameter);
            Assert.SequenceEqual(["twitch-client-id"], request.Headers.GetValues("Client-Id"));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                {
                  "data": [
                    {
                      "user_login": "summit1g",
                      "user_name": "Summit1G",
                      "game_name": "Just Chatting",
                      "thumbnail_url": "https://static-cdn.jtvnw.net/previews-ttv/live_user_summit1g-{width}x{height}.jpg"
                    }
                  ]
                }
                """)
            };
        }));

        var service = new StreamMetadataService(new MemoryLogger(), httpClient);
        var result = await service.GetLiveStreamMetadataAsync(
            StreamInputParser.Parse("summit1g", PlatformKind.Twitch),
            settings);

        Assert.Equal(StreamMetadataState.Available, result.State);
        Assert.Equal("Summit1G", result.DisplayName);
        Assert.Equal("Just Chatting", result.CategoryName);
        Assert.Equal(
            "https://static-cdn.jtvnw.net/jtv_user_pictures/summit-profile.png",
            result.ProfileImageUrl);
        Assert.Contains("440x248", result.ThumbnailUrl);
        Assert.DoesNotContain("{width}", result.ThumbnailUrl);
    }),
    ("gets Kick stream metadata thumbnail from channel data", async () =>
    {
        var settings = new AppSettings();
        settings.Chat.KickOAuthToken = "Bearer kick-token";
        using var httpClient = new HttpClient(new FakeHttpMessageHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/public/v1/users")
            {
                Assert.Contains("id=12345", request.RequestUri.Query);
                Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
                Assert.Equal("kick-token", request.Headers.Authorization?.Parameter);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                    {
                      "data": [
                        {
                          "user_id": 12345,
                          "profile_picture": "//files.kick.com/xqc-profile.jpg"
                        }
                      ]
                    }
                    """, Encoding.UTF8, "application/json")
                };
            }

            Assert.Equal("api.kick.com", request.RequestUri!.Host);
            Assert.Equal("/public/v1/channels", request.RequestUri.AbsolutePath);
            Assert.Contains("slug=xqc", request.RequestUri.Query);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("kick-token", request.Headers.Authorization?.Parameter);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                {
                  "data": [
                    {
                      "broadcaster_user_id": 12345,
                      "slug": "xqc",
                      "thumbnail": "//files.kick.com/live.jpg",
                      "category": { "name": "IRL" },
                      "stream": {
                        "is_live": true,
                        "viewer_count": 5678
                      }
                    }
                  ]
                }
                """)
            };
        }));

        var service = new StreamMetadataService(new MemoryLogger(), httpClient);
        var result = await service.GetLiveStreamMetadataAsync(
            StreamInputParser.Parse("https://kick.com/xqc", PlatformKind.Twitch),
            settings);

        Assert.Equal(StreamMetadataState.Available, result.State);
        Assert.Equal("xqc", result.DisplayName);
        Assert.Equal("IRL", result.CategoryName);
        Assert.Equal("https://files.kick.com/xqc-profile.jpg", result.ProfileImageUrl);
        Assert.Equal("https://files.kick.com/live.jpg", result.ThumbnailUrl);
    }),
    ("gets Twitch followed live streams", async () =>
    {
        var settings = new AppSettings();
        settings.Chat.TwitchClientId = "twitch-client-id";
        settings.Chat.TwitchOAuthToken = "oauth:twitch-token";
        using var httpClient = new HttpClient(new FakeHttpMessageHandler(request =>
        {
            if (request.RequestUri!.Host == "id.twitch.tv")
            {
                Assert.Equal("/oauth2/validate", request.RequestUri.AbsolutePath);
                Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
                Assert.Equal("twitch-token", request.Headers.Authorization?.Parameter);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                    {
                      "client_id": "twitch-client-id",
                      "user_id": "141981764",
                      "login": "mybot",
                      "scopes": ["chat:read", "chat:edit", "user:read:follows"],
                      "expires_in": 3600
                    }
                    """)
                };
            }

            if (request.RequestUri.AbsolutePath == "/helix/users")
            {
                Assert.Contains("login=summit1g", request.RequestUri.Query);
                Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
                Assert.Equal("twitch-token", request.Headers.Authorization?.Parameter);
                Assert.SequenceEqual(["twitch-client-id"], request.Headers.GetValues("Client-Id"));
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                    {
                      "data": [
                        {
                          "login": "summit1g",
                          "profile_image_url": "https://static-cdn.jtvnw.net/jtv_user_pictures/summit-profile.png"
                        }
                      ]
                    }
                    """, Encoding.UTF8, "application/json")
                };
            }

            Assert.Equal("api.twitch.tv", request.RequestUri.Host);
            Assert.Equal("/helix/streams/followed", request.RequestUri.AbsolutePath);
            Assert.Contains("user_id=141981764", request.RequestUri.Query);
            Assert.Contains("first=100", request.RequestUri.Query);
            Assert.SequenceEqual(["twitch-client-id"], request.Headers.GetValues("Client-Id"));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                {
                  "data": [
                    {
                      "user_login": "summit1g",
                      "user_name": "summit1g",
                      "game_name": "Just Chatting",
                      "title": "live now",
                      "viewer_count": 12000,
                      "started_at": "2026-06-01T12:30:00Z",
                      "language": "en",
                      "thumbnail_url": "https://static-cdn.jtvnw.net/previews-ttv/live_user_summit1g-{width}x{height}.jpg",
                      "is_mature": false
                    }
                  ],
                  "pagination": {}
                }
                """)
            };
        }));

        var service = new FollowedStreamsService(new MemoryLogger(), httpClient);
        var result = await service.GetLiveFollowedStreamsAsync(settings);

        var stream = result.Streams.Single(item => item.Platform == PlatformKind.Twitch);
        Assert.Equal("summit1g", stream.Channel);
        Assert.Equal("live now", stream.Title);
        Assert.Equal("Just Chatting", stream.CategoryName);
        Assert.Equal(12000, stream.ViewerCount);
        Assert.Equal(
            "https://static-cdn.jtvnw.net/jtv_user_pictures/summit-profile.png",
            stream.ProfileImageUrl);
        var homeItem = new LiveStreamCardViewModel(
            LiveStreamCardData.FromFollowedStream(stream),
            (_, _) => Task.CompletedTask);
        Assert.Equal(
            "https://static-cdn.jtvnw.net/jtv_user_pictures/summit-profile.png",
            homeItem.ProfileImageUrl);
        Assert.Equal(LiveStreamCardSource.Followed, homeItem.Source);
        Assert.True(homeItem.HasProfileImage);
        Assert.Contains("440x248", stream.ThumbnailUrl);
        Assert.True(result.Messages.Any(message => message.StartsWith("Kick:", StringComparison.Ordinal)));
    }),
    ("Twitch followed streams stop after excessive pagination", async () =>
    {
        var settings = new AppSettings();
        settings.Chat.TwitchClientId = "twitch-client-id";
        settings.Chat.TwitchOAuthToken = "oauth:twitch-token";
        var streamPageRequests = 0;
        using var httpClient = new HttpClient(new FakeHttpMessageHandler(request =>
        {
            if (request.RequestUri!.Host == "id.twitch.tv")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                    {
                      "client_id": "twitch-client-id",
                      "user_id": "141981764",
                      "login": "mybot",
                      "scopes": ["user:read:follows"]
                    }
                    """)
                };
            }

            Assert.Equal("/helix/streams/followed", request.RequestUri.AbsolutePath);
            streamPageRequests++;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"{{\"data\":[],\"pagination\":{{\"cursor\":\"cursor-{streamPageRequests}\"}}}}")
            };
        }));

        var service = new FollowedStreamsService(new MemoryLogger(), httpClient);
        var result = await service.GetLiveFollowedStreamsAsync(settings);

        Assert.Equal(100, streamPageRequests);
        Assert.Equal(false, result.SucceededPlatforms?.Contains(PlatformKind.Twitch) == true);
        Assert.True(result.Messages.Any(message => message.Contains("safety limit", StringComparison.OrdinalIgnoreCase)));
    }),
    ("searches Twitch VODs with Helix user resolution and pagination", async () =>
    {
        var settings = new AppSettings();
        settings.Chat.TwitchClientId = "twitch-client-id";
        settings.Chat.TwitchOAuthToken = "oauth:vod-search-token";
        var requestPaths = new List<string>();
        using var httpClient = new HttpClient(new FakeHttpMessageHandler(request =>
        {
            if (request.RequestUri!.Host == "id.twitch.tv")
            {
                Assert.Equal("Bearer vod-search-token", request.Headers.Authorization?.ToString());
                return CreateTwitchTokenValidationResponse("twitch-client-id");
            }

            requestPaths.Add(request.RequestUri!.PathAndQuery);
            if (request.RequestUri.AbsolutePath == "/helix/users")
            {
                Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
                Assert.Equal("vod-search-token", request.Headers.Authorization?.Parameter);
                Assert.SequenceEqual(["twitch-client-id"], request.Headers.GetValues("Client-Id"));
                Assert.Contains("login=summit1g", request.RequestUri.Query);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                    {
                      "data": [
                        {
                          "id": "26490481",
                          "login": "summit1g",
                          "display_name": "summit1g",
                          "profile_image_url": "https://static-cdn.jtvnw.net/jtv_user_pictures/summit-profile.png"
                        }
                      ]
                    }
                    """)
                };
            }

            if (request.RequestUri.AbsolutePath == "/helix/videos")
            {
                Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
                Assert.Equal("vod-search-token", request.Headers.Authorization?.Parameter);
                Assert.SequenceEqual(["twitch-client-id"], request.Headers.GetValues("Client-Id"));
                Assert.Contains("user_id=26490481", request.RequestUri.Query);
                Assert.Contains("type=archive", request.RequestUri.Query);
                Assert.Contains("sort=time", request.RequestUri.Query);
                Assert.Contains("first=100", request.RequestUri.Query);
                Assert.Contains("after=cursor-1", request.RequestUri.Query);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                    {
                      "data": [
                        {
                          "id": "2786354640",
                          "stream_id": "123456789",
                          "user_id": "26490481",
                          "user_login": "summit1g",
                          "user_name": "summit1g",
                          "title": "vod title",
                          "description": "vod description",
                          "created_at": "2026-06-01T20:00:02Z",
                          "published_at": "2026-06-01T20:05:00Z",
                          "url": "https://www.twitch.tv/videos/2786354640",
                          "thumbnail_url": "https://static-cdn.jtvnw.net/cf_vods/d2nvs31859zcd8/%{width}x%{height}/thumb.jpg",
                          "view_count": 12345,
                          "type": "archive",
                          "duration": "3h8m33s"
                        }
                      ],
                      "pagination": { "cursor": "cursor-2" }
                    }
                    """)
                };
            }

            Assert.Equal("gql.twitch.tv", request.RequestUri.Host);
            Assert.Equal("/gql", request.RequestUri.AbsolutePath);
            Assert.Equal(null, request.Headers.Authorization);
            Assert.SequenceEqual(["kimne78kx3ncx6brgo4mv6wki5h1ko"], request.Headers.GetValues("Client-Id"));
            Assert.True(request.Headers.Contains("X-Device-Id"));
            var accessRequestBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            using var accessRequest = JsonDocument.Parse(accessRequestBody);
            Assert.Contains("videoPlaybackAccessToken", accessRequest.RootElement.GetProperty("query").GetString() ?? "");
            Assert.Equal("2786354640", accessRequest.RootElement.GetProperty("variables").GetProperty("vod0").GetString());
            var publicToken = JsonSerializer.Serialize(new
            {
                authorization = new { forbidden = false, reason = "" },
                chansub = new { restricted_bitrates = Array.Empty<string>() },
                vod_id = 2786354640
            });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    data = new Dictionary<string, object?>
                    {
                        ["vod0"] = new { value = publicToken }
                    }
                }))
            };
        }));

        var service = new TwitchVodService(new MemoryLogger(), httpClient);
        var result = await service.SearchAsync(
            new TwitchVodSearchRequest("summit1g", TwitchVodTypeFilter.Archive, "cursor-1"),
            settings);

        Assert.Equal(TwitchVodSearchStatus.Available, result.Status);
        Assert.Equal("cursor-2", result.NextCursor);
        Assert.Equal("26490481", result.Broadcaster!.Id);
        Assert.Equal(
            "https://static-cdn.jtvnw.net/jtv_user_pictures/summit-profile.png",
            result.Broadcaster.ProfileImageUrl);
        var vod = result.Videos.Single();
        Assert.Equal("2786354640", vod.Id);
        Assert.Equal("123456789", vod.StreamId);
        Assert.Equal("26490481", vod.BroadcasterId);
        Assert.Equal("vod title", vod.Title);
        Assert.Equal("vod description", vod.Description);
        Assert.Equal("https://www.twitch.tv/videos/2786354640", vod.Url);
        Assert.Contains("320x180", vod.ThumbnailUrl);
        Assert.DoesNotContain("%{width}", vod.ThumbnailUrl);
        Assert.Equal(new DateTimeOffset(2026, 6, 1, 20, 0, 2, TimeSpan.Zero), vod.CreatedAtUtc);
        Assert.Equal(new DateTimeOffset(2026, 6, 1, 20, 5, 0, TimeSpan.Zero), vod.PublishedAtUtc);
        Assert.Equal(new TimeSpan(3, 8, 33), vod.Duration);
        Assert.Equal(12345, vod.ViewCount);
        Assert.Equal(TwitchVodTypeFilter.Archive, vod.Type);
        Assert.Equal(TwitchVodAccessKind.Public, vod.AccessKind);
        Assert.Equal(
            "https://static-cdn.jtvnw.net/jtv_user_pictures/summit-profile.png",
            vod.ProfileImageUrl);
        var card = new VodViewModel(vod, (_, _) => Task.CompletedTask);
        Assert.Equal(vod.ProfileImageUrl, card.ProfileImageUrl);
        Assert.Equal(vod.ProfileImageUrl, card.Target.ProfileImageUrl);
        Assert.Equal(3, requestPaths.Count);
    }),
    ("classifies subscriber-only Twitch VODs from anonymous playback tokens", async () =>
    {
        var settings = new AppSettings();
        settings.Chat.TwitchClientId = "twitch-client-id";
        settings.Chat.TwitchOAuthToken = "oauth:vod-access-token";
        var graphQlRequestCount = 0;
        using var httpClient = new HttpClient(new FakeHttpMessageHandler(request =>
        {
            if (request.RequestUri!.Host == "id.twitch.tv")
            {
                return CreateTwitchTokenValidationResponse("twitch-client-id");
            }

            if (request.RequestUri!.AbsolutePath == "/helix/users")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"data":[{"id":"1","login":"streamer","display_name":"Streamer"}]}""")
                };
            }

            if (request.RequestUri.AbsolutePath == "/helix/videos")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                    {
                      "data": [
                        {
                          "id": "2091984624",
                          "user_id": "1",
                          "user_login": "streamer",
                          "user_name": "Streamer",
                          "title": "subscriber VOD",
                          "url": "https://www.twitch.tv/videos/2091984624",
                          "type": "archive",
                          "duration": "1h"
                        },
                        {
                          "id": "2838068542",
                          "user_id": "1",
                          "user_login": "streamer",
                          "user_name": "Streamer",
                          "title": "public VOD",
                          "url": "https://www.twitch.tv/videos/2838068542",
                          "type": "archive",
                          "duration": "2h"
                        }
                      ],
                      "pagination": {}
                    }
                    """)
                };
            }

            graphQlRequestCount++;
            Assert.Equal("gql.twitch.tv", request.RequestUri.Host);
            Assert.Equal(null, request.Headers.Authorization);
            Assert.SequenceEqual(["kimne78kx3ncx6brgo4mv6wki5h1ko"], request.Headers.GetValues("Client-Id"));
            var requestBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            using var requestDocument = JsonDocument.Parse(requestBody);
            var variables = requestDocument.RootElement.GetProperty("variables");
            Assert.Equal("2091984624", variables.GetProperty("vod0").GetString());
            Assert.Equal("2838068542", variables.GetProperty("vod1").GetString());

            var subscriberToken = JsonSerializer.Serialize(new
            {
                authorization = new { forbidden = false, reason = "" },
                chansub = new
                {
                    restricted_bitrates = new[]
                    {
                        "160p30",
                        "360p30",
                        "480p30",
                        "720p60",
                        "audio_only",
                        "chunked"
                    }
                },
                vod_id = 2091984624L
            });
            var publicToken = JsonSerializer.Serialize(new
            {
                authorization = new { forbidden = false, reason = "" },
                chansub = new { restricted_bitrates = Array.Empty<string>() },
                vod_id = 2838068542L
            });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    data = new Dictionary<string, object?>
                    {
                        ["vod0"] = new { value = subscriberToken },
                        ["vod1"] = new { value = publicToken }
                    }
                }))
            };
        }));

        var service = new TwitchVodService(new MemoryLogger(), httpClient);
        var result = await service.SearchAsync(new TwitchVodSearchRequest("streamer"), settings);

        Assert.Equal(1, graphQlRequestCount);
        Assert.Equal(2, result.Videos.Count);
        Assert.Equal(TwitchVodAccessKind.SubscriberOnly, result.Videos[0].AccessKind);
        Assert.Equal(TwitchVodAccessKind.Public, result.Videos[1].AccessKind);
        Assert.DoesNotContain("Access could not be checked", result.Message);

        var subscriberCard = new VodViewModel(result.Videos[0], (_, _) => Task.CompletedTask);
        var publicCard = new VodViewModel(result.Videos[1], (_, _) => Task.CompletedTask);
        Assert.True(subscriberCard.IsSubscriberOnly);
        Assert.Equal(false, subscriberCard.IsTwitchVodAccessUnknown);
        Assert.Equal(false, publicCard.IsSubscriberOnly);
        Assert.Equal(false, publicCard.IsTwitchVodAccessUnknown);
    }),
    ("keeps Twitch VOD access unknown when playback metadata cannot be verified", async () =>
    {
        var settings = new AppSettings();
        settings.Chat.TwitchClientId = "twitch-client-id";
        settings.Chat.TwitchOAuthToken = "vod-unknown-token";
        using var httpClient = new HttpClient(new FakeHttpMessageHandler(request =>
        {
            if (request.RequestUri!.Host == "id.twitch.tv")
            {
                return CreateTwitchTokenValidationResponse("twitch-client-id");
            }

            if (request.RequestUri!.AbsolutePath == "/helix/users")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"data":[{"id":"1","login":"streamer","display_name":"Streamer"}]}""")
                };
            }

            if (request.RequestUri.AbsolutePath == "/helix/videos")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                    {
                      "data": [
                        {
                          "id": "2091984624",
                          "user_id": "1",
                          "user_login": "streamer",
                          "user_name": "Streamer",
                          "title": "unclassified VOD",
                          "url": "https://www.twitch.tv/videos/2091984624",
                          "type": "archive",
                          "duration": "1h"
                        }
                      ],
                      "pagination": {}
                    }
                    """)
                };
            }

            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("""{"message":"temporarily unavailable"}""")
            };
        }));

        var service = new TwitchVodService(new MemoryLogger(), httpClient);
        var result = await service.SearchAsync(new TwitchVodSearchRequest("streamer"), settings);
        var vod = result.Videos.Single();

        Assert.Equal(TwitchVodAccessKind.Unknown, vod.AccessKind);
        Assert.Contains("Access could not be checked for 1 VOD", result.Message);
        var card = new VodViewModel(vod, (_, _) => Task.CompletedTask);
        Assert.Equal(false, card.IsSubscriberOnly);
        Assert.True(card.IsTwitchVodAccessUnknown);
    }),
    ("uses Twitch VOD type filters and omits type for all", async () =>
    {
        var settings = new AppSettings();
        settings.Chat.TwitchClientId = "twitch-client-id";
        settings.Chat.TwitchOAuthToken = "vod-filter-token";
        var videoQueries = new List<string>();
        using var httpClient = new HttpClient(new FakeHttpMessageHandler(request =>
        {
            if (request.RequestUri!.Host == "id.twitch.tv")
            {
                return CreateTwitchTokenValidationResponse("twitch-client-id");
            }

            if (request.RequestUri!.AbsolutePath == "/helix/users")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"data":[{"id":"1","login":"streamer","display_name":"Streamer"}]}""")
                };
            }

            videoQueries.Add(request.RequestUri.Query);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"data":[],"pagination":{}}""")
            };
        }));

        var service = new TwitchVodService(new MemoryLogger(), httpClient);
        await service.SearchAsync(new TwitchVodSearchRequest("streamer", TwitchVodTypeFilter.Archive), settings);
        await service.SearchAsync(new TwitchVodSearchRequest("streamer", TwitchVodTypeFilter.Highlight), settings);
        await service.SearchAsync(new TwitchVodSearchRequest("streamer", TwitchVodTypeFilter.Upload), settings);
        await service.SearchAsync(new TwitchVodSearchRequest("streamer", TwitchVodTypeFilter.All), settings);

        Assert.Contains("type=archive", videoQueries[0]);
        Assert.Contains("type=highlight", videoQueries[1]);
        Assert.Contains("type=upload", videoQueries[2]);
        Assert.DoesNotContain("type=", videoQueries[3]);
    }),
    ("reports Twitch VOD auth and channel-not-found states", async () =>
    {
        var requestCount = 0;
        using var httpClient = new HttpClient(new FakeHttpMessageHandler(request =>
        {
            if (request.RequestUri!.Host == "id.twitch.tv")
            {
                return CreateTwitchTokenValidationResponse("twitch-client-id");
            }

            requestCount++;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"data":[]}""")
            };
        }));
        var service = new TwitchVodService(new MemoryLogger(), httpClient);

        var missingAuth = await service.SearchAsync(
            new TwitchVodSearchRequest("streamer"),
            new AppSettings());
        Assert.Equal(TwitchVodSearchStatus.NotConfigured, missingAuth.Status);
        Assert.Equal(0, requestCount);

        var settings = new AppSettings();
        settings.Chat.TwitchClientId = "twitch-client-id";
        settings.Chat.TwitchOAuthToken = "vod-not-found-token";
        var notFound = await service.SearchAsync(
            new TwitchVodSearchRequest("missing"),
            settings);
        Assert.Equal(TwitchVodSearchStatus.NotFound, notFound.Status);
        Assert.Equal(1, requestCount);
    }),
    ("searches Twitch channels with Helix live and offline mapping", async () =>
    {
        var settings = new AppSettings();
        settings.Chat.TwitchClientId = "twitch-client-id";
        settings.Chat.TwitchOAuthToken = "oauth:channel-search-token";
        var requestPaths = new List<string>();
        using var httpClient = new HttpClient(new FakeHttpMessageHandler(request =>
        {
            if (request.RequestUri!.Host == "id.twitch.tv")
            {
                Assert.Equal("Bearer channel-search-token", request.Headers.Authorization?.ToString());
                return CreateTwitchTokenValidationResponse("twitch-client-id");
            }

            requestPaths.Add(request.RequestUri!.PathAndQuery);
            if (request.RequestUri.Host == "kick.com")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"channels":[]}""")
                };
            }

            Assert.Equal("api.twitch.tv", request.RequestUri.Host);
            Assert.Equal("/helix/search/channels", request.RequestUri.AbsolutePath);
            Assert.Contains("query=xqc", request.RequestUri.Query);
            Assert.Contains("first=5", request.RequestUri.Query);
            Assert.Contains("live_only=false", request.RequestUri.Query);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("channel-search-token", request.Headers.Authorization?.Parameter);
            Assert.SequenceEqual(["twitch-client-id"], request.Headers.GetValues("Client-Id"));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                {
                  "data": [
                    {
                      "broadcaster_login": "xqc",
                      "display_name": "xQc",
                      "thumbnail_url": "https://static-cdn.jtvnw.net/xqc.jpg",
                      "title": "live title",
                      "game_name": "Just Chatting",
                      "is_live": true
                    },
                    {
                      "broadcaster_login": "xqcclips",
                      "display_name": "xQcClips",
                      "thumbnail_url": "https://static-cdn.jtvnw.net/xqcclips.jpg",
                      "title": "",
                      "game_name": "",
                      "is_live": false
                    }
                  ]
                }
                """)
            };
        }));
        var streamlink = new FakeStreamlinkService
        {
            ProbeStreamsOverride = (request, _) => Task.FromResult(
                request.Target.Platform == PlatformKind.Twitch && request.Target.Channel == "xqc"
                    ? new StreamlinkProbeResult(true, "Playable stream found.")
                    : new StreamlinkProbeResult(false, "No streams found."))
        };
        var service = new StreamSearchService(new MemoryLogger(), streamlink, httpClient);

        var result = await service.SearchAsync(new StreamSearchRequest("xqc", "best", 5), settings);

        Assert.Equal(StreamSearchResultStatus.Available, result.Status);
        Assert.True(requestPaths.Any(path => path.Contains("/helix/search/channels", StringComparison.Ordinal)));
        var live = result.Channels.Single(channel => channel.Platform == PlatformKind.Twitch && channel.Channel == "xqc");
        Assert.Equal("xQc", live.DisplayName);
        Assert.Equal("https://static-cdn.jtvnw.net/xqc.jpg", live.ThumbnailUrl);
        Assert.Equal("live title", live.Title);
        Assert.Equal("Just Chatting", live.CategoryName);
        Assert.Equal(StreamSearchChannelState.Live, live.State);
        Assert.True(live.CanPlay);
        var offline = result.Channels.Single(channel => channel.Platform == PlatformKind.Twitch && channel.Channel == "xqcclips");
        Assert.Equal(StreamSearchChannelState.Offline, offline.State);
        Assert.Equal(false, offline.CanPlay);
        Assert.Equal(0, streamlink.ProbeRequests.Count);
    }),
    ("searches Kick channels with curl fallback ranking and dedupe", async () =>
    {
        using var httpClient = new HttpClient(new FakeHttpMessageHandler(request =>
        {
            Assert.Equal("kick.com", request.RequestUri!.Host);
            Assert.Equal("/api/search", request.RequestUri.AbsolutePath);
            Assert.Contains("searched_word=xqc", request.RequestUri.Query);
            return new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent("""{"message":"blocked"}""")
            };
        }));
        var curlRequests = new List<(string Url, string Referrer)>();
        var streamlink = new FakeStreamlinkService
        {
            ProbeStreamsOverride = (request, _) => Task.FromResult(
                request.Target.Platform == PlatformKind.Kick && request.Target.Channel == "xqc"
                    ? new StreamlinkProbeResult(true, "Playable stream found.")
                    : new StreamlinkProbeResult(false, "No streams found."))
        };
        var service = new StreamSearchService(
            new MemoryLogger(),
            streamlink,
            httpClient,
            (url, referrer, _) =>
            {
                curlRequests.Add((url, referrer));
                return Task.FromResult<string?>("""
                {
                  "channels": [
                    {
                      "slug": "xqc",
                      "isLive": false,
                      "user": { "username": "xQc offline", "profilePic": "https://files.kick.com/xqc-offline.jpg" }
                    },
                    {
                      "slug": "xqc",
                      "isLive": true,
                      "user": { "username": "xQc", "profilePic": "https://files.kick.com/xqc.jpg" },
                      "livestream": {
                        "session_title": "kick live title",
                        "viewer_count": 4321,
                        "category": { "name": "IRL" }
                      }
                    },
                    {
                      "slug": "xqcclips",
                      "isLive": false,
                      "user": { "username": "xQcClips", "profilePic": "https://files.kick.com/xqcclips.jpg" },
                      "recentCategories": [{ "name": "Just Chatting" }]
                    }
                  ]
                }
                """);
            });

        var result = await service.SearchAsync(new StreamSearchRequest("xqc", "best", 10), new AppSettings
        {
            StreamlinkPath = "streamlink.exe"
        });

        Assert.Equal(1, curlRequests.Count);
        Assert.Contains("searched_word=xqc", curlRequests[0].Url);
        Assert.Equal("https://kick.com/", curlRequests[0].Referrer);
        var kickChannels = result.Channels.Where(channel => channel.Platform == PlatformKind.Kick).ToArray();
        Assert.Equal(2, kickChannels.Length);
        var live = kickChannels.Single(channel => channel.Channel == "xqc");
        Assert.Equal("xQc", live.DisplayName);
        Assert.Equal("https://files.kick.com/xqc.jpg", live.ThumbnailUrl);
        Assert.Equal("kick live title", live.Title);
        Assert.Equal("IRL", live.CategoryName);
        Assert.Equal(StreamSearchChannelState.Live, live.State);
        Assert.True(live.CanPlay);
        Assert.Equal(4321, live.ViewerCount);
        var offline = kickChannels.Single(channel => channel.Channel == "xqcclips");
        Assert.Equal(StreamSearchChannelState.Offline, offline.State);
        Assert.Equal("Just Chatting", offline.CategoryName);
        Assert.Equal(false, offline.CanPlay);
        Assert.Equal(false, streamlink.ProbeRequests.Any(request => request.Target.Platform == PlatformKind.Kick));
    }),
    ("short stream search skips channel discovery and probes exact candidates", async () =>
    {
        var httpRequests = 0;
        using var httpClient = new HttpClient(new FakeHttpMessageHandler(_ =>
        {
            httpRequests++;
            throw new InvalidOperationException("Short queries should not call website search.");
        }));
        var streamlink = new FakeStreamlinkService
        {
            ProbeStreamsOverride = (_, _) => Task.FromResult(new StreamlinkProbeResult(false, "No streams found."))
        };
        var service = new StreamSearchService(new MemoryLogger(), streamlink, httpClient);

        var result = await service.SearchAsync(new StreamSearchRequest("xy"), new AppSettings
        {
            StreamlinkPath = "streamlink.exe"
        });

        Assert.Equal(0, httpRequests);
        Assert.Equal(1, streamlink.ProbeRequests.Count);
        Assert.Equal(PlatformKind.Kick, streamlink.ProbeRequests[0].Target.Platform);
        Assert.True(result.Channels.All(channel => channel.State == StreamSearchChannelState.Unavailable));
    }),
    ("exact stream URL skips channel discovery and retains Streamlink probing", async () =>
    {
        var httpRequests = 0;
        using var httpClient = new HttpClient(new FakeHttpMessageHandler(_ =>
        {
            httpRequests++;
            throw new InvalidOperationException("Exact URLs should not call channel discovery.");
        }));
        var streamlink = new FakeStreamlinkService
        {
            ProbeStreamsOverride = (_, _) => Task.FromResult(new StreamlinkProbeResult(false, "No streams found."))
        };
        var service = new StreamSearchService(new MemoryLogger(), streamlink, httpClient);

        var result = await service.SearchAsync(
            new StreamSearchRequest("https://kick.com/xqc"),
            new AppSettings { StreamlinkPath = "streamlink.exe" });

        Assert.Equal(0, httpRequests);
        Assert.Equal(1, streamlink.ProbeRequests.Count);
        Assert.Equal(PlatformKind.Kick, streamlink.ProbeRequests[0].Target.Platform);
        Assert.Equal("xqc", streamlink.ProbeRequests[0].Target.Channel);
        Assert.Equal(StreamSearchChannelState.Unavailable, result.Channels.Single().State);
    }),
    ("parses Kick VODs with curl fallback metadata and duration", async () =>
    {
        using var httpClient = new HttpClient(new FakeHttpMessageHandler(request =>
        {
            Assert.Equal("kick.com", request.RequestUri!.Host);
            Assert.Equal("/api/v2/channels/xqc/videos", request.RequestUri.AbsolutePath);
            return new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent("""{"message":"blocked"}""")
            };
        }));
        var curlRequests = new List<(string Url, string Referrer)>();
        var service = new KickVodService(
            new MemoryLogger(),
            httpClient,
            (url, referrer, _) =>
            {
                curlRequests.Add((url, referrer));
                return Task.FromResult<string?>("""
                {
                  "data": [
                  {
                    "id": 12345,
                    "live_stream_id": 67890,
                    "channel_id": 668,
                    "created_at": "2026-06-01T12:01:02Z",
                    "session_title": "Kick VOD title",
                    "start_time": "2026-06-01T11:00:00Z",
                    "source": "https://vod.kick.com/xqc/index.m3u8",
                    "duration": 2345000,
                    "thumbnail": { "src": "https://files.kick.com/vod.jpg" },
                    "views": 1234,
                    "channel": { "user": { "profile_pic": "//files.kick.com/xqc-profile.jpg" } },
                    "video": { "uuid": "uuid-123" },
                    "categories": [{ "name": "Just Chatting" }]
                  }
                  ],
                  "pagination": { "next_cursor": "vod-next" }
                }
                """);
            });

        var result = await service.SearchAsync(
            new KickVodSearchRequest("https://kick.com/xqc", Cursor: "vod-cursor", PageSize: 1),
            new AppSettings());

        Assert.Equal(KickVodSearchStatus.Available, result.Status);
        Assert.Equal("vod-next", result.NextCursor);
        Assert.Equal(1, curlRequests.Count);
        Assert.Contains("/api/v2/channels/xqc/videos", curlRequests[0].Url);
        Assert.Equal("https://kick.com/xqc", curlRequests[0].Referrer);
        var vod = result.Videos.Single();
        Assert.Equal("12345", vod.Id);
        Assert.Equal("67890", vod.LiveStreamId);
        Assert.Equal("uuid-123", vod.Uuid);
        Assert.Equal("668", vod.ChannelId);
        Assert.Equal("xqc", vod.ChannelSlug);
        Assert.Equal("Kick VOD title", vod.Title);
        Assert.Equal("https://vod.kick.com/xqc/index.m3u8", vod.Source);
        Assert.Equal("https://files.kick.com/vod.jpg", vod.ThumbnailUrl);
        Assert.Equal("Just Chatting", vod.CategoryName);
        Assert.Equal(new DateTimeOffset(2026, 6, 1, 12, 1, 2, TimeSpan.Zero), vod.CreatedAtUtc);
        Assert.Equal(new DateTimeOffset(2026, 6, 1, 11, 0, 0, TimeSpan.Zero), vod.StartedAtUtc);
        Assert.Equal(TimeSpan.FromMilliseconds(2345000), vod.Duration);
        Assert.Equal(1234, vod.ViewCount);
        Assert.Equal("https://files.kick.com/xqc-profile.jpg", vod.ProfileImageUrl);
        var card = new VodViewModel(vod, (_, _) => Task.CompletedTask);
        Assert.Equal(vod.ProfileImageUrl, card.ProfileImageUrl);
        Assert.Equal(vod.ProfileImageUrl, card.Target.ProfileImageUrl);
    }),
    ("fills Kick VOD profile image from channel metadata when VOD rows omit it", async () =>
    {
        using var httpClient = new HttpClient(new FakeHttpMessageHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/v2/channels/xqc/videos")
            {
                return new HttpResponseMessage(HttpStatusCode.Forbidden);
            }

            Assert.Equal("/api/v2/channels/xqc", request.RequestUri.AbsolutePath);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"user":{"profile_pic":"//files.kick.com/xqc-profile.jpg"}}""")
            };
        }));
        var service = new KickVodService(
            new MemoryLogger(),
            httpClient,
            (url, _, _) =>
            {
                Assert.Contains("/api/v2/channels/xqc/videos", url);
                return Task.FromResult<string?>("""
                [{
                  "id": 12345,
                  "source": "https://vod.kick.com/xqc/index.m3u8",
                  "video": { "uuid": "uuid-123" }
                }]
                """);
            });

        var result = await service.SearchAsync(new KickVodSearchRequest("xqc"), new AppSettings());

        Assert.Equal(KickVodSearchStatus.Available, result.Status);
        Assert.Equal("https://files.kick.com/xqc-profile.jpg", result.Videos.Single().ProfileImageUrl);
    }),
    ("gets configured Kick followed live streams", async () =>
    {
        var settings = new AppSettings();
        settings.Chat.KickOAuthToken = "kick-token";
        settings.FollowedChannels.KickChannelSlugs = ["xqc", "offline"];

        using var httpClient = new HttpClient(new FakeHttpMessageHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/public/v1/users")
            {
                Assert.Contains("id=12345", request.RequestUri.Query);
                Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
                Assert.Equal("kick-token", request.Headers.Authorization?.Parameter);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                    {
                      "data": [
                        {
                          "user_id": 12345,
                          "profile_picture": "https://files.kick.com/xqc-profile.jpg"
                        }
                      ]
                    }
                    """, Encoding.UTF8, "application/json")
                };
            }

            Assert.Equal("api.kick.com", request.RequestUri!.Host);
            Assert.Equal("/public/v1/channels", request.RequestUri.AbsolutePath);
            Assert.Contains("slug=xqc", request.RequestUri.Query);
            Assert.Contains("slug=offline", request.RequestUri.Query);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("kick-token", request.Headers.Authorization?.Parameter);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                {
                  "data": [
                    {
                      "slug": "xqc",
                      "broadcaster_user_id": 12345,
                      "category": { "name": "IRL" },
                      "stream_title": "kick live",
                      "stream": {
                        "is_live": true,
                        "viewer_count": 9876,
                        "thumbnail": "https://files.kick.com/live.jpg",
                        "started_at": "2026-06-01T12:00:00Z",
                        "language": "en",
                        "is_mature": true
                      }
                    },
                    {
                      "slug": "offline",
                      "stream": null
                    },
                    {
                      "slug": "bad slug",
                      "stream_title": "bad row",
                      "stream": {
                        "is_live": true,
                        "viewer_count": 1
                      }
                    }
                  ]
                }
                """)
            };
        }));

        var service = new FollowedStreamsService(new MemoryLogger(), httpClient);
        var result = await service.GetLiveFollowedStreamsAsync(settings);

        var stream = result.Streams.Single(item => item.Platform == PlatformKind.Kick);
        Assert.Equal("xqc", stream.Channel);
        Assert.Equal("kick live", stream.Title);
        Assert.Equal("IRL", stream.CategoryName);
        Assert.Equal(9876, stream.ViewerCount);
        Assert.Equal("https://files.kick.com/xqc-profile.jpg", stream.ProfileImageUrl);
        Assert.Equal("https://files.kick.com/live.jpg", stream.ThumbnailUrl);
        Assert.Equal(true, stream.IsMature);
        Assert.True(result.Messages.Any(message => message.StartsWith("Twitch:", StringComparison.Ordinal)));
    }),
    ("skips invalid configured Kick followed channel slugs", async () =>
    {
        var settings = new AppSettings();
        settings.Chat.KickOAuthToken = "kick-token";
        settings.FollowedChannels.KickChannelSlugs =
        [
            "xqc",
            "bad slug",
            "https://www.twitch.tv/summit1g",
            "kick.com/offline"
        ];

        using var httpClient = new HttpClient(new FakeHttpMessageHandler(request =>
        {
            Assert.Equal("api.kick.com", request.RequestUri!.Host);
            Assert.Equal("/public/v1/channels", request.RequestUri.AbsolutePath);
            Assert.Contains("slug=xqc", request.RequestUri.Query);
            Assert.Contains("slug=offline", request.RequestUri.Query);
            Assert.DoesNotContain("bad", request.RequestUri.Query);
            Assert.DoesNotContain("summit1g", request.RequestUri.Query);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                {
                  "data": [
                    {
                      "slug": "xqc",
                      "stream_title": "kick live",
                      "stream": {
                        "is_live": true,
                        "viewer_count": 9876
                      }
                    },
                    {
                      "slug": "offline",
                      "stream": null
                    }
                  ]
                }
                """)
            };
        }));

        var service = new FollowedStreamsService(new MemoryLogger(), httpClient);
        var result = await service.GetLiveFollowedStreamsAsync(settings);

        var stream = result.Streams.Single(item => item.Platform == PlatformKind.Kick);
        Assert.Equal("xqc", stream.Channel);
    }),
    ("uses Kick top-level thumbnail when stream thumbnail is absent", async () =>
    {
        var settings = new AppSettings();
        settings.Chat.KickOAuthToken = "kick-token";
        settings.FollowedChannels.KickChannelSlugs = ["xqc"];

        using var httpClient = new HttpClient(new FakeHttpMessageHandler(request =>
        {
            Assert.Equal("api.kick.com", request.RequestUri!.Host);
            Assert.Equal("/public/v1/channels", request.RequestUri.AbsolutePath);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                {
                  "data": [
                    {
                      "slug": "xqc",
                      "thumbnail": "//files.kick.com/live.jpg",
                      "stream_title": "kick live",
                      "stream": {
                        "is_live": true,
                        "viewer_count": 9876
                      }
                    }
                  ]
                }
                """)
            };
        }));

        var service = new FollowedStreamsService(new MemoryLogger(), httpClient);
        var result = await service.GetLiveFollowedStreamsAsync(settings);

        var stream = result.Streams.Single(item => item.Platform == PlatformKind.Kick);
        Assert.Equal("https://files.kick.com/live.jpg", stream.ThumbnailUrl);
    }),
    ("decodes Kick WebP thumbnails", () =>
    {
        const string webpBase64 = "UklGRkAAAABXRUJQVlA4IDQAAADwAQCdASoBAAEAAQAcJaACdLoB+AAETAAA/vW4f/6aR40jxpHxcP/ugT90CfugT/3NoAAA";
        var bytes = Convert.FromBase64String(webpBase64);

        Assert.True(AnimatedEmoteImage.TryDecodeImageForTest(bytes, out var frameCount, out var width, out var height));
        Assert.Equal(1, frameCount);
        Assert.Equal(1, width);
        Assert.Equal(1, height);

        return Task.CompletedTask;
    }),
    ("decodes animated emote frames", () =>
    {
        return TestSta.RunAsync(async () =>
        {
            var firstFrame = BitmapSource.Create(
                2,
                2,
                96,
                96,
                PixelFormats.Pbgra32,
                null,
                new byte[]
                {
                    0, 0, 255, 255, 0, 0, 255, 255,
                    0, 0, 255, 255, 0, 0, 255, 255
                },
                8);
            var secondFrame = BitmapSource.Create(
                2,
                2,
                96,
                96,
                PixelFormats.Pbgra32,
                null,
                new byte[]
                {
                    255, 0, 0, 255, 255, 0, 0, 255,
                    255, 0, 0, 255, 255, 0, 0, 255
                },
                8);
            var encoder = new GifBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(firstFrame));
            encoder.Frames.Add(BitmapFrame.Create(secondFrame));
            using var stream = new MemoryStream();
            encoder.Save(stream);

            Assert.True(AnimatedEmoteImage.TryDecodeImageForTest(
                stream.ToArray(),
                out var frameCount,
                out var width,
                out var height));
            Assert.Equal(2, frameCount);
            Assert.Equal(2, width);
            Assert.Equal(2, height);

            var tempPath = Path.Combine(
                Path.GetTempPath(),
                $"svs-animated-emote-{Guid.NewGuid():N}.gif");
            try
            {
                File.WriteAllBytes(tempPath, stream.ToArray());
                var image = new AnimatedEmoteImage
                {
                    ImageUrl = new Uri(tempPath).AbsoluteUri
                };
                await TestWait.UntilAsync(
                    () => !image.IsImageLoadPending && image.Source is BitmapSource,
                    TimeSpan.FromSeconds(1));

                Assert.True(image.ApplyAnimationClock(TimeSpan.Zero, out _));
                Assert.True(BitmapAssert.CountPixels(
                    image.Source,
                    (r, g, b) => r > 180 && g < 100 && b < 100) > 0);

                Assert.True(image.ApplyAnimationClock(TimeSpan.FromMilliseconds(150), out _));
                Assert.True(BitmapAssert.CountPixels(
                    image.Source,
                    (r, g, b) => r < 100 && g < 100 && b > 180) > 0);
            }
            finally
            {
                AnimatedEmoteImage.ClearCacheForTest();
                File.Delete(tempPath);
            }

        });
    }),
    ("sends tab chat through connected chat client", async () =>
    {
        var streamlink = new FakeStreamlinkService();
        var playbackFactory = new FakePlaybackEngineFactory();
        var logger = new MemoryLogger();
        var chatFactory = new FakeChatClientFactory();
        var tab = TestViewModels.CreateTab(
            StreamInputParser.Parse("albralelie", PlatformKind.Twitch),
            "best",
            streamlink,
            playbackFactory,
            chatFactory,
            logger,
            action => action());

        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\Program Files\VideoLAN\VLC"
        };
        settings.Chat.ConnectAutomatically = true;

        tab.SetVideoHandle(new IntPtr(1234));
        await tab.StartAsync(settings);
        tab.OutgoingChatText = " hello chat ";
        await tab.SendChatMessageAsync();

        Assert.SequenceEqual(new[] { "hello chat" }, chatFactory.Client.SentMessages);
        Assert.Equal("", tab.OutgoingChatText);
        Assert.Equal("tester", tab.ChatMessages.Last().Username);
        Assert.Equal("hello chat", tab.ChatMessages.Last().Message);        await tab.DisposeAsync();
    }),
    ("sends multilingual chat without splitting Unicode text elements", async () =>
    {
        var streamlink = new FakeStreamlinkService();
        var playbackFactory = new FakePlaybackEngineFactory();
        var logger = new MemoryLogger();
        var chatFactory = new FakeChatClientFactory();
        var tab = TestViewModels.CreateTab(
            StreamInputParser.Parse("albralelie", PlatformKind.Twitch),
            "best",
            streamlink,
            playbackFactory,
            chatFactory,
            logger,
            action => action());

        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\Program Files\VideoLAN\VLC"
        };
        settings.Chat.ConnectAutomatically = true;
        settings.Chat.Layout = ChatLayout.Docked;

        var emoji = char.ConvertFromUtf32(0x1F602);
        tab.SetVideoHandle(new IntPtr(1234));
        await tab.StartAsync(settings);
        tab.OutgoingChatText = " " + string.Concat(Enumerable.Repeat(emoji, 501)) + " ";
        await tab.SendChatMessageAsync();

        var sent = chatFactory.Client.SentMessages.Single();
        Assert.Equal(500, new StringInfo(sent).LengthInTextElements);
        Assert.Equal(string.Concat(Enumerable.Repeat(emoji, 500)), sent);
        Assert.Equal(false, ContainsUnpairedSurrogate(sent));        await tab.DisposeAsync();
    }),
    ("docked chat suppresses a matching server echo after local send", async () =>
    {
        var streamlink = new FakeStreamlinkService();
        var playbackFactory = new FakePlaybackEngineFactory();
        var logger = new MemoryLogger();
        var chatFactory = new FakeChatClientFactory();
        var target = StreamInputParser.Parse("albralelie", PlatformKind.Twitch);
        var tab = TestViewModels.CreateTab(
            target,
            "best",
            streamlink,
            playbackFactory,
            chatFactory,
            logger,
            action => action());

        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\Program Files\VideoLAN\VLC"
        };
        settings.Chat.ConnectAutomatically = true;
        settings.Chat.Layout = ChatLayout.Docked;
        chatFactory.Client.EchoSentMessages = true;

        tab.SetVideoHandle(new IntPtr(1234));
        await tab.StartAsync(settings);
        tab.OutgoingChatText = "hello chat";
        await tab.SendChatMessageAsync();

        chatFactory.Client.Receive(new ChatMessage(
            target.Platform,
            target.Channel,
            "viewer",
            "hello chat",
            DateTimeOffset.Now,
            "#8AB4F8"));

        Assert.SequenceEqual(new[] { "hello chat" }, chatFactory.Client.SentMessages);
        Assert.Equal(3, tab.ChatMessages.Count(message => message.Message == "hello chat"));
        Assert.Equal(2, tab.DockedChatMessages.Count(message => message.Message == "hello chat"));
        Assert.Equal("viewer", tab.DockedChatMessages.Last().Username);
        await tab.DisposeAsync();
    }),
    ];
}
