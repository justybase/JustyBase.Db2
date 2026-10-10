using System.Net;
using System.Net.Sockets;
using JustyBase.Db2;
using Xunit;

namespace JustyBase.Db2.Tests;

public sealed partial class SessionTests
{
    [Fact]
    public async Task ReturningUnusedPoolLeasesDoesNotProbeRegistersAgain()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var server = Task.Run(async () =>
        {
            using var client = await AcceptInitializedPoolClientAsync(listener);
            var stream = client.GetStream();
            // After the first baseline, two unused logical leases produce no requests.
            // A probe would return a DSS header byte instead of EOF.
            Assert.Equal(0, await stream.ReadAsync(new byte[1]).AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        });
        await using (var pool = new Db2Pool(TestConfig(((IPEndPoint)listener.LocalEndpoint).Port), new Db2PoolOptions(1)))
        {
            var first = await pool.AcquireSessionAsync();
            await first.DisposeAsync();
            Assert.False(first.Session.IsClosed);
            var second = await pool.AcquireSessionAsync();
            Assert.Same(first.Session, second.Session);
            await second.DisposeAsync();
        }
        await server;
    }
}
