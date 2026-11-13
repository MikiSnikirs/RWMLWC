using RWMLWC.Shared;
using Xunit;

namespace RWMLWC.Tests;

public class ProtocolInfoTests
{
    [Fact]
    public void SameVersionIsCompatible()
    {
        Assert.True(ProtocolInfo.IsCompatible(ProtocolInfo.ProtocolVersion));
    }

    [Fact]
    public void DifferentVersionIsRejected()
    {
        Assert.False(ProtocolInfo.IsCompatible(ProtocolInfo.ProtocolVersion + 1));
    }
}
