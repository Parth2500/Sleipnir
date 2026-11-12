using System.Text;

namespace DotNetDownloadManager.UI;

/// <summary>
/// A single-line text input with a cursor and the usual terminal editing keys:
/// arrows, Home/End, Backspace/Delete, Ctrl+A/E (start/end), Ctrl+U/K (delete to start/end), Ctrl+W (delete word).
/// </summary>
public sealed class LineEditor
{
    private readonly StringBuilder _text = new();

    public string Text => _text.ToString();
    public int Cursor { get; private set; }
    public bool IsEmpty => _text.Length == 0;

    /// <summary>Applies an editing key. Returns false for keys that aren't editing keys (Enter, Esc, Tab, Up/Down, Ctrl+C…).</summary>
    public bool Handle(ConsoleKeyInfo key)
    {
        var ctrl = key.Modifiers.HasFlag(ConsoleModifiers.Control);

        if (ctrl)
        {
            switch (key.Key)
            {
                case ConsoleKey.A: Cursor = 0; return true;
                case ConsoleKey.E: Cursor = _text.Length; return true;
                case ConsoleKey.U: _text.Remove(0, Cursor); Cursor = 0; return true;
                case ConsoleKey.K: _text.Remove(Cursor, _text.Length - Cursor); return true;
                case ConsoleKey.W: DeleteWordBefore(); return true;
                default: return false;
            }
        }

        switch (key.Key)
        {
            case ConsoleKey.LeftArrow: Cursor = Math.Max(0, Cursor - 1); return true;
            case ConsoleKey.RightArrow: Cursor = Math.Min(_text.Length, Cursor + 1); return true;
            case ConsoleKey.Home: Cursor = 0; return true;
            case ConsoleKey.End: Cursor = _text.Length; return true;
            case ConsoleKey.Backspace:
                if (Cursor > 0) { _text.Remove(Cursor - 1, 1); Cursor--; }
                return true;
            case ConsoleKey.Delete:
                if (Cursor < _text.Length) _text.Remove(Cursor, 1);
                return true;
        }

        if (key.KeyChar != '\0' && !char.IsControl(key.KeyChar) && !key.Modifiers.HasFlag(ConsoleModifiers.Alt))
        {
            _text.Insert(Cursor, key.KeyChar);
            Cursor++;
            return true;
        }

        return false;
    }

    public void Clear()
    {
        _text.Clear();
        Cursor = 0;
    }

    public void SetText(string text)
    {
        _text.Clear().Append(text);
        Cursor = _text.Length;
    }

    private void DeleteWordBefore()
    {
        var start = Cursor;
        while (start > 0 && _text[start - 1] == ' ') start--;
        while (start > 0 && _text[start - 1] != ' ') start--;
        _text.Remove(start, Cursor - start);
        Cursor = start;
    }
}
