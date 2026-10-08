internal static partial class CodeCleanupTestCatalog
{
    private static readonly (string Opening, string ContentFence, string Closing)[] ReleaseNotesFenceCases =
    [
        ("~~~text", "```", "~~~"),
        ("````text", "```", "`````"),
        ("```text", "``` still code", "```"),
        ("~~~~text", "~~~", "~~~~")
    ];

    private static Task ReleaseNotesFenceSplittingAsync()
    {
        foreach (var (opening, contentFence, closing) in ReleaseNotesFenceCases)
        {
            var sample = $"{opening}\n{contentFence}\n## Updating\nLiteral sample instructions.\n{closing}";
            var notes = new ReleaseNotes(new Version(1, 8, 5),
                $"# Notes\n\n{sample}\n\n## Updating\n\nInstall the release.\n");
            Assert.Contains(sample, notes.ChangesMarkdown);
            Assert.Equal("Install the release.", notes.UpdatingMarkdown);
            Assert.Contains(sample, notes.BodyMarkdown);
            Assert.DoesNotContain("Install the release.", notes.BodyMarkdown);
        }

        var unclosed = new ReleaseNotes(new Version(1, 8, 5), "# Notes\n\n~~~text\n## Updating\nLiteral sample.");
        Assert.Equal(unclosed.Markdown, unclosed.ChangesMarkdown);
        Assert.Equal(false, unclosed.HasUpdatingInstructions);

        foreach (var invalidOpening in new[] { "``text", "~~text", "    ```text", "```text`invalid" })
        {
            var notes = new ReleaseNotes(new Version(1, 8, 5),
                $"# Notes\n\n{invalidOpening}\n\n## Updating\nInstall the release.");
            Assert.Equal("Install the release.", notes.UpdatingMarkdown);
        }
        return Task.CompletedTask;
    }

    private static Task ReleaseNotesFenceRenderingAsync() => TestSta.RunOffscreenAsync(() =>
    {
        foreach (var (opening, contentFence, closing) in ReleaseNotesFenceCases)
        {
            var literal = $"{contentFence}\n## Updating\n**literal emphasis**\n[example](https://example.com)";
            var control = new ReleaseNotesText { Markdown = $"{opening}\n{literal}\n{closing}" };
            var paragraph = control.Document.Blocks.OfType<Paragraph>().Single();
            Assert.Equal("Consolas", paragraph.FontFamily.Source);
            Assert.Equal(literal, paragraph.Inlines.OfType<Run>().Single().Text);
        }
        return Task.CompletedTask;
    });

    private static Task ReleaseNotesCodeWhitespaceAsync() => TestSta.RunOffscreenAsync(() =>
    {
        foreach (var indentation in new[] { "", "  ", "   " })
        {
            var sample = $"{indentation}```text\n\n\n{indentation}  value\n\n{indentation}```";
            var notes = new ReleaseNotes(new Version(1, 8, 5),
                $"# Notes\n\n{sample}\n\n## Updating\n\n{sample}\n");
            foreach (var markdown in new[] { sample, notes.BodyMarkdown, notes.UpdatingMarkdown })
            {
                var control = new ReleaseNotesText { Markdown = markdown };
                var paragraph = control.Document.Blocks.OfType<Paragraph>().Single();
                Assert.Equal("\n\n  value\n", paragraph.Inlines.OfType<Run>().Single().Text);
            }
        }

        var unclosed = new ReleaseNotes(new Version(1, 8, 5), "# Notes\n\n~~~text\nvalue\n\n");
        var unclosedControl = new ReleaseNotesText { Markdown = unclosed.BodyMarkdown };
        Assert.Equal("value\n\n", unclosedControl.Document.Blocks.OfType<Paragraph>().Single().Inlines.OfType<Run>().Single().Text);
        return Task.CompletedTask;
    });
}
