using Xunit;
using Drone.Native;

namespace Drone.Tests;

public class NativeTests
{
    [Fact]
    public void TryParseHeaderNative_TooSmallData_ReturnsFalse()
    {
        var result = NativeBindings.TryParseHeaderNative(new byte[5], out _, out _, out _);
        Assert.False(result);
    }

    [Fact]
    public void TryParseHeaderNative_DoesNotThrow_WhenNativeUnavailable()
    {
        var data = new byte[16];
        var exception = Record.Exception(() => NativeBindings.TryParseHeaderNative(data, out _, out _, out _));
        Assert.Null(exception);
    }

    [Fact]
    public void ScreenDiffNative_WhenUnavailable_ReturnsNullOrGraceful()
    {
        var bufferA = new byte[100];
        var bufferB = new byte[100];
        var exception = Record.Exception(() => NativeBindings.ScreenDiffNative(bufferA, bufferB, 25));
        Assert.Null(exception);
    }

    [Fact]
    public void ComputeMerkleRootNative_EmptyLeaves_ReturnsNull()
    {
        var result = NativeBindings.ComputeMerkleRootNative(Array.Empty<byte[]>());
        Assert.Null(result);
    }

    [Fact]
    public void VerifyMerkleProofNative_WhenUnavailable_DoesNotThrow()
    {
        var root = new byte[32];
        var leaf = new byte[32];
        var exception = Record.Exception(() => NativeBindings.VerifyMerkleProofNative(root, leaf, Array.Empty<byte[]>(), 0));
        Assert.Null(exception);
    }

    [Fact]
    public void TryParseMerkleFrame_RejectsShortData()
    {
        var merkleRoot = new byte[32];
        var result = NativeBindings.TryParseMerkleFrame(new byte[10], merkleRoot, out _);
        Assert.False(result);
    }

    [Fact]
    public void TryParseMerkleFrame_RejectsBadMagic()
    {
        var data = new byte[100];
        data[0] = 0xFF;
        var merkleRoot = new byte[32];
        var result = NativeBindings.TryParseMerkleFrame(data, merkleRoot, out _);
        Assert.False(result);
    }

    [Fact]
    public void DeltaEngine_IsNativeAvailable_ReturnsBool()
    {
        var result = DeltaEngine.IsNativeAvailable;
        Assert.IsType<bool>(result);
    }

    [Fact]
    public void NativeBindings_Constants_HaveExpectedValues()
    {
        Assert.Equal(36, NativeBindings.NmcpMerkleHeaderSize);
        Assert.Equal(32, NativeBindings.MerkleRootSize);
    }
}
