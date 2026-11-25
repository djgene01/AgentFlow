using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using MisakiSharp;

namespace AgentFlow.Speech;

/// <summary>A Kokoro voice. The first letter is the accent (a = American, b = British), the second the gender.</summary>
public sealed record KokoroVoice(string Id, string Name)
{
    public bool IsBritish => Id.StartsWith('b');
    public override string ToString() => Name;
}

/// <summary>
/// Kokoro-82M v1.0 (Apache-2.0) in the onnx-community ONNX export: the model, its phoneme vocabulary
/// (tokenizer.json) and one style file per voice (voices/&lt;id&gt;.bin, float32 [rows, 1, 256]).
/// </summary>
public static class KokoroModel
{
    public const int SampleRate = 24000;
    public const string Repository = "onnx-community/Kokoro-82M-v1.0-ONNX";

    public static string Folder => Path.Combine(SpeechModels.Root, "kokoro-v1.0");
    public static string ModelFile => Path.Combine(Folder, "model.onnx");
    public static string TokenizerFile => Path.Combine(Folder, "tokenizer.json");
    public static string VoiceFile(string id) => Path.Combine(Folder, "voices", id + ".bin");

    public const long ApproxBytes = 326L * 1024 * 1024;

    public static bool IsDownloaded => File.Exists(ModelFile) && File.Exists(TokenizerFile);

    /// <summary>The English voices (misaki's English G2P), best-rated first.</summary>
    public static IReadOnlyList<KokoroVoice> Voices { get; } =
    [
        new("af_heart", "Heart (American, female)"), new("af_bella", "Bella (American, female)"),
        new("af_nicole", "Nicole (American, female, soft)"), new("af_aoede", "Aoede (American, female)"),
        new("af_kore", "Kore (American, female)"), new("af_sarah", "Sarah (American, female)"),
        new("af_nova", "Nova (American, female)"), new("af_sky", "Sky (American, female)"),
        new("af_alloy", "Alloy (American, female)"), new("af_jessica", "Jessica (American, female)"),
        new("af_river", "River (American, female)"),
        new("am_michael", "Michael (American, male)"), new("am_fenrir", "Fenrir (American, male)"),
        new("am_puck", "Puck (American, male)"), new("am_echo", "Echo (American, male)"),
        new("am_eric", "Eric (American, male)"), new("am_liam", "Liam (American, male)"),
        new("am_onyx", "Onyx (American, male)"), new("am_adam", "Adam (American, male)"),
        new("bf_emma", "Emma (British, female)"), new("bf_isabella", "Isabella (British, female)"),
        new("bf_alice", "Alice (British, female)"), new("bf_lily", "Lily (British, female)"),
        new("bm_george", "George (British, male)"), new("bm_fable", "Fable (British, male)"),
        new("bm_lewis", "Lewis (British, male)"), new("bm_daniel", "Daniel (British, male)"),
    ];

    private static string Url(string path) => $"https://huggingface.co/{Repository}/resolve/main/{path}";

