using Offstream.Core.Audio;
using Xunit;

namespace Offstream.Core.Tests.Audio;

/// <summary>
/// New coverage. The reference implementation's circular buffer had no tests at all, despite
/// wrap-around arithmetic in four separate methods and a data race in <c>Peek</c>.
/// </summary>
public sealed class AudioRingBufferTests
{
    private static byte[] Bytes(params int[] values) => [.. values.Select(v => (byte)v)];

    [Fact]
    public void NewBuffer_IsEmpty()
    {
        var buffer = new AudioRingBuffer(8);

        Assert.Equal(8, buffer.Capacity);
        Assert.Equal(0, buffer.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Capacity_MustBePositive(int capacity) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new AudioRingBuffer(capacity));

    [Fact]
    public void WriteThenRead_RoundTripsBytes()
    {
        var buffer = new AudioRingBuffer(8);
        Assert.Equal(4, buffer.Write(Bytes(1, 2, 3, 4)));
        Assert.Equal(4, buffer.Count);

        var destination = new byte[4];
        Assert.Equal(4, buffer.Read(destination));

        Assert.Equal(Bytes(1, 2, 3, 4), destination);
        Assert.Equal(0, buffer.Count);
    }

    /// <summary>
    /// A full buffer truncates rather than blocking. Dropping a sample beats stalling the
    /// capture callback, which would corrupt the stream for everyone.
    /// </summary>
    [Fact]
    public void Write_WhenFull_TruncatesRatherThanBlocking()
    {
        var buffer = new AudioRingBuffer(4);

        Assert.Equal(4, buffer.Write(Bytes(1, 2, 3, 4)));
        Assert.Equal(0, buffer.Write(Bytes(5, 6)));
        Assert.Equal(4, buffer.Count);
    }

    [Fact]
    public void Write_PartiallyFits_TakesWhatItCan()
    {
        var buffer = new AudioRingBuffer(4);
        buffer.Write(Bytes(1, 2, 3));

        Assert.Equal(1, buffer.Write(Bytes(4, 5, 6)));
        Assert.Equal(4, buffer.Count);
    }

    [Fact]
    public void Write_WrapsAroundTheEnd()
    {
        var buffer = new AudioRingBuffer(4);

        buffer.Write(Bytes(1, 2, 3));
        buffer.Read(new byte[3]);          // read position now 3
        buffer.Write(Bytes(4, 5, 6));      // writes 4 at index 3, then 5,6 at 0,1

        var destination = new byte[3];
        Assert.Equal(3, buffer.Read(destination));
        Assert.Equal(Bytes(4, 5, 6), destination);
    }

    [Fact]
    public void Read_WrapsAroundTheEnd()
    {
        var buffer = new AudioRingBuffer(4);

        buffer.Write(Bytes(1, 2, 3, 4));
        buffer.Advance(3);
        buffer.Write(Bytes(5, 6));

        var destination = new byte[3];
        Assert.Equal(3, buffer.Read(destination));
        Assert.Equal(Bytes(4, 5, 6), destination);
    }

    [Fact]
    public void Read_MoreThanBuffered_ReturnsOnlyWhatExists()
    {
        var buffer = new AudioRingBuffer(8);
        buffer.Write(Bytes(1, 2));

        var destination = new byte[8];
        Assert.Equal(2, buffer.Read(destination));
    }

    [Fact]
    public void Read_FromEmpty_ReturnsZero() =>
        Assert.Equal(0, new AudioRingBuffer(8).Read(new byte[4]));

    [Fact]
    public void Peek_DoesNotConsume()
    {
        var buffer = new AudioRingBuffer(8);
        buffer.Write(Bytes(1, 2, 3, 4));

        var peeked = new byte[4];
        Assert.Equal(4, buffer.Peek(peeked));
        Assert.Equal(Bytes(1, 2, 3, 4), peeked);
        Assert.Equal(4, buffer.Count);

        var read = new byte[4];
        buffer.Read(read);
        Assert.Equal(peeked, read);
    }

    [Fact]
    public void Peek_WrapsAroundTheEnd()
    {
        var buffer = new AudioRingBuffer(4);
        buffer.Write(Bytes(1, 2, 3, 4));
        buffer.Advance(3);
        buffer.Write(Bytes(5, 6));

        var peeked = new byte[3];
        Assert.Equal(3, buffer.Peek(peeked));
        Assert.Equal(Bytes(4, 5, 6), peeked);
        Assert.Equal(3, buffer.Count);
    }

    [Fact]
    public void Advance_DiscardsBytes()
    {
        var buffer = new AudioRingBuffer(8);
        buffer.Write(Bytes(1, 2, 3, 4));

        buffer.Advance(2);

        var destination = new byte[2];
        buffer.Read(destination);
        Assert.Equal(Bytes(3, 4), destination);
    }

    [Fact]
    public void Advance_BeyondCount_ResetsTheBuffer()
    {
        var buffer = new AudioRingBuffer(8);
        buffer.Write(Bytes(1, 2, 3, 4));

        buffer.Advance(99);

        Assert.Equal(0, buffer.Count);
        Assert.Equal(0, buffer.ReadPosition);
        Assert.Equal(0, buffer.WritePosition);
    }

    [Fact]
    public void Advance_RejectsNegativeCounts() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new AudioRingBuffer(8).Advance(-1));

    [Fact]
    public void Reset_EmptiesTheBuffer()
    {
        var buffer = new AudioRingBuffer(8);
        buffer.Write(Bytes(1, 2, 3, 4));

        buffer.Reset();

        Assert.Equal(0, buffer.Count);
        Assert.Equal(0, buffer.ReadPosition);
        Assert.Equal(0, buffer.WritePosition);
    }

    /// <summary>
    /// Many wrap cycles must not drift. This is the arithmetic the reference never covered.
    /// </summary>
    [Fact]
    public void RepeatedWrapping_StaysConsistent()
    {
        var buffer = new AudioRingBuffer(5);
        var next = 0;

        for (var cycle = 0; cycle < 200; cycle++)
        {
            var written = Bytes(next % 251, (next + 1) % 251, (next + 2) % 251);
            Assert.Equal(3, buffer.Write(written));

            var read = new byte[3];
            Assert.Equal(3, buffer.Read(read));
            Assert.Equal(written, read);

            Assert.Equal(0, buffer.Count);
            next += 3;
        }
    }

    /// <summary>
    /// Concurrent writers and readers must not corrupt the counters. The reference's Peek
    /// read its state outside the lock, which is exactly the shape of bug this catches.
    /// </summary>
    [Fact]
    public async Task ConcurrentWriteAndPeek_KeepsCountWithinBounds()
    {
        var buffer = new AudioRingBuffer(1024);
        using var stopping = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        var writer = Task.Run(() =>
        {
            var chunk = new byte[64];
            while (!stopping.IsCancellationRequested)
            {
                buffer.Write(chunk);
                buffer.Advance(32);
            }
        });

        var peeker = Task.Run(() =>
        {
            var destination = new byte[128];
            while (!stopping.IsCancellationRequested)
            {
                var count = buffer.Peek(destination);
                Assert.InRange(count, 0, destination.Length);
                Assert.InRange(buffer.Count, 0, buffer.Capacity);
            }
        });

        await Task.WhenAll(writer, peeker);
    }
}
