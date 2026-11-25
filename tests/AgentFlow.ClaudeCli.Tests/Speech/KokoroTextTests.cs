using AgentFlow.Speech;

namespace AgentFlow.ClaudeCli.Tests.Speech;

/// <summary>Kokoro's text side: what is read aloud, how it is split, and the phonemes (no model needed).</summary>
public sealed class KokoroTextTests
{
    [Fact]
    public void Markdown_is_read_without_code_tables_or_urls()
    {
        const string md = """
            ## Done

            I changed **two files**:
            - `VoiceViewModel.cs` for the settings
            - see [the docs](https://example.com/docs) or https://example.com

            ```csharp
            var secret = 42;
            ```

            | File | Lines |
            | --- | --- |
            | a.cs | 3 |

            Run it again.
            """;
        var spoken = SpokenText.FromMarkdown(md, final: true);

        Assert.Contains("Done.", spoken);
        Assert.Contains("I changed two files:", spoken);
        Assert.Contains("VoiceViewModel.cs for the settings.", spoken);
        Assert.Contains("see the docs or a link.", spoken);
        Assert.Contains("Run it again.", spoken);
        Assert.DoesNotContain("secret", spoken);
        Assert.DoesNotContain("|", spoken);
        Assert.DoesNotContain("**", spoken);
        Assert.DoesNotContain("example.com", spoken);
    }

    [Fact]
    public void Streaming_text_is_spoken_sentence_by_sentence_and_never_twice()
    {
        var reply = "First sentence here. Second one is a bit longer than the first";
        var spoken = SpokenText.FromMarkdown(reply, final: false);
        var (pieces, next) = SpokenText.TakeSentences(spoken, 0, final: false);
        Assert.Equal(["First sentence here."], pieces); // the unfinished sentence waits

        reply += ". Third!\n\n```\ncode";
        spoken = SpokenText.FromMarkdown(reply, final: false);
        (pieces, next) = SpokenText.TakeSentences(spoken, next, final: false);
        Assert.Equal(["Second one is a bit longer than the first. Third!"], pieces);
        Assert.DoesNotContain("code", spoken); // an unclosed fence is code so far

        reply += "\n```\nThat is all";
        spoken = SpokenText.FromMarkdown(reply, final: true);
        (pieces, _) = SpokenText.TakeSentences(spoken, next, final: true);
        Assert.Equal(["That is all."], pieces);
    }

    [Fact]
    public void Misaki_turns_english_into_kokoro_phonemes()
    {
        // A slice of Kokoro's vocabulary is enough to check the mapping.
        var vocab = new Dictionary<string, long>(StringComparer.Ordinal) { [" "] = 16 };
        var phonemizer = new KokoroPhonemizer(vocab);
        var us = phonemizer.Phonemes("Hello world, the tomato is ready.", british: false);
        var gb = phonemizer.Phonemes("Hello world, the tomato is ready.", british: true);

        Assert.Contains("ˈ", us); // stress marks
        Assert.Contains("w", us);
        Assert.NotEqual(us, gb); // tomato: American and British differ
        foreach (var c in us.Where(c => c != ' ')) vocab.TryAdd(c.ToString(), vocab.Count + 1);
        var tokens = Assert.Single(phonemizer.Tokens("Hello world, the tomato is ready.", british: false));
        Assert.Equal(us.Length, tokens.Length);
    }

    [Fact]
    public void Long_text_is_split_at_spaces_under_the_token_limit()
    {
        var vocab = new Dictionary<string, long>(StringComparer.Ordinal) { [" "] = 16 };
        var phonemizer = new KokoroPhonemizer(vocab);
        var text = string.Join(" ", Enumerable.Repeat("information", 120));
        foreach (var c in phonemizer.Phonemes(text, false)) vocab.TryAdd(c.ToString(), vocab.Count + 1);

        var pieces = phonemizer.Tokens(text, false);
        Assert.True(pieces.Count > 1);
        Assert.All(pieces, p => Assert.InRange(p.Length, 1, KokoroPhonemizer.MaxTokens));
        Assert.All(pieces.SkipLast(1), p => Assert.Equal(16, p[^1])); // cut after a space, not mid-word
    }

    [Fact]
    public void Speech_is_played_as_clipped_16_bit_pcm()
    {
        var pcm = SpeechPlayer.ToPcm16([0f, 1f, -1f, 2f, 0.5f]);
        Assert.Equal(10, pcm.Length);
        short At(int i) => BitConverter.ToInt16(pcm, 2 * i);
        Assert.Equal(0, At(0));
        Assert.Equal(32767, At(1));
        Assert.Equal(-32767, At(2));
        Assert.Equal(32767, At(3)); // clipped
        Assert.Equal(16384, At(4));
    }

    [Fact]
    public void The_tokenizer_vocabulary_keeps_case()
    {
        var vocab = KokoroModel.LoadVocabulary("""{"model":{"vocab":{"A":24,"a":43," ":16}}}""");
        Assert.Equal(24, vocab["A"]);
        Assert.Equal(43, vocab["a"]);
        Assert.Contains(KokoroModel.Voices, v => v.Id == "af_heart");
        Assert.True(KokoroModel.Voices.Single(v => v.Id == "bf_emma").IsBritish);
    }
}
