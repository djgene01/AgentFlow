using System.Text;

namespace AgentFlow.Speech;

/// <summary>The model's SentencePiece tokens ("token id" per line; "▁" marks the start of a word; the last one is the blank).</summary>
public sealed class TokenVocabulary
{
    private readonly string[] _tokens;

    public TokenVocabulary(IEnumerable<string> lines)
    {
        var map = new SortedDictionary<int, string>();
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r', '\n');
            var cut = line.LastIndexOf(' ');
            if (cut <= 0 || !int.TryParse(line.AsSpan(cut + 1), out var id)) continue;
            map[id] = line[..cut];
        }
        if (map.Count == 0) throw new InvalidDataException("The vocabulary file is empty.");
        _tokens = new string[map.Keys.Max() + 1];
        foreach (var (id, token) in map) _tokens[id] = token;
        BlankId = Array.IndexOf(_tokens, "<blk>") is var b and >= 0 ? b : _tokens.Length - 1;
    }

    public static TokenVocabulary Load(string path) => new(File.ReadAllLines(path, Encoding.UTF8));

    public int Count => _tokens.Length;
    public int BlankId { get; }

    public string Decode(IEnumerable<int> ids)
    {
        var sb = new StringBuilder();
        foreach (var id in ids)
        {
            if (id < 0 || id >= _tokens.Length || id == BlankId) continue;
            var t = _tokens[id];
            if (t is null || (t.StartsWith('<') && t.EndsWith('>'))) continue; // <unk> and other specials
            sb.Append(t.Replace('▁', ' '));
        }
        return CollapseSpaces(sb.ToString()).Trim();
    }

    private static string CollapseSpaces(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (c == ' ' && sb.Length > 0 && sb[^1] == ' ') continue;
            sb.Append(c);
        }
        return sb.ToString();
    }
}

/// <summary>One step of the TDT decoder/joint network: logits for (tokens + blank) followed by the duration logits.</summary>
public interface ITdtStep
{
    /// <summary>Runs the network for one encoder frame and the last emitted token. Returns the logits and the new decoder state.</summary>
    (float[] Logits, object State) Run(int frame, int lastToken, object state);

    object InitialState();
}

/// <summary>
/// Greedy decoding for a Token-and-Duration Transducer (as in NeMo and onnx-asr): at each frame take the best
/// token and the best duration; a non-blank token is emitted and advances the decoder state; the duration says
/// how many frames to skip. At most <see cref="MaxTokensPerFrame"/> tokens are emitted without moving on.
/// </summary>
public static class TdtGreedy
{
    public const int MaxTokensPerFrame = 10;

    public static List<int> Decode(ITdtStep step, int frames, int vocabSize, int blankId)
    {
        var tokens = new List<int>();
        var state = step.InitialState();
        var last = blankId;
        var t = 0;
        var emitted = 0;
        while (t < frames)
        {
            var (logits, next) = step.Run(t, last, state);
            var token = ArgMax(logits, 0, vocabSize);
            var duration = logits.Length > vocabSize ? ArgMax(logits, vocabSize, logits.Length - vocabSize) : 0;

            if (token != blankId)
            {
                state = next;
                last = token;
                tokens.Add(token);
                emitted++;
            }

            if (duration > 0)
            {
                t += duration;
                emitted = 0;
            }
            else if (token == blankId || emitted >= MaxTokensPerFrame)
            {
                t++;
                emitted = 0;
            }
        }
        return tokens;
    }

    internal static int ArgMax(float[] values, int start, int count)
    {
        var best = start;
        for (var i = start + 1; i < start + count; i++)
            if (values[i] > values[best]) best = i;
        return best - start;
    }
}