    /// <summary>Downloads the model and tokenizer (once) and the voice (each about 0.5 MB).</summary>
    public static async Task DownloadAsync(string voice, IProgress<(long Done, long Total, string File)>? progress, CancellationToken ct)
    {
        var files = new List<(string Remote, string Local)>
        {
            ("tokenizer.json", TokenizerFile),
            ("onnx/model.onnx", ModelFile),
            ($"voices/{voice}.bin", VoiceFile(voice)),
        };
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("AgentFlow/1.0");
        long done = 0, total = ApproxBytes;
        foreach (var (remote, local) in files)
        {
            if (File.Exists(local)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(local)!);
            var part = local + ".part";
            using var response = await http.GetAsync(Url(remote), HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            await using (var src = await response.Content.ReadAsStreamAsync(ct))
            await using (var dst = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
            {
                var buffer = new byte[1 << 20];
                int read;
                var last = Environment.TickCount64;
                while ((read = await src.ReadAsync(buffer, ct)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                    done += read;
                    if (Environment.TickCount64 - last > 200) { last = Environment.TickCount64; progress?.Report((done, total, remote)); }
                }
            }
            File.Move(part, local, overwrite: true);
        }
        progress?.Report((total, total, ""));
    }

    public static void Delete()
    {
        if (Directory.Exists(Folder)) Directory.Delete(Folder, recursive: true);
    }

    /// <summary>Reads the phoneme-to-id table from tokenizer.json (model.vocab). Case matters: "A" and "a" differ.</summary>
    public static Dictionary<string, long> LoadVocabulary(string tokenizerJson)
    {
        using var doc = JsonDocument.Parse(tokenizerJson);
        var vocab = doc.RootElement.GetProperty("model").GetProperty("vocab");
        var map = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var p in vocab.EnumerateObject()) map[p.Name] = p.Value.GetInt64();
        return map;
    }
}

/// <summary>Text to Kokoro token ids with misaki's English G2P (American or British), sentence by sentence.</summary>
public sealed class KokoroPhonemizer
{
    /// <summary>Kokoro reads at most 510 tokens at once (512 with the two padding zeros).</summary>
    public const int MaxTokens = 510;

    private readonly Dictionary<string, long> _vocab;
    private readonly Lock _lock = new();
    private EnglishG2P? _american, _british;

    public KokoroPhonemizer(Dictionary<string, long> vocab) => _vocab = vocab;

    public string Phonemes(string text, bool british)
    {
        lock (_lock)
        {
            var g2p = british
                ? _british ??= new EnglishG2P(british: true, espeakFallback: new EspeakFallback(true, EspeakReplay.Provider("en-gb")).Phonemize, unk: "")
                : _american ??= new EnglishG2P(british: false, espeakFallback: new EspeakFallback(false, EspeakReplay.Provider("en-us")).Phonemize, unk: "");
            return g2p.Phonemize(text).Item1;
        }
    }

    /// <summary>Phoneme ids, split at spaces into pieces Kokoro can read in one go.</summary>
    public List<long[]> Tokens(string text, bool british)
    {
        var ids = new List<long>();
        foreach (var ch in Phonemes(text, british).EnumerateRunes())
            if (_vocab.TryGetValue(ch.ToString(), out var id)) ids.Add(id);

        var pieces = new List<long[]>();
        var space = _vocab.TryGetValue(" ", out var s) ? s : -1;
        var start = 0;
        while (start < ids.Count)
        {
            var end = Math.Min(ids.Count, start + MaxTokens);
            if (end < ids.Count)
            {
                var cut = ids.LastIndexOf(space, end - 1, end - start);
                if (cut > start) end = cut + 1;
            }
            pieces.Add(ids.GetRange(start, end - start).ToArray());
            start = end;
        }
        return pieces;
    }
}

/// <summary>Kokoro-82M text to speech on ONNX Runtime: DirectML on any DirectX 12 GPU, or the CPU.</summary>
public sealed class KokoroTts : IDisposable
{
    private readonly InferenceSession _session;
    private readonly string _ids, _style, _speed;
    private readonly bool _speedIsInt;
    private readonly Dictionary<string, float[]> _voices = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public KokoroPhonemizer Phonemizer { get; }
    public string Provider { get; }
    public TimeSpan LoadTime { get; private set; }

    private KokoroTts(InferenceSession session, KokoroPhonemizer phonemizer, string provider)
    {
        _session = session;
        Phonemizer = phonemizer;
        Provider = provider;
        var inputs = session.InputMetadata;
        _ids = inputs.Keys.FirstOrDefault(k => k.Contains("input", StringComparison.OrdinalIgnoreCase) || k.Contains("token", StringComparison.OrdinalIgnoreCase))
               ?? inputs.Keys.First();
        _style = inputs.Keys.FirstOrDefault(k => k.Contains("style", StringComparison.OrdinalIgnoreCase)) ?? inputs.Keys.ElementAt(1);
        _speed = inputs.Keys.FirstOrDefault(k => k.Contains("speed", StringComparison.OrdinalIgnoreCase)) ?? inputs.Keys.ElementAt(2);
        _speedIsInt = inputs[_speed].ElementType == typeof(int);
    }

    /// <summary>
    /// Loads Kokoro and speaks a test sentence. Some GPUs/drivers accept the model but fail when it runs
    /// (DirectML does not support every operation Kokoro uses on every GPU); then it loads again on the CPU,
    /// which is fast enough for Kokoro's 82M parameters, and <see cref="GpuProblem"/> says why.
    /// </summary>
    public static KokoroTts Load(SpeechDevice device, int gpuIndex = 0)
    {
        if (!KokoroModel.IsDownloaded) throw new FileNotFoundException("Kokoro is not downloaded yet.");
        var sw = Stopwatch.StartNew();
        var phonemizer = new KokoroPhonemizer(KokoroModel.LoadVocabulary(File.ReadAllText(KokoroModel.TokenizerFile)));
        string? gpuProblem = null;
        if (device != SpeechDevice.Cpu)
        {
            KokoroTts? gpu = null;
            try
            {
                var o = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL, EnableMemoryPattern = false, ExecutionMode = ExecutionMode.ORT_SEQUENTIAL };
                o.AppendExecutionProvider_DML(gpuIndex);
                gpu = new KokoroTts(new InferenceSession(KokoroModel.ModelFile, o), phonemizer, $"GPU {gpuIndex} (DirectML)");
                gpu.WarmUp();
                gpu.LoadTime = sw.Elapsed;
                return gpu;
            }
            catch (Exception ex) when (ex is not FileNotFoundException)
            {
                gpu?.Dispose();
                gpuProblem = ex.Message;
            }
        }
        var cpu = new KokoroTts(new InferenceSession(KokoroModel.ModelFile, new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount - 1),
        }), phonemizer, gpuProblem is null ? $"CPU ({Environment.ProcessorCount} threads)" : $"CPU ({Environment.ProcessorCount} threads; the GPU could not run Kokoro)");
        try
        {
            cpu.WarmUp();
        }
        catch
        {
            cpu.Dispose();
            throw;
        }
        cpu.GpuProblem = gpuProblem;
        cpu.LoadTime = sw.Elapsed;
        return cpu;
    }

    /// <summary>Why the GPU was not used, when it was tried and failed.</summary>
    public string? GpuProblem { get; private set; }

    /// <summary>One short run with a neutral style, so a GPU that cannot run the model fails here and not mid-reply.</summary>
    private void WarmUp()
    {
        var ids = Phonemizer.Tokens("Ready.", british: false).FirstOrDefault() ?? [];
        if (ids.Length == 0) ids = [16];
        Run(ids, new float[256], 1f);
    }

    private float[] Run(long[] ids, float[] style, float speed)
    {
        var padded = new long[ids.Length + 2];
        Array.Copy(ids, 0, padded, 1, ids.Length);
        using var results = _session.Run(
        [
            NamedOnnxValue.CreateFromTensor(_ids, new DenseTensor<long>(padded, [1, padded.Length])),
            NamedOnnxValue.CreateFromTensor(_style, new DenseTensor<float>(style, [1, 256])),
            _speedIsInt
                ? NamedOnnxValue.CreateFromTensor(_speed, new DenseTensor<int>(new[] { (int)MathF.Round(speed) }, [1]))
                : NamedOnnxValue.CreateFromTensor(_speed, new DenseTensor<float>(new[] { speed }, [1])),
        ]);
        return results.First().AsTensor<float>().ToArray();
    }

    private float[] Voice(string id)
    {
        lock (_voices)
        {
            if (_voices.TryGetValue(id, out var v)) return v;
            var bytes = File.ReadAllBytes(KokoroModel.VoiceFile(id));
            v = new float[bytes.Length / 4];
            Buffer.BlockCopy(bytes, 0, v, 0, v.Length * 4);
            _voices[id] = v;
            return v;
        }
    }

    /// <summary>Speaks one piece of text (best a sentence or two). Returns 24 kHz mono samples.</summary>
    public float[] Synthesize(string text, KokoroVoice voice, float speed = 1f, CancellationToken ct = default)
    {
        var pieces = Phonemizer.Tokens(text, voice.IsBritish);
        var style = Voice(voice.Id);
        var rows = style.Length / 256;
        var output = new List<float>();
        foreach (var ids in pieces)
        {
            ct.ThrowIfCancellationRequested();
            if (ids.Length == 0) continue;
            var row = Math.Min(ids.Length, rows - 1); // the style is chosen by the number of tokens
            var s = new float[256];
            Array.Copy(style, row * 256, s, 0, 256);

            _gate.Wait(ct);
            try
            {
                output.AddRange(Run(ids, s, speed));
            }
            finally
            {
                _gate.Release();
            }
        }
        return output.ToArray();
    }

    public void Dispose()
    {
        _session.Dispose();
        _gate.Dispose();
    }
}
