using Xunit;

namespace Drone.Tests;

public class CoreTests
{
    [Fact]
    public void NmcpFrame_HeaderSize_Is16()
    {
        Assert.Equal(16, Drone.Core.Protocol.NmcpFrame.HeaderSize);
    }

    [Fact]
    public void NmcpFrame_Magic_IsVNMC()
    {
        Assert.Equal(0x564E4D43u, Drone.Core.Protocol.NmcpFrame.Magic);
    }

    [Fact]
    public void NmcpFrame_WriteAndRead_RoundTrips()
    {
        var frame = new Drone.Core.Protocol.NmcpFrame(10, 42, new byte[] { 1, 2, 3 });
        var header = new byte[16];
        frame.WriteHeader(header);

        var success = Drone.Core.Protocol.NmcpFrame.TryReadHeader(header, out var frameType, out var payloadLen, out var seqId);
        Assert.True(success);
        Assert.Equal(10u, frameType);
        Assert.Equal(3u, payloadLen);
        Assert.Equal(42u, seqId);
    }

    [Fact]
    public void DroneConfig_Defaults_AreSane()
    {
        var config = new Drone.Core.Config.DroneConfig();
        Assert.Equal("Drone", config.DroneId);
        Assert.Equal(Drone.Core.Config.DroneMode.Full, config.Mode);
        Assert.True(config.Uplink.AutoReconnect);
        Assert.Equal(4 * 1024 * 1024, config.Uplink.BufferSize);
    }
}
