using System.Net.Http;

namespace AgentFlow.Speech;

/// <summary>Which Parakeet model: English only (most accurate in English) or 25 European languages.</summary>
public enum SpeechLanguage { English, European }

/// <summary>
/// Full = the original 32-bit weights (best on a GPU). Compact = 8-bit weights (about a quarter of the size, fast on a CPU).
/// </summary>
public enum SpeechPrecision { Full, Compact }

/// <summary>One file of a model, as published on Hugging Face.</summary>
public sealed record SpeechModelFile(string Name, long ApproxBytes);

/// <summary>A downloadable model variant and where it lives on disk.</summary>
public sealed record SpeechModel(SpeechLanguage Language, SpeechPrecision Precision, string Repository, IReadOnlyList<SpeechModelFile> Files)
{
    public string Folder => Path.Combine(SpeechModels.Root, Repository.Replace('/', '_'), Precision == SpeechPrecision.Full ? "full" : "compact");

    public long ApproxBytes => Files.Sum(f => f.ApproxBytes);

    public bool IsDownloaded => Files.All(f => File.Exists(Path.Combine(Folder, f.Name)));

    public string Url(SpeechModelFile f) => $"https://huggingface.co/{Repository}/resolve/main/{f.Name}";

    public string Encoder => Path.Combine(Folder, Precision == SpeechPrecision.Full ? "encoder-model.onnx" : "encoder-model.int8.onnx");
    public string DecoderJoint => Path.Combine(Folder, Precision == SpeechPrecision.Full ? "decoder_joint-model.onnx" : "decoder_joint-model.int8.onnx");
    public string Preprocessor => Path.Combine(Folder, "nemo128.onnx");
    public string Vocabulary => Path.Combine(Folder, "vocab.txt");

    public string Label => (Language == SpeechLanguage.English ? "English" : "25 European languages") + ", " +
                           (Precision == SpeechPrecision.Full ? "full precision" : "compact");
}

/// <summary>
/// NVIDIA Parakeet TDT 0.6B (CC-BY-4.0), exported to ONNX by istupakov for onnx-asr: a preprocessor
/// (nemo128.onnx: audio to 128 log-mel features), the FastConformer encoder and the TDT decoder/joint network.
/// </summary>
public static class SpeechModels
{
    public static string Root =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentFlow", "Speech");

    private const long MB = 1024 * 1024;

    public static SpeechModel Get(SpeechLanguage language, SpeechPrecision precision)
    {
        var repo = language == SpeechLanguage.English ? "istupakov/parakeet-tdt-0.6b-v2-onnx" : "istupakov/parakeet-tdt-0.6b-v3-onnx";
        var decoderMb = language == SpeechLanguage.English ? (full: 34, compact: 9) : (full: 69, compact: 17);
        SpeechModelFile[] files = precision == SpeechPrecision.Full
            ?
            [
                new("nemo128.onnx", 1 * MB), new("vocab.txt", 1 * MB),
                new("encoder-model.onnx", 40 * MB), new("encoder-model.onnx.data", 2323 * MB),
                new("decoder_joint-model.onnx", decoderMb.full * MB),
            ]
            :
            [
                new("nemo128.onnx", 1 * MB), new("vocab.txt", 1 * MB),
                new("encoder-model.int8.onnx", 622 * MB),
                new("decoder_joint-model.int8.onnx", decoderMb.compact * MB),
            ];
        return new SpeechModel(language, precision, repo, files);
    }

    public static IEnumerable<SpeechModel> All() =>
        from l in Enum.GetValues<SpeechLanguage>()
        from p in Enum.GetValues<SpeechPrecision>()
        select Get(l, p);

    /// <summary>
    /// Downloads the missing files. Each file goes to a .part file first and is renamed when complete,
    /// so an interrupted download never leaves a broken model behind.
    /// </summary>
    /// <param name="progress">Bytes done and total (approximate until each file's size is known).</param>
    public static async Task DownloadAsync(SpeechModel model, IProgress<(long Done, long Total, string File)>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(model.Folder);
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("AgentFlow/1.0");
        var total = model.ApproxBytes;
        long done = 0;

        foreach (var file in model.Files)
        {
            var target = Path.Combine(model.Folder, file.Name);
            if (File.Exists(target))
            {
                done += new FileInfo(target).Length;
                progress?.Report((done, total, file.Name));
                continue;
            }

            var part = target + ".part";
            using var response = await http.GetAsync(model.Url(file), HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is { } len) total += len - file.ApproxBytes;

            await using (var source = await response.Content.ReadAsStreamAsync(ct))
            await using (var dest = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
            {
                var buffer = new byte[1 << 20];
                int read;
                var lastReport = Environment.TickCount64;
                while ((read = await source.ReadAsync(buffer, ct)) > 0)
                {
                    await dest.WriteAsync(buffer.AsMemory(0, read), ct);
                    done += read;
                    if (Environment.TickCount64 - lastReport > 200)
                    {
                        lastReport = Environment.TickCount64;
                        progress?.Report((done, total, file.Name));
                    }
                }
            }
            File.Move(part, target, overwrite: true);
            progress?.Report((done, total, file.Name));
        }
    }

    public static void Delete(SpeechModel model)
    {
        if (Directory.Exists(model.Folder)) Directory.Delete(model.Folder, recursive: true);
    }
}
