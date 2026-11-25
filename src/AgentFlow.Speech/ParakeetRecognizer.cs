using System.Diagnostics;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace AgentFlow.Speech;

public enum SpeechDevice { Auto, Gpu, Cpu }

/// <summary>What a load ended up using, for the Voice page.</summary>
public sealed record RecognizerInfo(string Provider, string Model, TimeSpan LoadTime);

/// <summary>
/// Speech to text with NVIDIA Parakeet TDT on ONNX Runtime. The encoder (nearly all the work) runs on the GPU
/// through DirectML when one is available, on any DirectX 12 GPU (AMD, Intel or NVIDIA); the small preprocessor
/// and the per-frame decoder stay on the CPU, where their many tiny calls are fastest. Thread-safe: calls are serialized.
/// </summary>
public sealed class ParakeetRecognizer : IDisposable
{
    public const int SampleRate = 16000;

    private readonly InferenceSession _preprocessor, _encoder, _decoder;
    private readonly TokenVocabulary _vocab;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public RecognizerInfo Info { get; private set; }

    private ParakeetRecognizer(InferenceSession pre, InferenceSession enc, InferenceSession dec, TokenVocabulary vocab, RecognizerInfo info)
    {
        _preprocessor = pre;
        _encoder = enc;
        _decoder = dec;
        _vocab = vocab;
        Info = info;
    }

    /// <summary>
    /// Loads the model and runs it once. When the GPU cannot start or cannot run the model (not every
    /// DirectML driver supports every operation), it loads again on the CPU and <see cref="GpuProblem"/> says why.
    /// </summary>
    public static ParakeetRecognizer Load(SpeechModel model, SpeechDevice device, int gpuIndex = 0)
    {
        if (!model.IsDownloaded) throw new FileNotFoundException($"The speech model is not downloaded yet ({model.Label}).");
        var sw = Stopwatch.StartNew();
        string? gpuProblem = null;
        if (device != SpeechDevice.Cpu)
        {
            try
            {
                return Create(model, GpuOptions(gpuIndex), $"GPU {gpuIndex} (DirectML)", sw, null);
            }
            catch (Exception ex) when (ex is not FileNotFoundException)
            {
                gpuProblem = ex.Message;
            }
        }
        return Create(model, CpuOptions(),
            gpuProblem is null ? $"CPU ({Environment.ProcessorCount} threads)" : $"CPU ({Environment.ProcessorCount} threads; the GPU could not run the model)",
            sw, gpuProblem);
    }

    private static ParakeetRecognizer Create(SpeechModel model, SessionOptions encoderOptions, string provider, Stopwatch sw, string? gpuProblem)
    {
        var vocab = TokenVocabulary.Load(model.Vocabulary);
        InferenceSession? pre = null, dec = null, enc = null;
        try
        {
            pre = new InferenceSession(model.Preprocessor, CpuOptions());
            dec = new InferenceSession(model.DecoderJoint, CpuOptions());
            enc = new InferenceSession(model.Encoder, encoderOptions);
        }
        catch
        {
            pre?.Dispose();
            dec?.Dispose();
            enc?.Dispose();
            throw;
        }
        var r = new ParakeetRecognizer(pre, enc, dec, vocab, new RecognizerInfo(provider, model.Label, TimeSpan.Zero));
        try
        {
            r.Transcribe(new float[SampleRate / 2]); // warm-up: compiles kernels, and shows a GPU that cannot run it
        }
        catch
        {
            r.Dispose();
            throw;
        }
        r.Info = r.Info with { LoadTime = sw.Elapsed };
        r.GpuProblem = gpuProblem;
        return r;
    }

    /// <summary>Why the GPU was not used, when it was tried and failed.</summary>
    public string? GpuProblem { get; private set; }

