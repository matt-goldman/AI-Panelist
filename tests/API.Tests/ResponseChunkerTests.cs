using API.Configuration;
using API.Services;
using Microsoft.Extensions.Options;
using Xunit;

namespace API.Tests;

/// <summary>
/// Chunk boundaries decide how the first sentence sounds. They are chosen by punctuation,
/// never by token count: a fixed split lands mid-phrase and the TTS renders bad prosody
/// at the seam.
/// </summary>
public class ResponseChunkerTests
{
    private static ResponseChunker Chunker(StreamingResponseOptions? options = null) =>
        new(Options.Create(options ?? new StreamingResponseOptions()));

    private static async Task<List<string>> Chunks(ResponseChunker chunker, params string[] tokens)
    {
        var result = new List<string>();
        await foreach (var chunk in chunker.ChunkAsync(Tokens(tokens))) result.Add(chunk);
        return result;

        static async IAsyncEnumerable<string> Tokens(string[] tokens)
        {
            foreach (var token in tokens)
            {
                yield return token;
                await Task.Yield();
            }
        }
    }

    [Fact]
    public async Task A_flush_speaks_what_is_buffered_without_waiting_for_a_boundary()
    {
        // A holding line is followed by a long silence while the model thinks. Without an
        // explicit flush it would sit in the buffer for the whole of it.
        var chunks = await Chunks(Chunker(),
            "Hmm.", ResponseChunker.Flush, " The answer is forty two, obviously.");

        Assert.Equal("Hmm.", chunks[0]);
        Assert.DoesNotContain(chunks, chunk => chunk.Contains('\0'));
    }

    [Fact]
    public async Task Does_not_split_on_a_decimal_point_or_an_abbreviation()
    {
        var chunks = await Chunks(Chunker(),
            "Version 3.5 is out, e.g. the one Dr. Smith mentioned, and it is quite good really. ",
            "Next sentence here. ");

        // Each must survive whole inside one chunk, rather than being split at the dot.
        Assert.Contains(chunks, chunk => chunk.Contains("3.5"));
        Assert.Contains(chunks, chunk => chunk.Contains("e.g. the"));
        Assert.Contains(chunks, chunk => chunk.Contains("Dr. Smith"));
    }

    [Fact]
    public async Task Nothing_is_dropped_however_it_is_split()
    {
        const string text = "Right, so that is a good question. I reckon the answer is yes, mostly. "
                            + "There are trade-offs though, and they matter more than people think. Cheers.";

        var chunks = await Chunks(Chunker(), [.. text.Chunk(7).Select(c => new string(c))]);

        Assert.Equal(
            text.Replace(" ", string.Empty).Trim(),
            string.Concat(chunks).Replace(" ", string.Empty));
    }

    [Fact]
    public async Task The_opening_chunk_is_kept_short_because_it_sets_time_to_first_audio()
    {
        var options = new StreamingResponseOptions { FirstChunkMinChars = 20, FirstChunkMaxChars = 60, MinChunkChars = 80 };

        var chunks = await Chunks(Chunker(options),
            "Well, that is a fair question, and I think the answer depends on a great many things. ",
            "Here is some more text to make a second chunk happen properly. ");

        Assert.True(chunks[0].Length <= 60, $"first chunk was {chunks[0].Length} characters");
    }

    [Theory]
    [InlineData("*waves* hello", "waves hello")]
    [InlineData("# Heading\nbody", "Heading body")]
    [InlineData("`code` and _emphasis_", "code and emphasis")]
    public void Markdown_is_stripped_before_it_reaches_the_TTS(string input, string expected) =>
        Assert.Equal(expected, SpeechText.Clean(input));
}
