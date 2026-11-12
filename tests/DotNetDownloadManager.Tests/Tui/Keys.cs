namespace DotNetDownloadManager.Tests.Tui;

/// <summary>Builds <see cref="ConsoleKeyInfo"/> values the way a terminal delivers them.</summary>
public static class Keys
{
    public static ConsoleKeyInfo Char(char c) => new(c, c == ' ' ? ConsoleKey.Spacebar : ConsoleKey.NoName, char.IsUpper(c), false, false);
    public static IEnumerable<ConsoleKeyInfo> Type(string text) => text.Select(c => c == '\n' ? Enter : Char(c));

    public static ConsoleKeyInfo Enter => new('\r', ConsoleKey.Enter, false, false, false);
    public static ConsoleKeyInfo Escape => new('\x1b', ConsoleKey.Escape, false, false, false);
    public static ConsoleKeyInfo Tab => new('\t', ConsoleKey.Tab, false, false, false);
    public static ConsoleKeyInfo Space => new(' ', ConsoleKey.Spacebar, false, false, false);
    public static ConsoleKeyInfo Backspace => new('\b', ConsoleKey.Backspace, false, false, false);
    public static ConsoleKeyInfo Delete => new('\0', ConsoleKey.Delete, false, false, false);
    public static ConsoleKeyInfo Left => new('\0', ConsoleKey.LeftArrow, false, false, false);
    public static ConsoleKeyInfo Right => new('\0', ConsoleKey.RightArrow, false, false, false);
    public static ConsoleKeyInfo Up => new('\0', ConsoleKey.UpArrow, false, false, false);
    public static ConsoleKeyInfo Down => new('\0', ConsoleKey.DownArrow, false, false, false);
    public static ConsoleKeyInfo Home => new('\0', ConsoleKey.Home, false, false, false);
    public static ConsoleKeyInfo End => new('\0', ConsoleKey.End, false, false, false);
    public static ConsoleKeyInfo Ctrl(ConsoleKey key) => new((char)(key - ConsoleKey.A + 1), key, false, false, true);
}
