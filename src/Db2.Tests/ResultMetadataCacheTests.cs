using JustyBase.Db2.Protocol;
using Xunit;
namespace JustyBase.Db2.Tests;
public sealed class ResultMetadataCacheTests
{
    [Fact]
    public void WireFingerprintReusesPlansAndInvalidatesOnDescriptorChange()
    {
        var cache = new Db2ResultMetadataCache();
        byte[] descriptor = [6, 0x76, 0xd0, 3, 0, 4];
        var fields = cache.ReadFields(descriptor); var plans = cache.Decoders;
        Assert.Same(fields, cache.ReadFields(descriptor.ToArray())); Assert.Same(plans, cache.Decoders);
        descriptor[3] = 5; descriptor[5] = 2;
        Assert.NotSame(fields, cache.ReadFields(descriptor)); Assert.Equal((byte)5, cache.Decoders[0].Field.Type);
        var updatedPlans = cache.Decoders;
        Assert.Throws<InvalidDataException>(() => cache.ReadFields([0]));
        Assert.Same(updatedPlans, cache.Decoders);
        Assert.NotSame(plans, cache.Decoders);
    }
}
