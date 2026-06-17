using AwesomeAssertions;
using Starling.Net.Http.H2;

namespace Starling.Net.Tests.Http.H2;

/// <summary>Frame writer → reader round-trips, including HEADERS fragmentation.</summary>
[TestClass]
public class H2FrameTests
{
    [TestMethod]
    public async Task Data_window_update_and_rst_round_trip()
    {
        var ms = new MemoryStream();
        var writer = new H2FrameWriter(ms);

        var body = new byte[] { 1, 2, 3, 4, 5 };
        await writer.WriteDataAsync(1, body, endStream: true, CancellationToken.None);
        await writer.WriteWindowUpdateAsync(0, 1000, CancellationToken.None);
        await writer.WriteRstStreamAsync(3, H2ErrorCode.Cancel, CancellationToken.None);

        ms.Position = 0;
        var reader = new H2FrameReader(ms, H2Protocol.DefaultMaxFrameSize);

        var data = (await reader.ReadFrameAsync(CancellationToken.None))!.Value;
        data.Type.Should().Be(H2FrameType.Data);
        data.StreamId.Should().Be(1);
        data.HasFlag(H2Flags.EndStream).Should().BeTrue();
        data.Payload.Should().Equal(body);

        var win = (await reader.ReadFrameAsync(CancellationToken.None))!.Value;
        win.Type.Should().Be(H2FrameType.WindowUpdate);
        win.StreamId.Should().Be(0);

        var rst = (await reader.ReadFrameAsync(CancellationToken.None))!.Value;
        rst.Type.Should().Be(H2FrameType.RstStream);
        rst.StreamId.Should().Be(3);
        rst.Payload[3].Should().Be((byte)H2ErrorCode.Cancel);
    }

    [TestMethod]
    public async Task Reader_returns_null_at_clean_eof()
    {
        var reader = new H2FrameReader(new MemoryStream(), H2Protocol.DefaultMaxFrameSize);
        (await reader.ReadFrameAsync(CancellationToken.None)).Should().BeNull();
    }

    [TestMethod]
    public async Task Large_headers_block_fragments_into_continuation()
    {
        var ms = new MemoryStream();
        var writer = new H2FrameWriter(ms);

        // 25-byte block with a 10-byte peer frame size → HEADERS(10) +
        // CONTINUATION(10) + CONTINUATION(5, END_HEADERS).
        var block = new byte[25];
        for (var i = 0; i < block.Length; i++)
        {
            block[i] = (byte)i;
        }

        await writer.WriteHeadersAsync(1, block, endStream: true, peerMaxFrameSize: 10, CancellationToken.None);

        ms.Position = 0;
        var reader = new H2FrameReader(ms, H2Protocol.DefaultMaxFrameSize);

        var f1 = (await reader.ReadFrameAsync(CancellationToken.None))!.Value;
        f1.Type.Should().Be(H2FrameType.Headers);
        f1.HasFlag(H2Flags.EndStream).Should().BeTrue();
        f1.HasFlag(H2Flags.EndHeaders).Should().BeFalse();
        f1.Payload.Length.Should().Be(10);

        var f2 = (await reader.ReadFrameAsync(CancellationToken.None))!.Value;
        f2.Type.Should().Be(H2FrameType.Continuation);
        f2.HasFlag(H2Flags.EndHeaders).Should().BeFalse();

        var f3 = (await reader.ReadFrameAsync(CancellationToken.None))!.Value;
        f3.Type.Should().Be(H2FrameType.Continuation);
        f3.HasFlag(H2Flags.EndHeaders).Should().BeTrue();
        f3.Payload.Length.Should().Be(5);

        var reassembled = f1.Payload.Concat(f2.Payload).Concat(f3.Payload).ToArray();
        reassembled.Should().Equal(block);
    }

    [TestMethod]
    public async Task Reader_rejects_frame_larger_than_max()
    {
        var ms = new MemoryStream();
        var writer = new H2FrameWriter(ms);
        await writer.WriteDataAsync(1, new byte[100], endStream: false, CancellationToken.None);

        ms.Position = 0;
        var reader = new H2FrameReader(ms, maxFrameSize: 50);
        var act = async () => await reader.ReadFrameAsync(CancellationToken.None);
        await act.Should().ThrowAsync<H2ConnectionException>();
    }
}
