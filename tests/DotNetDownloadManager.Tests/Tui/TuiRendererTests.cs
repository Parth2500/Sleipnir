using DotNetDownloadManager.Core.Models;
using DotNetDownloadManager.UI;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace DotNetDownloadManager.Tests.Tui;

public class TuiRendererTests
{
    private readonly StubDownloadService _service = new();
    private readonly SpeedTracker _speeds = new();

    private TuiController Ui() => new(_service, "/downloads", () => DateTime.UtcNow);

    private static string Render(IRenderable renderable, int width = 100)
    {
        var writer = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(writer),
        });
        console.Profile.Width = width;
        console.Write(renderable);
        return writer.ToString();
    }

    private string Screen(TuiController ui, int width = 100, int height = 40) =>
        Render(TuiRenderer.Render(ui, _speeds, width, height), width);

    [Fact]
    public void Empty_state_invites_pasting_a_url()
    {
        var output = Screen(Ui());

        Assert.Contains("Sleipnir", output);
        Assert.Contains("No downloads yet", output);
        Assert.Contains("Paste a URL", output);
        Assert.Contains("? for help", output);
    }

    [Fact]
    public void Each_download_is_one_row_with_progress_and_the_selected_one_is_marked()
    {
        _service.Seed("old.zip", DownloadStatus.Completed, 2048, 2048);
        _service.Seed("ubuntu.iso", DownloadStatus.Downloading, 1000, 580);

        var lines = Screen(Ui()).Split('\n');

        var ubuntu = Assert.Single(lines, l => l.Contains("ubuntu.iso"));
        Assert.Contains("58%", ubuntu);
        Assert.Contains("›", ubuntu); // newest first, so it is selected
        var old = Assert.Single(lines, l => l.Contains("old.zip"));
        Assert.Contains("done", old);
        Assert.DoesNotContain("›", old);
    }

    [Fact]
    public void Running_download_shows_speed_and_eta()
    {
        var task = _service.Seed("big.iso", DownloadStatus.Downloading, 10 * 1024 * 1024, 0);
        var t0 = DateTime.UtcNow;
        _speeds.Update(_service.Tasks, t0);
        task.Segments[0].BytesDownloaded = 2 * 1024 * 1024;
        _speeds.Update(_service.Tasks, t0.AddSeconds(2)); // 1 MB/s, 8 MB left

        var row = Assert.Single(Screen(Ui()).Split('\n'), l => l.Contains("big.iso"));

        Assert.Contains("1 MB/s", row);
        Assert.Contains("0:08", row);
    }

    [Fact]
    public void Header_summarises_activity()
    {
        _service.Seed("a", DownloadStatus.Downloading);
        _service.Seed("b", DownloadStatus.Queued);
        _service.Seed("c", DownloadStatus.Completed);

        var header = Screen(Ui()).Split('\n').First(l => l.Contains("Sleipnir"));

        Assert.Contains("2 active", header);
    }

    [Fact]
    public void Markup_characters_in_names_and_errors_render_literally()
    {
        var task = _service.Seed("movie [1080p].mkv", DownloadStatus.Failed);
        task.ErrorMessage = "HTTP [404] Not Found";
        var ui = Ui();

        var output = Screen(ui);
        Assert.Contains("movie [1080p].mkv", output);
        Assert.Contains("[404]", output);
    }

    [Fact]
    public async Task Expanded_download_shows_its_details()
    {
        var task = _service.Seed("a.iso", DownloadStatus.Downloading, 1000, 500);
        var ui = Ui();
        await ui.HandleKeyAsync(Keys.Enter);

        var output = Screen(ui);

        Assert.Contains("/downloads/a.iso", output);
        Assert.Contains(task.Url, output);
        Assert.Contains("Seg 1", output);
    }

    [Fact]
    public async Task Segments_waiting_to_retry_say_so_instead_of_showing_zero()
    {
        var task = _service.Seed("a.iso", DownloadStatus.Downloading, 2000, 0);
        task.Segments = new[]
        {
            new DownloadSegment { Index = 0, StartOffset = 0, EndOffset = 999, BytesDownloaded = 500, Status = DownloadStatus.Downloading },
            new DownloadSegment { Index = 1, StartOffset = 1000, EndOffset = 1999, BytesDownloaded = 0, Status = DownloadStatus.Queued },
            new DownloadSegment { Index = 2, StartOffset = 2000, EndOffset = 2999, BytesDownloaded = 0, Status = DownloadStatus.Queued },
        };
        task.TotalBytes = 3000;
        var ui = Ui();
        await ui.HandleKeyAsync(Keys.Enter);

        var output = Screen(ui);

        Assert.Contains("Seg 1 50%", output);
        Assert.Contains("Seg 2 waiting", output);
        Assert.Contains("Seg 2 waiting  Seg 3", output); // cells never run into each other
    }

    [Fact]
    public void Paused_percentage_matches_the_details_by_never_rounding_up()
    {
        _service.Seed("a.iso", DownloadStatus.Paused, 1000, 598); // 59.8%

        var row = Assert.Single(Screen(Ui()).Split('\n'), l => l.Contains("a.iso"));

        Assert.Contains("paused at 59%", row);
    }

    [Fact]
    public void Unknown_size_shows_bytes_received()
    {
        var task = _service.Seed("stream.bin", DownloadStatus.Downloading);
        task.IsSizeKnown = false;
        task.TotalBytes = 0;
        task.Segments = new[] { new DownloadSegment { StartOffset = 0, EndOffset = long.MaxValue - 1, IsOpenEnded = true, BytesDownloaded = 2048 } };

        var row = Assert.Single(Screen(Ui()).Split('\n'), l => l.Contains("stream.bin"));

        Assert.Contains("2 KB", row);
        Assert.DoesNotContain("%", row);
    }

    [Fact]
    public async Task Typed_text_and_slash_suggestions_appear_above_the_input()
    {
        var ui = Ui();
        foreach (var k in Keys.Type("/pa")) await ui.HandleKeyAsync(k);

        var output = Screen(ui);

        Assert.Contains("/pa", output);
        Assert.Contains("/pause", output);
        Assert.Contains("Pause the selected download", output);
        Assert.DoesNotContain("/quit", output);
    }

    [Fact]
    public async Task Help_lists_keys_and_commands()
    {
        var ui = Ui();
        await ui.HandleKeyAsync(Keys.Char('?'));

        var output = Screen(ui);

        Assert.Contains("Ctrl+C", output);
        Assert.Contains("/clear", output);
        Assert.Contains("Space", output);
    }

    [Fact]
    public async Task Toast_is_shown()
    {
        var ui = Ui();
        foreach (var k in Keys.Type("nonsense\n")) await ui.HandleKeyAsync(k);

        Assert.Contains(ui.Toast!.Message, Screen(ui));
    }

    [Fact]
    public void Narrow_terminals_truncate_long_names_instead_of_breaking()
    {
        _service.Seed("a-really-long-file-name-that-does-not-fit-anywhere-at-all.tar.gz", DownloadStatus.Downloading, 1000, 300);

        var output = Screen(Ui(), width: 50);

        Assert.Contains("…", output);
        Assert.All(output.Split('\n'), line => Assert.True(line.TrimEnd().Length <= 50, $"line too long: '{line}'"));
    }

    [Fact]
    public async Task Long_lists_scroll_to_keep_the_selection_visible()
    {
        for (var i = 0; i < 40; i++) _service.Seed($"file{i:00}.bin", DownloadStatus.Completed);
        var ui = Ui();
        for (var i = 0; i < 35; i++) await ui.HandleKeyAsync(Keys.Down); // newest first: file39 … file04

        var output = Screen(ui, height: 20);

        Assert.Contains("file04.bin", output);
        Assert.DoesNotContain("file39.bin", output);
        Assert.Contains("more", output); // a hint that rows are hidden
    }

    [Theory]
    [InlineData(0, 30)]
    [InlineData(3, 30)]
    [InlineData(50, 24)]
    public async Task Screen_fits_the_terminal_height_with_the_hint_line_visible(int downloads, int height)
    {
        for (var i = 0; i < downloads; i++) _service.Seed($"f{i}.bin", DownloadStatus.Downloading, 1000, 100);
        var ui = Ui();
        foreach (var k in Keys.Type("/")) await ui.HandleKeyAsync(k); // suggestions make the footer taller

        var lines = Screen(ui, height: height).TrimEnd('\n').Split('\n');

        Assert.True(lines.Length < height, $"{lines.Length} lines for a {height}-line terminal");
        Assert.Contains("Tab complete", lines[^1]);
    }

    [Fact]
    public void Size_unknown_download_before_the_server_answers_renders()
    {
        _service.Tasks.Add(new DownloadTask { Url = "http://x.test/a", Filename = "a", Status = DownloadStatus.Queued });

        Assert.Contains("a", Screen(Ui()));
    }
}
