using Sleipnir.Core.Models;
using Sleipnir.UI;

namespace Sleipnir.Tests.Tui;

public class TuiControllerTests
{
    private const string Out = "/home/me/Downloads";

    private DateTime _now = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    private readonly StubDownloadService _service = new();

    private TuiController Create() => new(_service, Out, () => _now);

    private static async Task Press(TuiController ui, params ConsoleKeyInfo[] keys)
    {
        foreach (var key in keys) await ui.HandleKeyAsync(key);
    }

    private static Task TypeText(TuiController ui, string text) => Press(ui, Keys.Type(text).ToArray());

    [Fact]
    public async Task Typing_a_url_and_pressing_enter_adds_and_selects_it()
    {
        var ui = Create();

        await TypeText(ui, "https://example.test/files/ubuntu.iso\n");

        Assert.Equal("add https://example.test/files/ubuntu.iso -> " + Out, Assert.Single(_service.Calls));
        Assert.Equal("", ui.Input.Text);
        Assert.Equal(_service.Tasks.Single().Id, ui.SelectedId);
        Assert.Contains("ubuntu.iso", ui.Toast!.Message);
        Assert.Equal(ToastKind.Info, ui.Toast.Kind);
    }

    [Fact]
    public async Task Several_pasted_urls_are_all_added()
    {
        var ui = Create();

        await TypeText(ui, "https://a.test/1.zip https://a.test/2.zip\nhttps://a.test/3.zip\n");

        Assert.Equal(3, _service.Calls.Count(c => c.StartsWith("add ")));
    }

    [Fact]
    public async Task Text_that_is_not_a_url_is_kept_with_an_error()
    {
        var ui = Create();

        await TypeText(ui, "ftp//oops\n");

        Assert.Empty(_service.Calls);
        Assert.Equal("ftp//oops", ui.Input.Text);
        Assert.Equal(ToastKind.Error, ui.Toast!.Kind);
    }

    [Fact]
    public async Task Downloads_are_listed_newest_first_and_the_first_is_selected()
    {
        _service.Seed("old.zip", DownloadStatus.Completed);
        _service.Seed("new.zip", DownloadStatus.Downloading);
        var ui = Create();

        Assert.Equal(new[] { "new.zip", "old.zip" }, ui.Downloads.Select(d => d.Filename));
        await Press(ui, Keys.Down);
        Assert.Equal("old.zip", ui.Downloads.Single(d => d.Id == ui.SelectedId).Filename);
        await Press(ui, Keys.Down); // clamps at the end
        Assert.Equal("old.zip", ui.Downloads.Single(d => d.Id == ui.SelectedId).Filename);
        await Press(ui, Keys.Up, Keys.Up);
        Assert.Equal("new.zip", ui.Downloads.Single(d => d.Id == ui.SelectedId).Filename);
    }

    [Fact]
    public async Task By_default_the_selection_lands_on_a_download_that_still_needs_attention()
    {
        _service.Seed("still-going.iso", DownloadStatus.Downloading);
        _service.Seed("newest-but-done.zip", DownloadStatus.Completed);
        var ui = Create();

        Assert.Equal("still-going.iso", ui.Selected!.Filename);

        await Press(ui, Keys.Up); // an explicit choice sticks
        Assert.Equal("newest-but-done.zip", ui.Selected!.Filename);
    }

    [Fact]
    public async Task Space_toggles_pause_and_resume_when_the_input_is_empty()
    {
        _service.Seed("a.iso", DownloadStatus.Downloading);
        var ui = Create();

        await Press(ui, Keys.Space);
        await Press(ui, Keys.Space);

        Assert.Equal(new[] { "pause a.iso", "resume a.iso" }, _service.Calls);
    }

    [Fact]
    public async Task Space_is_just_a_space_while_typing()
    {
        _service.Seed("a.iso", DownloadStatus.Downloading);
        var ui = Create();

        await TypeText(ui, "/add x");
        await Press(ui, Keys.Home, Keys.Right, Keys.Right, Keys.Right, Keys.Right, Keys.Space);

        Assert.Empty(_service.Calls);
        Assert.Equal("/add  x", ui.Input.Text);
    }

    [Fact]
    public async Task Enter_on_an_empty_input_expands_the_selected_download_and_escape_collapses_it()
    {
        var task = _service.Seed("a.iso", DownloadStatus.Downloading);
        var ui = Create();

        await Press(ui, Keys.Enter);
        Assert.Equal(task.Id, ui.ExpandedId);
        await Press(ui, Keys.Escape);
        Assert.Null(ui.ExpandedId);
    }

    [Fact]
    public async Task Delete_cancels_only_after_confirmation()
    {
        _service.Seed("a.iso", DownloadStatus.Downloading);
        var ui = Create();

        await Press(ui, Keys.Delete);
        Assert.Empty(_service.Calls);
        Assert.Contains("again", ui.Toast!.Message);

        await Press(ui, Keys.Down); // any other key withdraws the request
        await Press(ui, Keys.Delete);
        Assert.Empty(_service.Calls);

        await Press(ui, Keys.Delete);
        Assert.Equal("cancel a.iso", Assert.Single(_service.Calls));
    }

