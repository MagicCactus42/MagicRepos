using FluentAssertions;
using MagicRepos.Protocol;
using MagicRepos.Protocol.Messages;

namespace MagicRepos.Protocol.Tests;

public class MessageSerializerTests
{
    [Fact]
    public async Task WriteMessageAsync_and_ReadFrameAsync_roundtrip_NegotiateRequest()
    {
        // Arrange
        using var stream = new MemoryStream();
        var original = new NegotiateRequest
        {
            Operation = NegotiateOperation.Push,
            Repository = "my-repo",
            Username = "testuser"
        };

        // Act — write
        await MessageSerializer.WriteMessageAsync(stream, MessageType.NegotiateRequest, original);

        // Seek back to beginning for reading
        stream.Position = 0;

        // Act — read
        (MessageType type, byte[] payload) = await MessageSerializer.ReadFrameAsync(stream);

        // Assert
        type.Should().Be(MessageType.NegotiateRequest);

        NegotiateRequest deserialized = MessageSerializer.Deserialize<NegotiateRequest>(payload);
        deserialized.Operation.Should().Be(NegotiateOperation.Push);
        deserialized.Repository.Should().Be("my-repo");
        deserialized.Username.Should().Be("testuser");
    }

    [Fact]
    public async Task WriteMessageAsync_and_ReadFrameAsync_roundtrip_NegotiateResponse()
    {
        // Arrange
        using var stream = new MemoryStream();
        var original = new NegotiateResponse
        {
            Success = true,
            ErrorMessage = null
        };

        // Act
        await MessageSerializer.WriteMessageAsync(stream, MessageType.NegotiateResponse, original);
        stream.Position = 0;
        (MessageType type, byte[] payload) = await MessageSerializer.ReadFrameAsync(stream);

        // Assert
        type.Should().Be(MessageType.NegotiateResponse);
        var deserialized = MessageSerializer.Deserialize<NegotiateResponse>(payload);
        deserialized.Success.Should().BeTrue();
        deserialized.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public async Task WriteMessageAsync_and_ReadFrameAsync_roundtrip_ErrorResponse()
    {
        // Arrange
        using var stream = new MemoryStream();
        var original = new ErrorResponse
        {
            Message = "Something went wrong",
            Code = 500
        };

        // Act
        await MessageSerializer.WriteMessageAsync(stream, MessageType.Error, original);
        stream.Position = 0;
        (MessageType type, byte[] payload) = await MessageSerializer.ReadFrameAsync(stream);

        // Assert
        type.Should().Be(MessageType.Error);
        var deserialized = MessageSerializer.Deserialize<ErrorResponse>(payload);
        deserialized.Message.Should().Be("Something went wrong");
        deserialized.Code.Should().Be(500);
    }

    [Fact]
    public async Task WriteMessageAsync_and_ReadFrameAsync_roundtrip_PackData()
    {
        // Arrange
        using var stream = new MemoryStream();
        byte[] data = new byte[256];
        Random.Shared.NextBytes(data);
        var original = new PackData
        {
            Data = data,
            SequenceNumber = 42
        };

        // Act
        await MessageSerializer.WriteMessageAsync(stream, MessageType.PackData, original);
        stream.Position = 0;
        (MessageType type, byte[] payload) = await MessageSerializer.ReadFrameAsync(stream);

        // Assert
        type.Should().Be(MessageType.PackData);
        var deserialized = MessageSerializer.Deserialize<PackData>(payload);
        deserialized.Data.Should().BeEquivalentTo(data);
        deserialized.SequenceNumber.Should().Be(42);
    }

    [Fact]
    public async Task WriteMessageAsync_and_ReadFrameAsync_roundtrip_RefAdvertisement()
    {
        // Arrange
        using var stream = new MemoryStream();
        var refs = new Dictionary<string, byte[]>
        {
            ["refs/heads/main"] = new byte[32],
            ["refs/heads/feature"] = new byte[32]
        };
        Random.Shared.NextBytes(refs["refs/heads/main"]);
        Random.Shared.NextBytes(refs["refs/heads/feature"]);

        var original = new RefAdvertisement { Refs = refs };

        // Act
        await MessageSerializer.WriteMessageAsync(stream, MessageType.RefAdvertisement, original);
        stream.Position = 0;
        (MessageType type, byte[] payload) = await MessageSerializer.ReadFrameAsync(stream);

        // Assert
        type.Should().Be(MessageType.RefAdvertisement);
        var deserialized = MessageSerializer.Deserialize<RefAdvertisement>(payload);
        deserialized.Refs.Should().HaveCount(2);
        deserialized.Refs["refs/heads/main"].Should().BeEquivalentTo(refs["refs/heads/main"]);
        deserialized.Refs["refs/heads/feature"].Should().BeEquivalentTo(refs["refs/heads/feature"]);
    }

    [Fact]
    public async Task Multiple_messages_can_be_written_and_read_sequentially()
    {
        // Arrange
        using var stream = new MemoryStream();

        var msg1 = new NegotiateRequest { Operation = NegotiateOperation.Pull, Repository = "repo1" };
        var msg2 = new OkResponse { Message = "All good" };
        var msg3 = new ErrorResponse { Message = "Oops", Code = 404 };

        // Act — write all
        await MessageSerializer.WriteMessageAsync(stream, MessageType.NegotiateRequest, msg1);
        await MessageSerializer.WriteMessageAsync(stream, MessageType.Ok, msg2);
        await MessageSerializer.WriteMessageAsync(stream, MessageType.Error, msg3);

        stream.Position = 0;

        // Act — read all
        var frame1 = await MessageSerializer.ReadFrameAsync(stream);
        var frame2 = await MessageSerializer.ReadFrameAsync(stream);
        var frame3 = await MessageSerializer.ReadFrameAsync(stream);

        // Assert
        frame1.Type.Should().Be(MessageType.NegotiateRequest);
        var d1 = MessageSerializer.Deserialize<NegotiateRequest>(frame1.Payload);
        d1.Repository.Should().Be("repo1");

        frame2.Type.Should().Be(MessageType.Ok);
        var d2 = MessageSerializer.Deserialize<OkResponse>(frame2.Payload);
        d2.Message.Should().Be("All good");

        frame3.Type.Should().Be(MessageType.Error);
        var d3 = MessageSerializer.Deserialize<ErrorResponse>(frame3.Payload);
        d3.Message.Should().Be("Oops");
        d3.Code.Should().Be(404);
    }

    [Fact]
    public async Task ReadFrameAsync_throws_on_truncated_stream()
    {
        // Arrange — write only partial header
        using var stream = new MemoryStream([0x00, 0x00]);

        // Act & Assert
        Func<Task> act = async () => await MessageSerializer.ReadFrameAsync(stream);
        await act.Should().ThrowAsync<EndOfStreamException>();
    }

    [Fact]
    public async Task WriteMessageAsync_and_ReadFrameAsync_roundtrip_PrCreateRequest()
    {
        // Arrange
        using var stream = new MemoryStream();
        var original = new PrCreateRequest
        {
            Title = "Add feature",
            Description = "This PR adds a new feature",
            SourceBranch = "feature-branch",
            TargetBranch = "main"
        };

        // Act
        await MessageSerializer.WriteMessageAsync(stream, MessageType.PrCreate, original);
        stream.Position = 0;
        (MessageType type, byte[] payload) = await MessageSerializer.ReadFrameAsync(stream);

        // Assert
        type.Should().Be(MessageType.PrCreate);
        var deserialized = MessageSerializer.Deserialize<PrCreateRequest>(payload);
        deserialized.Title.Should().Be("Add feature");
        deserialized.Description.Should().Be("This PR adds a new feature");
        deserialized.SourceBranch.Should().Be("feature-branch");
        deserialized.TargetBranch.Should().Be("main");
    }
}
