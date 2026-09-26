using System.Text;
using System.Text.RegularExpressions;

namespace INTVoiceToText.Services;

/// <summary>Offline post-processing dictionary for common Whisper variants.</summary>
public sealed class WordLibrary
{
    private readonly List<(Regex Pattern, string Replacement)> _rules = new();
    private readonly string _path;
    private DateTime _lastWriteUtc;

    public WordLibrary()
    {
        _path = System.IO.Path.Combine(AppContext.BaseDirectory, "library_words", "words.txt");
        Load();
    }

    public int Count => _rules.Count;
    public string Path => _path;

    private void Load()
    {
        try
        {
            _rules.Clear();
            if (!File.Exists(_path)) return;
            _lastWriteUtc = File.GetLastWriteTimeUtc(_path);
            foreach (var raw in File.ReadLines(_path, Encoding.UTF8))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;

                var separator = line.IndexOf("=>", StringComparison.Ordinal);
                var separatorLength = 2;
                if (separator < 1)
                {
                    // Also accept the user-friendly format: "дейзавр - DayZavr".
                    separator = Regex.Match(line, @"\s+-\s+").Index;
                    separatorLength = separator > 0 ? Regex.Match(line, @"\s+-\s+").Length : 0;
                }
                if (separator < 1 || separatorLength == 0) continue;

                var source = line[..separator].Trim();
                var replacement = line[(separator + separatorLength)..].Trim();
                if (source.Length == 0 || replacement.Length == 0) continue;

                // Match whole words/phrases only, preserving surrounding punctuation.
                var pattern = $"(?<![\\p{{L}}\\p{{N}}_]){Regex.Escape(source)}(?![\\p{{L}}\\p{{N}}_])";
                _rules.Add((new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), replacement));
            }
            // Longer phrases must win before their shorter components.
            _rules.Sort((a, b) => b.Pattern.ToString().Length.CompareTo(a.Pattern.ToString().Length));
        }
        catch { /* dictionary is optional; recognition must continue without it */ }
    }

    public string Apply(string text)
    {
        try
        {
            if (File.Exists(_path) && File.GetLastWriteTimeUtc(_path) != _lastWriteUtc)
                Load(); // changes are picked up without restarting the application
        }
        catch { }
        if (string.IsNullOrEmpty(text) || _rules.Count == 0) return text;
        foreach (var (pattern, replacement) in _rules)
            text = pattern.Replace(text, replacement);
        return text;
    }
}
