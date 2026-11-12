using DotNetDownloadManager.Core.Interfaces;
using DotNetDownloadManager.Core.Models;
using DotNetDownloadManager.UI;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace DotNetDownloadManager.Tests;

public class DashboardRendererTests
{
    private static string Render(IRenderable renderable)
    {
        var writer = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(writer),
        });
        console.Profile.Width = 200;
        console.Write(renderable);
        return writer.ToString();
    }

    private static DownloadTask TaskNamed(string name, DownloadStatus status = DownloadStatus.Downloading, string? error = null) => new()
    {
        Url = "http://example.test/" + name,
        Filename = name,
        DestinationPath = "/tmp/" + name,
        TotalBytes = 100,
        Status = status,
        ErrorMessage = error,
    };

    [Fact]
    public void Filename_containing_markup_characters_renders_literally()
    {
        var task = TaskNamed("movie [1080p].mkv");

        var output = Render(DashboardRenderer.BuildCurrentDownloadPanel(new StubService(task), task.Id));

        Assert.Contains("movie [1080p].mkv", output);
    }

    [Fact]
    public void History_with_markup_characters_in_filename_renders_literally()
    {
        var task = TaskNamed("[red]x[/].zip", DownloadStatus.Completed);

        var output = Render(DashboardRenderer.BuildHistoryPanel(new StubService(task)));

        Assert.Contains("[red]x[/].zip", output);
    }

    [Fact]
    public void Error_message_containing_brackets_renders_literally()
    {
        var task = TaskNamed("a.zip", DownloadStatus.Failed, "Response status code [404] Not Found");

        var output = Render(DashboardRenderer.BuildCurrentDownloadPanel(new StubService(task), task.Id));

        Assert.Contains("[404]", output);
    }

    [Fact]
    public void Download_whose_size_is_not_known_yet_renders_without_crashing()
    {
        // Right after Add, before the server has answered, TotalBytes is 0 and there are no segments.
        var task = new DownloadTask { Url = "http://example.test/a.iso", Filename = "a.iso", Status = DownloadStatus.Queued };

        var output = Render(DashboardRenderer.BuildCurrentDownloadPanel(new StubService(task), task.Id));

        Assert.Contains("a.iso", output);
    }

    private sealed class StubService(params DownloadTask[] tasks) : IDownloadService
    {
        public Task<Guid> AddDownloadAsync(string url, string outputDirectory) => throw new NotSupportedException();
        public Task PauseDownloadAsync(Guid downloadId) => Task.CompletedTask;
        public Task ResumeDownloadAsync(Guid downloadId) => Task.CompletedTask;
        public Task CancelDownloadAsync(Guid downloadId) => Task.CompletedTask;
        public IEnumerable<DownloadTask> GetAllDownloads() => tasks;
        public DownloadTask? GetDownload(Guid downloadId) => tasks.FirstOrDefault(t => t.Id == downloadId);
    }
}
