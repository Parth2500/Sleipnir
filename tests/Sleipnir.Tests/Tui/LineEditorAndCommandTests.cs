using Sleipnir.UI;

namespace Sleipnir.Tests.Tui;

public class LineEditorTests
{
    private static LineEditor Typed(string text)
    {
        var editor = new LineEditor();
        foreach (var key in Keys.Type(text)) editor.Handle(key);
        return editor;
    }

    [Fact]
    public void Typing_inserts_characters_at_the_cursor()
    {
        var editor = Typed("helo");
        editor.Handle(Keys.Left);
        editor.Handle(Keys.Char('l'));

        Assert.Equal("hello", editor.Text);
        Assert.Equal(4, editor.Cursor);
    }

    [Fact]
    public void Backspace_and_delete_remove_around_the_cursor()
    {
        var editor = Typed("abcd");
        editor.Handle(Keys.Left);
        editor.Handle(Keys.Backspace); // removes c
        editor.Handle(Keys.Delete);    // removes d

        Assert.Equal("ab", editor.Text);
        Assert.Equal(2, editor.Cursor);
    }

    [Fact]
    public void Home_end_and_arrows_move_without_leaving_the_text()
    {
        var editor = Typed("abc");
        editor.Handle(Keys.Home);
        editor.Handle(Keys.Left);
        Assert.Equal(0, editor.Cursor);
        editor.Handle(Keys.End);
        editor.Handle(Keys.Right);
        Assert.Equal(3, editor.Cursor);
        editor.Handle(Keys.Ctrl(ConsoleKey.A));
        Assert.Equal(0, editor.Cursor);
        editor.Handle(Keys.Ctrl(ConsoleKey.E));
        Assert.Equal(3, editor.Cursor);
    }

    [Fact]
    public void Ctrl_shortcuts_delete_words_and_lines()
    {
        var editor = Typed("get http://a.test/x");
        editor.Handle(Keys.Ctrl(ConsoleKey.W));
        Assert.Equal("get ", editor.Text);

        editor = Typed("abc def");
        editor.Handle(Keys.Left); editor.Handle(Keys.Left); editor.Handle(Keys.Left);
        editor.Handle(Keys.Ctrl(ConsoleKey.K));
        Assert.Equal("abc ", editor.Text);
        editor.Handle(Keys.Ctrl(ConsoleKey.U));
        Assert.Equal("", editor.Text);
    }

    [Fact]
    public void Non_editing_keys_are_left_to_the_caller()
    {
        var editor = Typed("x");

        Assert.False(editor.Handle(Keys.Enter));
        Assert.False(editor.Handle(Keys.Escape));
        Assert.False(editor.Handle(Keys.Up));
        Assert.False(editor.Handle(Keys.Tab));
        Assert.False(editor.Handle(Keys.Ctrl(ConsoleKey.C)));
        Assert.Equal("x", editor.Text);
    }

    [Fact]
    public void SetText_puts_the_cursor_at_the_end()
    {
        var editor = new LineEditor();
        editor.SetText("/pause ");

        Assert.Equal("/pause ", editor.Text);
        Assert.Equal(7, editor.Cursor);
    }
}

public class SlashCommandTests
{
    [Fact]
    public void A_bare_slash_suggests_every_command()
    {
        Assert.Equal(SlashCommands.All.Count, SlashCommands.Suggest("/").Count);
        Assert.True(SlashCommands.All.Count >= 7);
    }

    [Theory]
    [InlineData("/pa", SlashCommandKind.Pause)]
    [InlineData("/RE", SlashCommandKind.Resume)]
    [InlineData("/q", SlashCommandKind.Quit)]
    public void Suggestions_match_by_prefix_ignoring_case(string input, SlashCommandKind expected)
    {
        Assert.Equal(expected, Assert.Single(SlashCommands.Suggest(input)).Kind);
    }

    [Theory]
    [InlineData("http://example.test/a")]
    [InlineData("/pause now")]
    [InlineData("")]
    public void No_suggestions_for_urls_or_once_arguments_start(string input)
    {
        Assert.Empty(SlashCommands.Suggest(input));
    }

    [Fact]
    public void Parse_splits_the_command_and_its_arguments()
    {
        Assert.True(SlashCommands.TryParse("/add  http://example.test/a.iso ", out var command, out var args));
        Assert.Equal(SlashCommandKind.Add, command!.Kind);
        Assert.Equal("http://example.test/a.iso", args);
    }

    [Fact]
    public void Unambiguous_prefixes_parse_and_ambiguous_or_unknown_ones_do_not()
    {
        Assert.True(SlashCommands.TryParse("/pa", out var command, out _));
        Assert.Equal(SlashCommandKind.Pause, command!.Kind);

        Assert.False(SlashCommands.TryParse("/c", out _, out _));     // /cancel or /clear?
        Assert.False(SlashCommands.TryParse("/nope", out _, out _));
        Assert.False(SlashCommands.TryParse("pause", out _, out _));
    }
}
