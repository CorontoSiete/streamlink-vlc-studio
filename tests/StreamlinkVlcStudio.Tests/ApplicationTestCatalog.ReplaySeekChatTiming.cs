internal static partial class ApplicationTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> ReplaySeekChatTimingTests { get; } =
    [
        ("replay seek chat timing: late pages respect a seek on an empty timeline", () =>
        {
            var timeline = new VodChatTimeline();
            timeline.MoveCursorTo(TimeSpan.FromMinutes(49.5));
            timeline.AddRange([
                VodChatTestMessage(TimeSpan.FromMinutes(10), "early"),
                VodChatTestMessage(TimeSpan.FromMinutes(50), "late")], 100);
            var due = timeline.TakeMessagesDueAt(TimeSpan.FromMinutes(50), 100);
            Assert.Equal(1, due.Count);
            Assert.Equal("late", due[0].Message);
            return Task.CompletedTask;
        }),
        ("replay seek chat timing: a seek beyond cached chat retains late pages for backward seeking", () =>
        {
            var timeline = new VodChatTimeline();
            timeline.Add(VodChatTestMessage(TimeSpan.FromMinutes(5), "cached"), 100);
            timeline.MoveCursorTo(TimeSpan.FromMinutes(49.5));
            timeline.AddRange([
                VodChatTestMessage(TimeSpan.FromMinutes(10), "early"),
                VodChatTestMessage(TimeSpan.FromMinutes(50), "late")], 100);
            var due = timeline.TakeMessagesDueAt(TimeSpan.FromMinutes(50), 100);
            Assert.Equal(1, due.Count);
            Assert.Equal("late", due[0].Message);
            timeline.MoveCursorTo(TimeSpan.FromMinutes(9.5));
            due = timeline.TakeMessagesDueAt(TimeSpan.FromMinutes(10), 100);
            Assert.Equal(1, due.Count);
            Assert.Equal("early", due[0].Message);
            return Task.CompletedTask;
        }),
        ("replay seek chat timing: clearing a session resets its seek boundary", () =>
        {
            var timeline = new VodChatTimeline();
            timeline.MoveCursorTo(TimeSpan.FromMinutes(50));
            timeline.Clear();
            timeline.Add(VodChatTestMessage(TimeSpan.Zero, "new session"), 100);
            Assert.Equal("new session", timeline.TakeMessagesDueAt(TimeSpan.Zero, 100).Single().Message);
            return Task.CompletedTask;
        })
    ];
}
