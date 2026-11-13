using Sleipnir.Core.Interfaces;
using Sleipnir.Core.Models;

namespace Sleipnir.UI;

public enum ToastKind { Info, Error }

public sealed record Toast(string Message, ToastKind Kind, DateTime Until);

/// <summary>
/// The interactive state of the terminal UI and how keys change it. Rendering lives in <see cref="TuiRenderer"/>.
/// </summary>
/// <remarks>
/// Keys go to the input box, except when it is empty: then ↑↓ select, Space pauses/resumes,
/// Enter expands details, Del cancels (after confirmation) and ? shows help.
/// </remarks>
public sealed class TuiController
{
    private static readonly TimeSpan ToastDuration = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan ConfirmWindow = TimeSpan.FromSeconds(2);

    private readonly IDownloadService _service;
    private readonly string _outputDirectory;
    private readonly Func<DateTime> _clock;
    private Toast? _toast;
    private DateTime? _quitArmedAt;
    private Guid? _cancelArmedFor;

    public TuiController(IDownloadService service, string outputDirectory, Func<DateTime>? clock = null)
    {
        _service = service;
        _outputDirectory = outputDirectory;
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    public LineEditor Input { get; } = new();
    public Guid? ExpandedId { get; private set; }
    public bool ShowHelp { get; private set; }
    public bool QuitRequested { get; private set; }
    public int SuggestionIndex { get; private set; }

    private Guid? _selectedId;

    /// <summary>The selected download; defaults to the first one still in progress, else the first in the list.</summary>
    public Guid? SelectedId
    {
        get
        {
            var downloads = Downloads;
            if (_selectedId is { } id && downloads.Any(d => d.Id == id)) return id;
            // Nothing chosen yet: prefer something still in progress over finished history.
            var needsAttention = downloads.FirstOrDefault(d => d.Status is not (DownloadStatus.Completed or DownloadStatus.Cancelled));
            return (needsAttention ?? downloads.FirstOrDefault())?.Id;
        }
        private set => _selectedId = value;
    }

    public DownloadTask? Selected => SelectedId is { } id ? _service.GetDownload(id) : null;

    /// <summary>Current toast, or null if none or expired.</summary>
    public Toast? Toast => _toast is { } t && t.Until > _clock() ? t : null;

    /// <summary>Downloads in display order (newest first).</summary>
    public IReadOnlyList<DownloadTask> Downloads =>
        _service.GetAllDownloads().OrderByDescending(d => d.CreatedAt).ThenBy(d => d.Id).ToList();

    /// <summary>Slash-command suggestions for the current input.</summary>
    public IReadOnlyList<SlashCommand> Suggestions => SlashCommands.Suggest(Input.Text);

    public async Task HandleKeyAsync(ConsoleKeyInfo key)
    {
        var isCtrlC = key.Key == ConsoleKey.C && key.Modifiers.HasFlag(ConsoleModifiers.Control);
        if (!isCtrlC) _quitArmedAt = null;
        if (key.Key != ConsoleKey.Delete || !Input.IsEmpty) _cancelArmedFor = null;

        if (ShowHelp)
        {
            ShowHelp = false; // any key closes help
            return;
        }

        if (isCtrlC)
        {
            HandleCtrlC();
            return;
        }

        var suggestions = Suggestions;
        if (suggestions.Count > 0)
        {
            switch (key.Key)
            {
                case ConsoleKey.UpArrow:
                    SuggestionIndex = Math.Max(0, SuggestionIndex - 1);
                    return;
                case ConsoleKey.DownArrow:
                    SuggestionIndex = Math.Min(suggestions.Count - 1, SuggestionIndex + 1);
                    return;
                case ConsoleKey.Tab:
                    Input.SetText("/" + suggestions[Math.Min(SuggestionIndex, suggestions.Count - 1)].Name + " ");
                    SuggestionIndex = 0;
                    return;
            }
        }

        switch (key.Key)
        {
            case ConsoleKey.Enter:
                await SubmitAsync();
                return;
            case ConsoleKey.Escape:
                if (!Input.IsEmpty) Input.Clear();
                else ExpandedId = null;
                SuggestionIndex = 0;
                return;
        }

        if (Input.IsEmpty)
        {
            switch (key.Key)
            {
                case ConsoleKey.UpArrow: MoveSelection(-1); return;
                case ConsoleKey.DownArrow: MoveSelection(+1); return;
                case ConsoleKey.PageUp: MoveSelection(-10); return;
                case ConsoleKey.PageDown: MoveSelection(+10); return;
                case ConsoleKey.Spacebar: await TogglePauseAsync(); return;
                case ConsoleKey.Delete: await CancelWithConfirmationAsync(); return;
            }

            if (key.KeyChar == '?')
            {
                ShowHelp = true;
                return;
            }
        }

        var before = Input.Text;
        Input.Handle(key);
        if (Input.Text != before) SuggestionIndex = 0;
    }

    private void HandleCtrlC()
    {
        if (!Input.IsEmpty)
        {
            Input.Clear();
            SuggestionIndex = 0;
            return;
        }

        var now = _clock();
        if (_quitArmedAt is { } armed && now - armed <= ConfirmWindow)
        {
            QuitRequested = true;
            return;
        }

        _quitArmedAt = now;
        Show("Press Ctrl+C again to quit (downloads resume next time)", ToastKind.Info);
    }

    private async Task SubmitAsync()
    {
        var text = Input.Text.Trim();
        if (text.Length == 0)
        {
            ExpandedId = ExpandedId == SelectedId ? null : SelectedId;
            return;
        }

        if (text.StartsWith('/'))
        {
            var suggestions = Suggestions;
            SlashCommand? command;
            string args;
            if (!SlashCommands.TryParse(text, out command, out args) && suggestions.Count > 0)
            {
                command = suggestions[Math.Min(SuggestionIndex, suggestions.Count - 1)]; // run the highlighted one
                args = string.Empty;
            }

            if (command is null)
            {
                Show($"Unknown command {text.Split(' ')[0]}. Type / to see commands.", ToastKind.Error);
                return;
            }

            Input.Clear();
            SuggestionIndex = 0;
            await RunAsync(command, args);
            return;
        }

        if (await AddUrlsAsync(text)) Input.Clear();
    }

    /// <summary>Adds every URL in <paramref name="text"/> (whitespace-separated). Returns false if none was valid.</summary>
    private async Task<bool> AddUrlsAsync(string text)
    {
        var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var urls = parts.Where(IsDownloadableUrl).ToList();
        if (urls.Count == 0)
        {
            Show("That doesn't look like a URL. Paste an http:// or https:// link, or type / for commands.", ToastKind.Error);
            return false;
        }

        Guid? last = null;
        foreach (var url in urls)
        {
            last = await _service.AddDownloadAsync(url, _outputDirectory);
        }
        SelectedId = last;

        var skipped = parts.Length - urls.Count;
        var name = last is { } id ? _service.GetDownload(id)?.Filename : null;
        var message = urls.Count == 1 ? $"Added {name}" : $"Added {urls.Count} downloads";
        if (skipped > 0) message += $" ({skipped} ignored: not a URL)";
        Show(message, ToastKind.Info);
        return true;
    }

    private static bool IsDownloadableUrl(string text) =>
        Uri.TryCreate(text, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private async Task RunAsync(SlashCommand command, string args)
    {
        switch (command.Kind)
        {
            case SlashCommandKind.Add:
                if (!await AddUrlsAsync(args)) Input.SetText("/add " + args);
                return;
            case SlashCommandKind.Help:
                ShowHelp = true;
                return;
            case SlashCommandKind.Quit:
                QuitRequested = true;
                return;
            case SlashCommandKind.Clear:
                var finished = Downloads.Where(d => d.Status is DownloadStatus.Completed or DownloadStatus.Cancelled).ToList();
                foreach (var download in finished) await _service.RemoveDownloadAsync(download.Id);
                Show(finished.Count == 0 ? "Nothing to clear" : $"Cleared {finished.Count} finished download{(finished.Count == 1 ? "" : "s")}", ToastKind.Info);
                return;
        }

        if (Selected is not { } task)
        {
            Show("No download selected. Paste a URL to start one.", ToastKind.Error);
            return;
        }

        switch (command.Kind)
        {
            case SlashCommandKind.Pause:
                await _service.PauseDownloadAsync(task.Id);
                Show($"Paused {task.Filename}", ToastKind.Info);
                break;
            case SlashCommandKind.Resume:
                await _service.ResumeDownloadAsync(task.Id);
                Show($"Resumed {task.Filename}", ToastKind.Info);
                break;
            case SlashCommandKind.Cancel:
                await _service.CancelDownloadAsync(task.Id);
                Show($"Cancelled {task.Filename}", ToastKind.Info);
                break;
        }
    }

    private void MoveSelection(int delta)
    {
        var downloads = Downloads;
        if (downloads.Count == 0) return;
        var index = SelectedId is { } id ? downloads.ToList().FindIndex(d => d.Id == id) : 0;
        SelectedId = downloads[Math.Clamp(index + delta, 0, downloads.Count - 1)].Id;
    }

    private async Task TogglePauseAsync()
    {
        if (Selected is not { } task) return;
        switch (task.Status)
        {
            case DownloadStatus.Downloading or DownloadStatus.Queued:
                await _service.PauseDownloadAsync(task.Id);
                break;
            case DownloadStatus.Paused or DownloadStatus.Failed:
                await _service.ResumeDownloadAsync(task.Id);
                break;
        }
    }

    private async Task CancelWithConfirmationAsync()
    {
        if (Selected is not { } task || task.Status is DownloadStatus.Completed or DownloadStatus.Cancelled) return;

        if (_cancelArmedFor == task.Id)
        {
            _cancelArmedFor = null;
            await _service.CancelDownloadAsync(task.Id);
            Show($"Cancelled {task.Filename}", ToastKind.Info);
            return;
        }

        _cancelArmedFor = task.Id;
        Show($"Press Del again to cancel {task.Filename}", ToastKind.Info);
    }

    private void Show(string message, ToastKind kind) => _toast = new Toast(message, kind, _clock() + ToastDuration);
}
