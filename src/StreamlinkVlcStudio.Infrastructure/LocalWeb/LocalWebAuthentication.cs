using System.Net;
using System.Security.Cryptography;
using StreamlinkVlcStudio.Infrastructure.Threading;

namespace StreamlinkVlcStudio.Infrastructure.LocalWeb;

internal sealed class LocalWebAuthentication(Func<DateTimeOffset>? clock = null)
{
    private readonly object gate = new();
    private readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.UtcNow);
    private readonly Dictionary<string, (DateTimeOffset Start, int Count)> attempts = new();
    private DateTimeOffset globalStart;
    private int globalCount;
    internal Dictionary<string, LocalWebSession> Sessions { get; } = new(StringComparer.Ordinal);
    internal object Gate => gate;

    internal bool TryConsumeLoginAttempt(IPAddress address)
    {
        lock (gate)
        {
            var time = now();
            var key = address.ToString();
            foreach (var old in attempts.Where(p => time - p.Value.Start >= TimeSpan.FromMinutes(5)).Select(p => p.Key).ToArray())
                attempts.Remove(old);
            if (time - globalStart >= TimeSpan.FromMinutes(5)) { globalStart = time; globalCount = 0; }
            var entry = attempts.GetValueOrDefault(key, (Start: time, Count: 0));
            if (entry.Count >= 5 || globalCount >= 30 || attempts.Count >= 256 && !attempts.ContainsKey(key)) return false;
            // Successful attempts also consume the budget, preventing session oracle floods.
            attempts[key] = (entry.Start, entry.Count + 1);
            globalCount++;
            return true;
        }
    }

    internal LocalWebSession? Find(string? token, IPAddress address)
    {
        lock (gate)
        {
            if (token is null || token.Length != 64 || !Sessions.TryGetValue(token, out var session) ||
                !session.Address.Equals(address) || session.IsExpired(now())) return null;
            session.LastSeen = now();
            return session;
        }
    }

    internal static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
}

internal sealed class LocalWebSession(IPAddress address, string userId, string username, string credentialVersion,
    ILocalWebWorkspace workspace, DateTimeOffset created) : IAsyncDisposable
{
    private readonly DateTimeOffset createdAt = created;
    private readonly object disposalGate = new();
    private bool disposed;
    private Task? disposalTask;
    internal string Token { get; } = LocalWebAuthentication.NewToken();
    internal string Csrf { get; } = LocalWebAuthentication.NewToken();
    internal IPAddress Address { get; } = address;
    internal string UserId { get; } = userId;
    internal string Username { get; } = username;
    internal string CredentialVersion { get; } = credentialVersion;
    internal DateTimeOffset LastSeen { get; set; } = created;
    internal ILocalWebWorkspace Workspace { get; } = workspace;
    internal CancellationTokenSource Lifetime { get; } = new();
    internal SemaphoreSlim Requests { get; } = new(64, 64);
    internal SemaphoreSlim Commands { get; } = new(1, 1);
    internal bool IsExpired(DateTimeOffset now) => Volatile.Read(ref disposed) || Lifetime.IsCancellationRequested ||
        now - createdAt >= TimeSpan.FromHours(8) || now - LastSeen >= TimeSpan.FromMinutes(30);

    public ValueTask DisposeAsync() => new(AsyncDisposal.Begin(disposalGate, ref disposed, ref disposalTask, DisposeCoreAsync));

    private async Task DisposeCoreAsync()
    {
        try { await Lifetime.CancelAsync().ConfigureAwait(false); }
        finally { await Workspace.DisposeAsync().ConfigureAwait(false); }
        // Active requests can still be unwinding; their semaphore/CTS objects are GC-owned.
    }
}
