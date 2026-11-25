using System.Text;
using System.Text.RegularExpressions;

namespace AgentFlow.Speech;

/// <summary>
/// Names from a project (files, folders, classes, functions), so dictated words like "video studio view model"
/// come out as <c>VideoStudioViewModel</c> and "read me dot md" as <c>README.md</c>.
/// </summary>
public sealed partial class ProjectVocabulary
{
    private readonly Dictionary<string, string> _byWords = new(StringComparer.Ordinal); // "video studio view model" -> VideoStudioViewModel
    private int _longest;

    public int Count => _byWords.Count;

    /// <param name="names">Names that need at least three spoken words, so everyday two-word phrases
    /// ("main window") stay as they are.</param>
    /// <param name="keyNames">Names that may be matched from two words, such as the project's own name ("agent flow").</param>
    public ProjectVocabulary(IEnumerable<string> names, IEnumerable<string>? keyNames = null)
    {
        foreach (var name in keyNames ?? []) Add(name, minWords: 2);
        foreach (var name in names) Add(name, minWords: 3);
    }

    private void Add(string name, int minWords)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 80) return;
        foreach (var key in SpokenForms(name))
        {
            var words = key.Count(c => c == ' ') + 1;
            if (words < minWords) continue;
            _byWords.TryAdd(key, name);
            _longest = Math.Max(_longest, words);
        }
    }

    public static ProjectVocabulary Empty { get; } = new([]);

    /// <summary>How a name may be said: its word parts, and for a file name also with "dot" and the extension.</summary>
    internal static IEnumerable<string> SpokenForms(string name)
    {
        var ext = Path.GetExtension(name);
        var stem = ext.Length is > 1 and <= 6 && !name.StartsWith('.') ? name[..^ext.Length] : name;
        var parts = SplitWords(stem);
        if (parts.Count == 0) yield break;
        if (stem == name)
        {
            yield return string.Join(' ', parts);
        }
        else
        {
            var extWords = SplitWords(ext[1..]);
            yield return string.Join(' ', parts.Append("dot").Concat(extWords));
            if (parts.Count > 1) yield return string.Join(' ', parts) + " " + string.Join(' ', ["dot", .. extWords]);
        }
    }

    /// <summary>Splits PascalCase, camelCase, snake_case, kebab-case and digits into lower-case words.</summary>
    public static List<string> SplitWords(string s) =>
        WordPart().Matches(s).Select(m => m.Value.ToLowerInvariant()).Where(w => w.Length > 0).ToList();

    [GeneratedRegex(@"[A-Z]+(?![a-z])|[A-Z]?[a-z]+|\d+")]
    private static partial Regex WordPart();

    /// <summary>Replaces the longest runs of spoken words that match a project name.</summary>
    public string Apply(string text)
    {
        if (_byWords.Count == 0 || text.Length == 0) return text;
        var tokens = Tokenize(text);
        var sb = new StringBuilder(text.Length);
        var i = 0;
        while (i < tokens.Count)
        {
            var matched = false;
            if (tokens[i].IsWord)
            {
                for (var n = Math.Min(_longest, CountWords(tokens, i)); n >= 2 && !matched; n--)
                {
                    var (key, end, trailing) = Span(tokens, i, n);
                    if (key is null || !_byWords.TryGetValue(key, out var name)) continue;
                    sb.Append(name).Append(trailing);
                    i = end;
                    matched = true;
                }
            }
            if (!matched)
            {
                sb.Append(tokens[i].Text);
                i++;
            }
        }
        return sb.ToString();
    }

    private readonly record struct Token(string Text, bool IsWord);

    private static List<Token> Tokenize(string text) =>
        Tokens().Matches(text).Select(m => new Token(m.Value, char.IsLetterOrDigit(m.Value[0]))).ToList();

    [GeneratedRegex(@"[\p{L}\p{N}']+|[^\p{L}\p{N}']+")]
    private static partial Regex Tokens();

    private static int CountWords(List<Token> tokens, int start)
    {
        var n = 0;
        for (var i = start; i < tokens.Count; i++)
        {
            if (tokens[i].IsWord || tokens[i].Text == ".") n++; // a written dot stands for the spoken "dot"
            else if (!IsGap(tokens[i].Text)) break;
        }
        return n;
    }

    /// <summary>Spaces, a hyphen or a dot may sit between the words of one name ("view-model", "readme.md").</summary>
    private static bool IsGap(string s) => s is " " or "-" or "." or "_" or " - ";

    /// <summary>The key for n words from start, the token index after them, and any punctuation after the last word to keep.</summary>
    private static (string? Key, int End, string Trailing) Span(List<Token> tokens, int start, int n)
    {
        var words = new List<string>(n);
        var i = start;
        while (i < tokens.Count && words.Count < n)
        {
            var t = tokens[i];
            if (t.IsWord) words.Add(t.Text.ToLowerInvariant());
            else if (!IsGap(t.Text)) return (null, 0, "");
            else if (t.Text == "." && words.Count < n) words.Add("dot");
            i++;
            if (words.Count == n) break;
        }
        return words.Count == n ? (string.Join(' ', words), i, "") : (null, 0, "");
    }

    /// <summary>
    /// Reads names from a project folder: file and folder names, plus classes, functions and similar declared
    /// in source files. Skips build output and dependency folders; stops after a fixed number of files.
    /// </summary>
    public static ProjectVocabulary FromFolder(string folder, int maxFiles = 20000, int maxSourceFiles = 1500)
    {
        var projectName = Path.GetFileName(folder.TrimEnd('\\', '/'));
        var names = new HashSet<string>(StringComparer.Ordinal);
        var files = 0;
        var sources = 0;
        var stack = new Stack<(string Dir, int Depth)>();
        stack.Push((folder, 0));
        while (stack.Count > 0 && files < maxFiles)
        {
            var (dir, depth) = stack.Pop();
            IEnumerable<string> entries;
            try { entries = Directory.EnumerateFileSystemEntries(dir); }
            catch (Exception) { continue; }
            foreach (var path in entries)
            {
                var name = Path.GetFileName(path);
                if (Directory.Exists(path))
                {
                    if (SkippedFolders.Contains(name) || name.StartsWith('.')) continue;
                    names.Add(name);
                    if (depth < 8) stack.Push((path, depth + 1));
                    continue;
                }
                if (++files > maxFiles) break;
                names.Add(name);
                if (sources < maxSourceFiles && SourceExtensions.Contains(Path.GetExtension(name)))
                {
                    sources++;
                    try
                    {
                        var info = new FileInfo(path);
                        if (info.Length < 300_000)
                            foreach (Match m in Declaration().Matches(File.ReadAllText(path)))
                                names.Add(m.Groups["name"].Value);
                    }
                    catch (Exception) { /* unreadable: skip */ }
                }
            }
        }
        return new ProjectVocabulary(names, [projectName]);
    }

    private static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", "node_modules", "dist", "build", "out", "target", "packages", "artifacts", "vendor", "__pycache__", "venv", "env", "coverage",
    };

    private static readonly HashSet<string> SourceExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".ts", ".tsx", ".js", ".jsx", ".mjs", ".py", ".go", ".rs", ".java", ".kt", ".swift", ".cpp", ".h", ".hpp", ".php", ".rb", ".xaml",
    };

    [GeneratedRegex(@"\b(?:class|interface|record|struct|enum|function|def|func|fn|type|const|let|var)\s+(?<name>[A-Za-z_][A-Za-z0-9_]{3,})")]
    private static partial Regex Declaration();
}

