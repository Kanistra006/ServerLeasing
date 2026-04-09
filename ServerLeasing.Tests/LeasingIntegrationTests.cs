using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ServerLeasing.Data;
using ServerLeasing.Models;

namespace ServerLeasing.Tests;

public class LeasingIntegrationTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public LeasingIntegrationTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task LeaseEndpoint_ShouldAllowOnlyOneSuccessfulConcurrentLease()
    {
        var serverId = Guid.NewGuid();

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Servers.Add(new Server
            {
                Id = serverId,
                OsName = "ubuntu",
                MemoryGb = 8,
                DiskGb = 200,
                CpuCores = 4,
                Status = ServerStatus.AvailableOff
            });
            await db.SaveChangesAsync();
        }

        var client = _factory.CreateClient();

        var firstLease = client.PostAsync($"/api/servers/{serverId}/lease", content: null);
        var secondLease = client.PostAsync($"/api/servers/{serverId}/lease", content: null);
        var responses = await Task.WhenAll(firstLease, secondLease);

        Assert.Contains(responses, r => r.StatusCode == HttpStatusCode.OK);
        Assert.Contains(responses, r => r.StatusCode == HttpStatusCode.Conflict);

        using var verifyScope = _factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();

        var leasesCount = await verifyDb.Leases.CountAsync(l => l.ServerId == serverId);
        var server = await verifyDb.Servers.FirstAsync(s => s.Id == serverId);

        Assert.Equal(1, leasesCount);
        Assert.Equal(ServerStatus.Starting, server.Status);
    }

    [Fact]
    public async Task ExpirationWorker_ShouldExpireLeaseAndPowerOffServer()
    {
        var serverId = Guid.NewGuid();
        var leaseId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            db.Servers.Add(new Server
            {
                Id = serverId,
                OsName = "ubuntu",
                MemoryGb = 16,
                DiskGb = 300,
                CpuCores = 8,
                Status = ServerStatus.Leased
            });

            db.Leases.Add(new Lease
            {
                Id = leaseId,
                ServerId = serverId,
                Status = LeaseStatus.Active,
                RequestedAt = now.AddMinutes(-30),
                ReadyAt = now.AddMinutes(-30),
                StartedAt = now.AddMinutes(-25),
                ExpiresAt = now.AddMinutes(-1)
            });

            await db.SaveChangesAsync();
        }

        var client = _factory.CreateClient();
        _ = await client.GetAsync("/");

        var expired = await WaitForConditionAsync(async () =>
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var lease = await db.Leases.AsNoTracking().FirstAsync(l => l.Id == leaseId);
            return lease.Status == LeaseStatus.Expired;
        }, timeout: TimeSpan.FromSeconds(5));

        Assert.True(expired);

        using var verifyScope = _factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var leaseAfter = await verifyDb.Leases.AsNoTracking().FirstAsync(l => l.Id == leaseId);
        var serverAfter = await verifyDb.Servers.AsNoTracking().FirstAsync(s => s.Id == serverId);

        Assert.Equal(LeaseStatus.Expired, leaseAfter.Status);
        Assert.Null(leaseAfter.ReleasedAt);
        Assert.Equal(ServerStatus.AvailableOff, serverAfter.Status);
    }

    private static async Task<bool> WaitForConditionAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var started = DateTime.UtcNow;

        while (DateTime.UtcNow - started < timeout)
        {
            if (await condition())
            {
                return true;
            }

            await Task.Delay(100);
        }

        return false;
    }
}
