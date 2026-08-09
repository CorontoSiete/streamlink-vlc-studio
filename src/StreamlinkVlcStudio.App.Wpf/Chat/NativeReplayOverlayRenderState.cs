namespace StreamlinkVlcStudio.App.Wpf.Chat;

internal sealed class NativeReplayOverlayRenderState
{
    private readonly object gate = new();
    private string frameKey = "";
    private TimeSpan animationClock;
    private long version;

    public long Version => Volatile.Read(ref version);

    public NativeReplayOverlayRenderPlan? BeginRender(
        string nextFrameKey,
        bool forceAnimationRepaint,
        TimeSpan repaintAnimationClock)
    {
        lock (gate)
        {
            if (string.Equals(frameKey, nextFrameKey, StringComparison.Ordinal))
            {
                if (!forceAnimationRepaint)
                {
                    return null;
                }

                animationClock = repaintAnimationClock;
                return new NativeReplayOverlayRenderPlan(
                    version,
                    frameKey,
                    animationClock,
                    VersionAdvanced: false);
            }

            frameKey = nextFrameKey;
            animationClock = repaintAnimationClock;
            return new NativeReplayOverlayRenderPlan(
                Interlocked.Increment(ref version),
                frameKey,
                animationClock,
                VersionAdvanced: true);
        }
    }

    public void InvalidateFrameKey()
    {
        lock (gate)
        {
            frameKey = "";
        }
    }

    public long Reset()
    {
        lock (gate)
        {
            frameKey = "";
            animationClock = TimeSpan.Zero;
            return Interlocked.Increment(ref version);
        }
    }

    public bool IsCurrent(long expectedVersion)
    {
        return expectedVersion == Version;
    }

    public bool IsCurrent(long expectedVersion, string expectedFrameKey)
    {
        lock (gate)
        {
            return version == expectedVersion &&
                string.Equals(frameKey, expectedFrameKey, StringComparison.Ordinal);
        }
    }
}

internal readonly record struct NativeReplayOverlayRenderPlan(
    long Version,
    string FrameKey,
    TimeSpan AnimationClock,
    bool VersionAdvanced);
