using AgentFlow.Speech;

namespace AgentFlow.ClaudeCli.Tests.Speech;

/// <summary>The parts of voice input that need no model or microphone: decoding, tokens and text tidying.</summary>
public sealed class SpeechTextTests
{
    private static readonly TokenVocabulary Vocab = new(["<unk> 0", "▁hello 1", "▁wor 2", "ld 3", ". 4", "<blk> 5"]);

    [Fact]
    public void Tokens_join_into_words()
    {
        Assert.Equal(5, Vocab.BlankId);
        Assert.Equal("hello world.", Vocab.Decode([1, 2, 3, 4]));
        Assert.Equal("hello", Vocab.Decode([0, 1, 5])); // <unk> and blank are dropped
    }

    /// <summary>A scripted decoder: per frame, the token and duration it "predicts".</summary>
    private sealed class Script(params (int Token, int Duration)[] steps) : ITdtStep
    {
        private int _i;
        public List<int> LastTokens { get; } = [];
        public object InitialState() => 0;

        public (float[] Logits, object State) Run(int frame, int lastToken, object state)
        {
            LastTokens.Add(lastToken);
            var (token, duration) = steps[Math.Min(_i++, steps.Length - 1)];
            var logits = new float[6 + 5]; // 6 tokens incl. blank, durations 0..4
            logits[token] = 10;
            logits[6 + duration] = 10;
            return (logits, (int)state + 1);
        }
    }

    [Fact]
    public void Tdt_greedy_emits_tokens_and_skips_by_duration()
    {
        // frame 0: "hello", stay (0); frame 0: blank, move 1; frame 1: "wor", jump 2; frame 3: "ld", jump 4 -> past the end.
        var script = new Script((1, 0), (5, 1), (2, 2), (3, 4));
        var tokens = TdtGreedy.Decode(script, frames: 5, vocabSize: 6, blankId: 5);
        Assert.Equal([1, 2, 3], tokens);
        Assert.Equal([5, 1, 1, 2], script.LastTokens); // the decoder is fed the last emitted token, starting from blank
    }

    [Fact]
    public void Tdt_greedy_moves_on_after_too_many_tokens_on_one_frame()
    {
        var script = new Script((1, 0)); // always "hello", never moves by itself
        var tokens = TdtGreedy.Decode(script, frames: 2, vocabSize: 6, blankId: 5);
        Assert.Equal(2 * TdtGreedy.MaxTokensPerFrame, tokens.Count);
    }

    [Fact]
    public void Project_names_replace_their_spoken_words()
    {
        var v = new ProjectVocabulary(["VideoStudioViewModel", "README.md", "MainWindow", "build-installer.ps1"], ["AgentFlow"]);

        Assert.Equal("Open VideoStudioViewModel and README.md, please.",
            v.Apply("Open video studio view model and readme dot md, please."));
        Assert.Equal("AgentFlow is ready.", v.Apply("agent flow is ready."));
        Assert.Equal("Run build-installer.ps1 now", v.Apply("Run build installer dot ps 1 now"));
        Assert.Equal("Look at README.md", v.Apply("Look at readme.md")); // the model may already write the dot
        Assert.Equal("Close the main window.", v.Apply("Close the main window.")); // two everyday words stay
    }

    [Fact]
    public void Words_split_like_a_programmer_reads_them()
    {
        Assert.Equal(["video", "studio", "view", "model"], ProjectVocabulary.SplitWords("VideoStudioViewModel"));
        Assert.Equal(["parse", "json", "file"], ProjectVocabulary.SplitWords("parseJSONFile"));
        Assert.Equal(["build", "installer"], ProjectVocabulary.SplitWords("build_installer"));
        Assert.Equal(["v", "2"], ProjectVocabulary.SplitWords("v2"));
    }

    [Fact]
    public void Polish_handles_commands_fillers_and_spacing()
    {
        var options = new PolishOptions();
        Assert.Equal("First point.\nSecond point.", TextPolisher.Polish("Um, first point. New line. Second point.", options));
        Assert.Equal("Hello.\n\nNext part", TextPolisher.Polish("hello. New paragraph next part", options));
        Assert.Equal("So this works, right?", TextPolisher.Polish("so uh this works , right ?", options));
        Assert.Equal("Um, keep it", TextPolisher.Polish("um, keep it", options with { RemoveFillers = false }));
        Assert.Equal("", TextPolisher.Polish("   ", options));
    }

    [Fact]
    public void Project_folder_names_are_collected()
    {
        var dir = Directory.CreateTempSubdirectory("agentflow-vocab-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "src"));
            Directory.CreateDirectory(Path.Combine(dir, "node_modules", "LeftPadHelperThing"));
            File.WriteAllText(Path.Combine(dir, "src", "Widget.cs"), "public sealed class OrderTotalCalculator { }");
            var v = ProjectVocabulary.FromFolder(dir);
            Assert.Equal("Use OrderTotalCalculator", v.Apply("Use order total calculator"));
            Assert.Equal("left pad helper thing", v.Apply("left pad helper thing")); // dependency folders are skipped
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Model_variants_point_at_their_files()
    {
        var full = SpeechModels.Get(SpeechLanguage.English, SpeechPrecision.Full);
        var compact = SpeechModels.Get(SpeechLanguage.European, SpeechPrecision.Compact);
        Assert.Contains(full.Files, f => f.Name == "encoder-model.onnx.data");
        Assert.EndsWith("encoder-model.int8.onnx", compact.Encoder);
        Assert.Contains("v3", compact.Repository);
        Assert.NotEqual(full.Folder, SpeechModels.Get(SpeechLanguage.English, SpeechPrecision.Compact).Folder);
        Assert.StartsWith("https://huggingface.co/istupakov/parakeet-tdt-0.6b-v2-onnx/resolve/main/", full.Url(full.Files[0]));
    }
}