/// <summary>Settings for tidying dictated text.</summary>
public sealed record PolishOptions(bool RemoveFillers = true, bool SpokenCommands = true, ProjectVocabulary? Vocabulary = null);

/// <summary>Tidies a transcription: spoken "new line" and "new paragraph", filler words, and project names.</summary>
public static partial class TextPolisher
{
    public static string Polish(string text, PolishOptions options)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        if (options.RemoveFillers) text = Fillers().Replace(text, "");
        if (options.SpokenCommands)
        {
            text = NewParagraph().Replace(text, "\n\n");
            text = NewLine().Replace(text, "\n");
        }
        if (options.Vocabulary is { } v) text = v.Apply(text);
        text = SpaceBeforePunctuation().Replace(text, "$1");
        text = MultiSpace().Replace(text, " ");
        text = string.Join('\n', text.Split('\n').Select(l => CapitalizeFirst(l.Trim()))); // a new line starts a sentence
        return CapitalizeFirst(text.Trim());
    }

    private static string CapitalizeFirst(string s) =>
        s.Length > 0 && char.IsLower(s[0]) ? char.ToUpperInvariant(s[0]) + s[1..] : s;

    [GeneratedRegex(@"\b(?:um+|uh+|erm+|uhm+|hmm+)\b[,.]?\s*", RegexOptions.IgnoreCase)]
    private static partial Regex Fillers();

    // A comma before the command goes; a full stop stays (it ends the sentence before the break).
    [GeneratedRegex(@",?\s*\bnew paragraph\b[,.!?]?\s*", RegexOptions.IgnoreCase)]
    private static partial Regex NewParagraph();

    [GeneratedRegex(@",?\s*\bnew line\b[,.!?]?\s*", RegexOptions.IgnoreCase)]
    private static partial Regex NewLine();

    [GeneratedRegex(@"[ \t]+([,.!?;:])")]
    private static partial Regex SpaceBeforePunctuation();

    [GeneratedRegex(@"[ \t]{2,}")]
    private static partial Regex MultiSpace();
}
