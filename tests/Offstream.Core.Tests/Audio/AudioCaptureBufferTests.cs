using NAudio.Wave;
using Offstream.Core.Audio;
using Xunit;

namespace Offstream.Core.Tests.Audio;

/// <summary>
/// The pacing layer between capture and the recorders. None of this needed an audio endpoint to
/// test, which is the point of separating it from WASAPI — in the reference implementation the
/// same logic could not be reached without one.
/// </summary>
public sealed class AudioCaptureBufferTests
{
    /// <summary>A tiny format so a "second" of audio is 100 bytes and assertions stay readable.</summary>
    private static WaveFormat TinyFormat() => new(50, 8, 2);

    private static AudioCaptureBuffer Buffer(int capacitySeconds = 4) =>
        new(TinyFormat(), capacitySeconds);

    private static byte[] Tone(int length, byte value = 0x7F)
    {
        var data = new byte[length];
        Array.Fill(data, value);

        return data;
    }

    [Fact]
    public void ChunkSize_IsOneSecondOfAudio()
    {
        var buffer = Buffer();

        Assert.Equal(100, buffer.ChunkSize);
        Assert.Equal(400, buffer.Capacity);
    }

    [Fact]
    public void Constructor_RejectsAZeroSecondCapacity() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new AudioCaptureBuffer(TinyFormat(), 0));

    [Fact]
    public void ReadChunk_ReturnsNothingUntilAWholeChunkExists()
    {
        var buffer = Buffer();
        var destination = new byte[buffer.Capacity];

        buffer.Write(Tone(99));

        Assert.False(buffer.HasChunk);
        Assert.Equal(0, buffer.ReadChunk(destination));

        buffer.Write(Tone(1));

        Assert.True(buffer.HasChunk);
        Assert.Equal(100, buffer.ReadChunk(destination));
        Assert.Equal(0, buffer.Count);
    }

    [Fact]
    public void ReadChunk_TakesExactlyOneChunkEvenWhenMoreIsBuffered()
    {
        var buffer = Buffer();
        var destination = new byte[buffer.Capacity];

        buffer.Write(Tone(250));

        Assert.Equal(100, buffer.ReadChunk(destination));
        Assert.Equal(150, buffer.Count);
    }

    [Fact]
    public void ReadChunk_RejectsADestinationTooSmallToHoldAChunk()
    {
        var buffer = Buffer();

        Assert.Throws<ArgumentException>(() => buffer.ReadChunk(new byte[99]));
    }

    [Fact]
    public void Drain_TakesEverythingBuffered()
    {
        var buffer = Buffer();
        var destination = new byte[buffer.Capacity];

        buffer.Write(Tone(150));

        Assert.Equal(150, buffer.Drain(destination));
        Assert.Equal(0, buffer.Count);
        Assert.Equal(0, buffer.Drain(destination));
    }

    [Fact]
    public void Drain_TakesWhatFitsWhenTheDestinationIsSmaller()
    {
        var buffer = Buffer();

        buffer.Write(Tone(300));

        Assert.Equal(120, buffer.Drain(new byte[120]));
        Assert.Equal(180, buffer.Count);
    }

    /// <summary>
    /// What is buffered when a track starts is the tail of the previous one. Keeping it is how a
    /// recording ends up opening with the last second of the song before it.
    /// </summary>
    [Fact]
    public void DiscardBuffered_DropsTheTailOfThePreviousTrack()
    {
        var buffer = Buffer();

        buffer.Write(Tone(250));

        Assert.Equal(250, buffer.DiscardBuffered());
        Assert.Equal(0, buffer.Count);
        Assert.False(buffer.HasChunk);
    }

    [Fact]
    public void Write_PreservesTheAudioItself()
    {
        var buffer = Buffer();
        var destination = new byte[buffer.Capacity];
        var written = new byte[100];

        for (var i = 0; i < written.Length; i++) written[i] = (byte)(i % 256);

        buffer.Write(written);
        buffer.ReadChunk(destination);

        Assert.Equal(written, destination[..100]);
    }

    [Fact]
    public void IsReady_TurnsOnPastHalfCapacity()
    {
        var buffer = Buffer();

        buffer.Write(Tone(200));
        Assert.False(buffer.IsReady);

        buffer.Write(Tone(1));
        Assert.True(buffer.IsReady);
    }

    [Fact]
    public async Task WaitForChunkAsync_ReturnsImmediatelyWhenAChunkIsAlreadyThere()
    {
        var buffer = Buffer();
        buffer.Write(Tone(100));

        await buffer.WaitForChunkAsync();
    }

    [Fact]
    public async Task WaitForChunkAsync_CompletesWhenCaptureDeliversAChunk()
    {
        var buffer = Buffer();

        var waiting = buffer.WaitForChunkAsync();

        Assert.False(waiting.IsCompleted);

        buffer.Write(Tone(60));
        Assert.False(waiting.IsCompleted);

        buffer.Write(Tone(40));

        await waiting.WaitAsync(TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// The gate has to re-arm after a read, or every subsequent wait returns instantly and the
    /// recorder loop spins against the capture callback.
    /// </summary>
    [Fact]
    public async Task WaitForChunkAsync_WaitsAgainAfterTheChunkIsConsumed()
    {
        var buffer = Buffer();
        var destination = new byte[buffer.Capacity];

        buffer.Write(Tone(100));
        await buffer.WaitForChunkAsync();
        buffer.ReadChunk(destination);

        var waiting = buffer.WaitForChunkAsync();
        Assert.False(waiting.IsCompleted);

        buffer.Write(Tone(100));

        await waiting.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task WaitForChunkAsync_ObservesCancellation()
    {
        var buffer = Buffer();
        using var cancellation = new CancellationTokenSource();

        var waiting = buffer.WaitForChunkAsync(cancellation.Token);
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
    }

    [Fact]
    public async Task Reset_EmptiesTheBufferAndRearmsTheGate()
    {
        var buffer = Buffer();
        buffer.Write(Tone(200));

        buffer.Reset();

        Assert.Equal(0, buffer.Count);

        var waiting = buffer.WaitForChunkAsync();
        Assert.False(waiting.IsCompleted);

        buffer.Write(Tone(100));
        await waiting.WaitAsync(TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// Capture must never block, so a recorder that falls behind loses the oldest audio rather
    /// than stalling the callback that feeds every listener.
    /// </summary>
    [Fact]
    public void Write_PastCapacity_DropsAudioRatherThanBlocking()
    {
        var buffer = Buffer();

        buffer.Write(Tone(400));
        var written = buffer.Write(Tone(100));

        Assert.Equal(400, buffer.Count);
        Assert.True(written < 100);
    }
}
