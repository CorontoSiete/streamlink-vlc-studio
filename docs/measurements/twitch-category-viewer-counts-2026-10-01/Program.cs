using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using StreamlinkVlcStudio.Core.Logging;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Services;
using StreamlinkVlcStudio.Infrastructure.Settings;
using StreamlinkVlcStudio.Infrastructure.Viewers;

if (args.Length != 2) throw new ArgumentException("Expected output prefix and category fixture path.");
var settings = await new JsonSettingsService().LoadAsync();
var logger = new ProbeLogger();
using var handler = new CountingHandler(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All });
using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
http.DefaultRequestHeaders.UserAgent.ParseAdd("StreamlinkVlcStudio/1.0");
var service = new BrowseService(logger, http);
using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(2));
// Fetch the real browse page on both runs so token validation and connection warm-up
// match the app. Reuse its category IDs when comparing the count-loading phase.
var page = await service.GetCategoriesAsync(new BrowseCategoryRequest(PlatformKind.Twitch, PageSize: 10), settings, cancellation.Token);
if (!page.IsAvailable) throw new InvalidOperationException($"Category request failed: {page.Status}.");
var categories = File.Exists(args[1])
    ? JsonSerializer.Deserialize<BrowseCategory[]>(await File.ReadAllTextAsync(args[1]))!
    : page.Items.ToArray();
await File.WriteAllTextAsync(args[1], JsonSerializer.Serialize(categories, new JsonSerializerOptions { WriteIndented = true }));
handler.Reset();
var stopwatch = Stopwatch.StartNew();
using var slots = new SemaphoreSlim(4);
var rows = await Task.WhenAll(categories.Select(async category =>
{
    await slots.WaitAsync(cancellation.Token);
    try
    {
        var lookup = Stopwatch.StartNew();
        var result = await service.GetCategoryViewerCountsAsync(
            new BrowseCategoryViewerCountRequest(PlatformKind.Twitch, [category.Id]), settings, cancellation.Token);
        lookup.Stop();
        return new
        {
            category.Id,
            category.Name,
            Status = result.Status.ToString(),
            ViewerCount = result.Items.SingleOrDefault()?.ViewerCount,
            LookupMilliseconds = Math.Round(lookup.Elapsed.TotalMilliseconds, 1),
            ReadyMilliseconds = Math.Round(stopwatch.Elapsed.TotalMilliseconds, 1)
        };
    }
    finally { slots.Release(); }
}));
stopwatch.Stop();
var output = new
{
    TimestampUtc = DateTimeOffset.UtcNow,
    Concurrency = 4,
    Categories = rows.Length,
    FirstCountMilliseconds = rows.Min(row => row.ReadyMilliseconds),
    AllCountsMilliseconds = Math.Round(stopwatch.Elapsed.TotalMilliseconds, 1),
    StreamRequests = handler.StreamRequests,
    AggregateRequests = handler.AggregateRequests,
    Rows = rows,
    Logs = logger.Messages.ToArray()
};
await File.WriteAllTextAsync(args[0] + ".json", JsonSerializer.Serialize(output, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"{rows.Length} categories; first count {output.FirstCountMilliseconds:0.0} ms; all counts {output.AllCountsMilliseconds:0.0} ms; {output.StreamRequests} stream requests; {output.AggregateRequests} aggregate requests.");
foreach (var row in rows) Console.WriteLine($"{row.Name}: {row.Status}, {row.LookupMilliseconds:0.0} ms, ready {row.ReadyMilliseconds:0.0} ms, {row.ViewerCount} viewers.");
return rows.All(row => row.Status == nameof(BrowseResultStatus.Available)) ? 0 : 1;

sealed class ProbeLogger : IAppLogger
{
    public ConcurrentQueue<string> Messages { get; } = new();
    public event EventHandler<LogEntry>? EntryWritten;
    public void Write(AppLogLevel level, string source, string message, Exception? exception = null)
    {
        // Record only count telemetry, never settings, tokens, or HTTP headers.
        if (source == "Browse" && message.StartsWith("Loaded", StringComparison.Ordinal)) Messages.Enqueue(message);
        EntryWritten?.Invoke(this, new LogEntry(DateTimeOffset.Now, level, source, message));
    }
}

sealed class CountingHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    private int streamRequests;
    private int aggregateRequests;
    public int StreamRequests => Volatile.Read(ref streamRequests);
    public int AggregateRequests => Volatile.Read(ref aggregateRequests);
    public void Reset() { streamRequests = 0; aggregateRequests = 0; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri?.AbsolutePath == "/helix/streams") Interlocked.Increment(ref streamRequests);
        if (request.RequestUri?.Host == "gql.twitch.tv") Interlocked.Increment(ref aggregateRequests);
        return base.SendAsync(request, cancellationToken);
    }
}