    private static NamedOnnxValue Length(InferenceSession session, string name, long value) =>
        session.InputMetadata[name].ElementType == typeof(int)
            ? NamedOnnxValue.CreateFromTensor(name, new DenseTensor<int>(new[] { (int)value }, [1]))
            : NamedOnnxValue.CreateFromTensor(name, new DenseTensor<long>(new[] { value }, [1]));

    private static SessionOptions CpuOptions() => new()
    {
        GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount - 1),
    };

    private static SessionOptions GpuOptions(int gpuIndex)
    {
        var o = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            EnableMemoryPattern = false, // required by DirectML
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
        };
        o.AppendExecutionProvider_DML(gpuIndex);
        return o;
    }

    /// <summary>Transcribes 16 kHz mono samples in [-1, 1]. Returns the text with punctuation and capitals.</summary>
    public string Transcribe(ReadOnlySpan<float> samples)
    {
        if (samples.Length < SampleRate / 10) return "";
        var copy = samples.ToArray();
        _gate.Wait();
        try
        {
            return TranscribeCore(copy);
        }
        finally
        {
            _gate.Release();
        }
    }

    private string TranscribeCore(float[] samples)
    {
        // 1. Audio to 128 log-mel features.
        var preIn = _preprocessor.InputMetadata.Keys.ToList();
        using var feats = _preprocessor.Run(
        [
            NamedOnnxValue.CreateFromTensor(preIn[0], new DenseTensor<float>(samples, [1, samples.Length])),
            Length(_preprocessor, preIn[1], samples.Length),
        ]);
        var features = feats.First().AsTensor<float>();
        var featureLength = ReadLong(feats.ElementAt(1));

        // 2. Encoder.
        var encIn = _encoder.InputMetadata.Keys.ToList();
        using var enc = _encoder.Run(
        [
            NamedOnnxValue.CreateFromTensor(encIn[0], ToDense(features)),
            Length(_encoder, encIn[1], featureLength),
        ]);
        var encoded = enc.First().AsTensor<float>();
        var frames = (int)ReadLong(enc.ElementAt(1));

        // [1, D, T] (NeMo's layout) or [1, T, D].
        var dims = encoded.Dimensions.ToArray();
        var channelsFirst = dims[2] >= frames && dims[1] != frames;
        var hidden = channelsFirst ? dims[1] : dims[2];
        var encodedArray = encoded.ToArray();

        // 3. Greedy TDT decoding.
        var step = new DecoderStep(_decoder, encodedArray, hidden, channelsFirst ? dims[2] : dims[1], channelsFirst);
        var tokens = TdtGreedy.Decode(step, frames, _vocab.Count, _vocab.BlankId);
        return _vocab.Decode(tokens);
    }

    private static DenseTensor<float> ToDense(Tensor<float> t) =>
        t as DenseTensor<float> ?? new DenseTensor<float>(t.ToArray(), t.Dimensions.ToArray());

    private static long ReadLong(DisposableNamedOnnxValue v) => v.Value switch
    {
        Tensor<long> l => l.GetValue(0),
        Tensor<int> i => i.GetValue(0),
        _ => throw new InvalidDataException("Unexpected length output from the speech model."),
    };

    /// <summary>Runs the decoder/joint network for one frame; its recurrent state is kept as two float tensors.</summary>
    private sealed class DecoderStep : ITdtStep
    {
        private readonly InferenceSession _session;
        private readonly float[] _encoded;
        private readonly int _hidden, _time;
        private readonly bool _channelsFirst;
        private readonly string _encName, _targetName, _lengthName, _state1, _state2;
        private readonly bool _targetsAreInt64, _lengthIsInt64;
        private readonly int[] _stateShape1, _stateShape2;
        private readonly string _outLogits, _outState1, _outState2;

        public DecoderStep(InferenceSession session, float[] encoded, int hidden, int time, bool channelsFirst)
        {
            _session = session;
            _encoded = encoded;
            _hidden = hidden;
            _time = time;
            _channelsFirst = channelsFirst;

            var inputs = session.InputMetadata;
            string Find(Func<string, NodeMetadata, bool> match, string what) =>
                inputs.FirstOrDefault(kv => match(kv.Key, kv.Value)).Key ?? throw new InvalidDataException($"The decoder has no {what} input.");

            _encName = Find((n, m) => n.Contains("encoder", StringComparison.OrdinalIgnoreCase), "encoder");
            _targetName = Find((n, m) => n.Equals("targets", StringComparison.OrdinalIgnoreCase) || (n.Contains("target") && !n.Contains("length")), "targets");
            _lengthName = Find((n, m) => n.Contains("length", StringComparison.OrdinalIgnoreCase), "target length");
            var states = inputs.Where(kv => kv.Key.Contains("state", StringComparison.OrdinalIgnoreCase)).OrderBy(kv => kv.Key, StringComparer.Ordinal).ToList();
            if (states.Count != 2) throw new InvalidDataException("The decoder should have two state inputs.");
            _state1 = states[0].Key;
            _state2 = states[1].Key;
            _stateShape1 = states[0].Value.Dimensions.Select(d => d <= 0 ? 1 : d).ToArray();
            _stateShape2 = states[1].Value.Dimensions.Select(d => d <= 0 ? 1 : d).ToArray();
            _targetsAreInt64 = inputs[_targetName].ElementType == typeof(long);
            _lengthIsInt64 = inputs[_lengthName].ElementType == typeof(long);

            var outputs = session.OutputMetadata.Keys.ToList();
            var outStates = outputs.Where(n => n.Contains("state", StringComparison.OrdinalIgnoreCase)).OrderBy(n => n, StringComparer.Ordinal).ToList();
            _outLogits = outputs.First(n => !n.Contains("state", StringComparison.OrdinalIgnoreCase) && !n.Contains("length", StringComparison.OrdinalIgnoreCase));
            _outState1 = outStates[0];
            _outState2 = outStates[1];
        }

        public object InitialState() => (new float[_stateShape1.Aggregate(1, (a, b) => a * b)], new float[_stateShape2.Aggregate(1, (a, b) => a * b)]);

        public (float[] Logits, object State) Run(int frame, int lastToken, object state)
        {
            var (s1, s2) = ((float[], float[]))state;
            var vector = new float[_hidden];
            if (_channelsFirst)
                for (var d = 0; d < _hidden; d++) vector[d] = _encoded[d * _time + frame];
            else
                Array.Copy(_encoded, frame * _hidden, vector, 0, _hidden);

            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor(_encName, new DenseTensor<float>(vector, [1, _hidden, 1])),
                _targetsAreInt64
                    ? NamedOnnxValue.CreateFromTensor(_targetName, new DenseTensor<long>(new long[] { lastToken }, [1, 1]))
                    : NamedOnnxValue.CreateFromTensor(_targetName, new DenseTensor<int>(new[] { lastToken }, [1, 1])),
                _lengthIsInt64
                    ? NamedOnnxValue.CreateFromTensor(_lengthName, new DenseTensor<long>(new long[] { 1 }, [1]))
                    : NamedOnnxValue.CreateFromTensor(_lengthName, new DenseTensor<int>(new[] { 1 }, [1])),
                NamedOnnxValue.CreateFromTensor(_state1, new DenseTensor<float>(s1, _stateShape1)),
                NamedOnnxValue.CreateFromTensor(_state2, new DenseTensor<float>(s2, _stateShape2)),
            };
            using var results = _session.Run(inputs, [_outLogits, _outState1, _outState2]);
            var logits = results.First(r => r.Name == _outLogits).AsTensor<float>().ToArray();
            var n1 = results.First(r => r.Name == _outState1).AsTensor<float>().ToArray();
            var n2 = results.First(r => r.Name == _outState2).AsTensor<float>().ToArray();
            return (logits, (n1, n2));
        }
    }

    public void Dispose()
    {
        _preprocessor.Dispose();
        _encoder.Dispose();
        _decoder.Dispose();
        _gate.Dispose();
    }
}