    [Fact]
    public async Task Ctrl_c_clears_typed_text_first()
    {
        var ui = Create();
        await TypeText(ui, "half typed");

        await Press(ui, Keys.Ctrl(ConsoleKey.C));

        Assert.Equal("", ui.Input.Text);
        Assert.False(ui.QuitRequested);
    }

    [Fact]
    public async Task Ctrl_c_twice_in_quick_succession_quits()
    {
        var ui = Create();

        await Press(ui, Keys.Ctrl(ConsoleKey.C));
        Assert.False(ui.QuitRequested);
        Assert.Contains("Ctrl+C again", ui.Toast!.Message);

        _now = _now.AddSeconds(3); // too slow: starts over
        await Press(ui, Keys.Ctrl(ConsoleKey.C));
        Assert.False(ui.QuitRequested);

        _now = _now.AddSeconds(1);
        await Press(ui, Keys.Ctrl(ConsoleKey.C));
        Assert.True(ui.QuitRequested);
    }

    [Fact]
    public async Task Slash_shows_suggestions_that_arrows_move_through_and_tab_completes()
    {
        _service.Seed("a.iso", DownloadStatus.Downloading);
        _service.Seed("b.iso", DownloadStatus.Downloading);
        var ui = Create();
        var selected = ui.SelectedId;

        await TypeText(ui, "/");
        Assert.Equal(SlashCommands.All.Count, ui.Suggestions.Count);

        await Press(ui, Keys.Down, Keys.Down);
        Assert.Equal(2, ui.SuggestionIndex);
        Assert.Equal(selected, ui.SelectedId); // arrows drive the menu, not the list

        await Press(ui, Keys.Tab);
        Assert.Equal("/" + SlashCommands.All[2].Name + " ", ui.Input.Text);
        Assert.Empty(ui.Suggestions);
    }

    [Fact]
    public async Task Enter_runs_the_highlighted_command_on_the_selected_download()
    {
        _service.Seed("a.iso", DownloadStatus.Downloading);
        var ui = Create();

        await TypeText(ui, "/pa\n");

        Assert.Equal("pause a.iso", Assert.Single(_service.Calls));
        Assert.Equal("", ui.Input.Text);
    }

    [Fact]
    public async Task Add_command_adds_its_url()
    {
        var ui = Create();

        await TypeText(ui, "/add https://a.test/x.bin\n");

        Assert.StartsWith("add https://a.test/x.bin", Assert.Single(_service.Calls));
    }

    [Fact]
    public async Task Clear_removes_only_finished_downloads()
    {
        _service.Seed("done.zip", DownloadStatus.Completed);
        _service.Seed("gone.zip", DownloadStatus.Cancelled);
        _service.Seed("broken.zip", DownloadStatus.Failed);
        _service.Seed("running.zip", DownloadStatus.Downloading);
        var ui = Create();

        await TypeText(ui, "/clear\n");

        Assert.Equal(new[] { "remove done.zip", "remove gone.zip" }, _service.Calls.OrderBy(c => c));
        Assert.Contains("2", ui.Toast!.Message);
    }

    [Fact]
    public async Task Unknown_command_is_an_error()
    {
        var ui = Create();

        await TypeText(ui, "/frobnicate\n");

        Assert.Equal(ToastKind.Error, ui.Toast!.Kind);
        Assert.Empty(_service.Calls);
    }

    [Fact]
    public async Task Question_mark_opens_help_only_when_not_typing_and_any_key_closes_it()
    {
        var ui = Create();

        await TypeText(ui, "?");
        Assert.True(ui.ShowHelp);
        Assert.Equal("", ui.Input.Text);

        await Press(ui, Keys.Char('x'));
        Assert.False(ui.ShowHelp);
        Assert.Equal("", ui.Input.Text); // the closing key is swallowed

        await TypeText(ui, "a?b");
        Assert.False(ui.ShowHelp);
        Assert.Equal("a?b", ui.Input.Text);
    }

    [Fact]
    public async Task Help_and_quit_commands()
    {
        var ui = Create();

        await TypeText(ui, "/help\n");
        Assert.True(ui.ShowHelp);
        await Press(ui, Keys.Escape);

        await TypeText(ui, "/quit\n");
        Assert.True(ui.QuitRequested);
    }

    [Fact]
    public async Task Commands_that_need_a_download_say_so_when_there_is_none()
    {
        var ui = Create();

        await TypeText(ui, "/pause\n");

        Assert.Equal(ToastKind.Error, ui.Toast!.Kind);
        Assert.Empty(_service.Calls);
    }

    [Fact]
    public async Task Toasts_disappear_after_a_few_seconds()
    {
        var ui = Create();
        await TypeText(ui, "nonsense\n");
        Assert.NotNull(ui.Toast);

        _now = _now.AddSeconds(5);

        Assert.Null(ui.Toast);
    }
}
