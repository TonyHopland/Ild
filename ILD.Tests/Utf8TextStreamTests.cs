using System.Text;
using ILD.Core.Services.Implementations.Adapters;

namespace ILD.Tests;

/// <summary>A string read as UTF-8 a block at a time gives exactly the bytes of encoding it whole.</summary>
public sealed class Utf8TextStreamTests
{
    private static readonly string Text = string.Concat(Enumerable.Repeat("{\"text\":\"café ✓ 🙂\"}\n", 20_000)) + "\ud800 end";

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(81920)]
    public async Task Reading_in_blocks_of_any_size_gives_the_bytes_of_the_whole_string(int blockSize)
    {
        using var stream = new Utf8TextStream(Text);
        using var read = new MemoryStream();
        var block = new byte[blockSize];
        int count;
        while ((count = await stream.ReadAsync(block, TestContext.Current.CancellationToken)) > 0)
            read.Write(block, 0, count);

        Assert.Equal(Encoding.UTF8.GetBytes(Text), read.ToArray());
    }

    [Fact]
    public void An_empty_string_reads_as_nothing()
    {
        using var stream = new Utf8TextStream(string.Empty);

        Assert.Equal(0, stream.Read(new byte[16], 0, 16));
    }
}
