using Microsoft.EntityFrameworkCore;
using ServerLeasing.Data;
using ServerLeasing.Models;

namespace ServerLeasing.Endpoints;

public static class ServerEndpoints
{
    public static IEndpointRouteBuilder MapServerEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/servers", AddServer);
        app.MapGet("/api/servers/available", GetAvailableServers);
        app.MapPost("/api/servers/{serverId:guid}/lease", LeaseServer);
        app.MapPost("/api/servers/{serverId:guid}/release", ReleaseServer);
        return app;
    }

    private static async Task<IResult> AddServer(CreateServerRequest request, AppDbContext db, ILogger<Program> logger)
    {
        var server = new Server
        {
            Id = Guid.NewGuid(),
            OsName = request.OsName,
            MemoryGb = request.MemoryGb,
            DiskGb = request.DiskGb,
            CpuCores = request.CpuCores,
            Status = ServerStatus.AvailableOff
        };

        await db.Servers.AddAsync(server);
        await db.SaveChangesAsync();

        logger.LogInformation(
            "Server {ServerId} was added to pool. OS={OsName}, RAM={MemoryGb}, Disk={DiskGb}, CPU={CpuCores}.",
            server.Id,
            server.OsName,
            server.MemoryGb,
            server.DiskGb,
            server.CpuCores);

        return Results.Created($"/api/servers/{server.Id}", server);
    }

    private static async Task<IResult> GetAvailableServers(
        AppDbContext db,
        ILogger<Program> logger,
        string? osName,
        int? memoryGb,
        int? diskGb,
        int? cpuCores)
    {
        var query = db.Servers
            .AsNoTracking()
            .Where(s => s.Status == ServerStatus.AvailableOn || s.Status == ServerStatus.AvailableOff);

        if (!string.IsNullOrWhiteSpace(osName))
        {
            query = query.Where(s => s.OsName == osName);
        }

        if (memoryGb.HasValue)
        {
            query = query.Where(s => s.MemoryGb == memoryGb.Value);
        }

        if (diskGb.HasValue)
        {
            query = query.Where(s => s.DiskGb == diskGb.Value);
        }

        if (cpuCores.HasValue)
        {
            query = query.Where(s => s.CpuCores == cpuCores.Value);
        }

        var servers = await query.ToListAsync();

        logger.LogInformation(
            "Available servers requested. Filters: os={OsName}, memory={MemoryGb}, disk={DiskGb}, cpu={CpuCores}. ResultCount={Count}.",
            osName,
            memoryGb,
            diskGb,
            cpuCores,
            servers.Count);

        return Results.Ok(servers);
    }

    private static async Task<IResult> LeaseServer(Guid serverId, AppDbContext db, ILogger<Program> logger)
    {
        var now = DateTime.UtcNow;

        await using var tx = await db.Database.BeginTransactionAsync();

        var availableOnUpdated = await db.Servers
            .Where(s => s.Id == serverId && s.Status == ServerStatus.AvailableOn)
            .ExecuteUpdateAsync(update => update.SetProperty(s => s.Status, ServerStatus.Leased));

        if (availableOnUpdated == 1)
        {
            var lease = new Lease
            {
                Id = Guid.NewGuid(),
                ServerId = serverId,
                Status = LeaseStatus.Active,
                RequestedAt = now,
                ReadyAt = now,
                StartedAt = now,
                ExpiresAt = now.AddMinutes(20)
            };

            await db.Leases.AddAsync(lease);
            await db.SaveChangesAsync();
            await tx.CommitAsync();

            logger.LogInformation("Lease {LeaseId} activated immediately for server {ServerId}.", lease.Id, serverId);

            return Results.Ok(new
            {
                message = "Server leased successfully.",
                leaseId = lease.Id,
                leaseStatus = lease.Status,
                serverStatus = ServerStatus.Leased,
                readyAt = lease.ReadyAt,
                startedAt = lease.StartedAt,
                expiresAt = lease.ExpiresAt
            });
        }

        var availableOffUpdated = await db.Servers
            .Where(s => s.Id == serverId && s.Status == ServerStatus.AvailableOff)
            .ExecuteUpdateAsync(update => update.SetProperty(s => s.Status, ServerStatus.Starting));

        if (availableOffUpdated == 1)
        {
            var lease = new Lease
            {
                Id = Guid.NewGuid(),
                ServerId = serverId,
                Status = LeaseStatus.PendingStartup,
                RequestedAt = now,
                ReadyAt = now.AddMinutes(5)
            };

            await db.Leases.AddAsync(lease);
            await db.SaveChangesAsync();
            await tx.CommitAsync();

            logger.LogInformation("Lease {LeaseId} created for server {ServerId} in PendingStartup state.", lease.Id, serverId);

            return Results.Ok(new
            {
                message = "Lease created. Server is starting.",
                leaseId = lease.Id,
                leaseStatus = lease.Status,
                serverStatus = ServerStatus.Starting,
                requestedAt = lease.RequestedAt,
                readyAt = lease.ReadyAt
            });
        }

        await tx.RollbackAsync();
        var exists = await db.Servers.AnyAsync(s => s.Id == serverId);
        if (!exists)
        {
            logger.LogWarning("Lease request failed: server {ServerId} not found.", serverId);
            return Results.NotFound();
        }

        logger.LogWarning("Lease request conflict for server {ServerId}: already leased or starting.", serverId);
        return Results.Conflict($"Server with id {serverId} is already leased or starting.");
    }

    private static async Task<IResult> ReleaseServer(Guid serverId, AppDbContext db, ILogger<Program> logger)
    {
        var leaseInfo = await db.Leases
            .FirstOrDefaultAsync(l => l.ServerId == serverId && l.Status == LeaseStatus.Active);

        if (leaseInfo is null)
        {
            logger.LogWarning("Release request failed: active lease for server {ServerId} not found.", serverId);
            return Results.NotFound();
        }

        var now = DateTime.UtcNow;
        await using var tx = await db.Database.BeginTransactionAsync();

        var releasedCount = await db.Leases
            .Where(l => l.Id == leaseInfo.Id && l.Status == LeaseStatus.Active)
            .ExecuteUpdateAsync(update => update
                .SetProperty(l => l.Status, LeaseStatus.Released)
                .SetProperty(l => l.ReleasedAt, now));

        if (releasedCount == 0)
        {
            await tx.RollbackAsync();
            logger.LogWarning("Release request conflict for server {ServerId}: lease was already changed.", serverId);
            return Results.NotFound();
        }

        await db.Servers
            .Where(s => s.Id == serverId && s.Status == ServerStatus.Leased)
            .ExecuteUpdateAsync(update => update.SetProperty(s => s.Status, ServerStatus.AvailableOff));

        await tx.CommitAsync();

        var lease = await db.Leases.AsNoTracking().FirstAsync(l => l.Id == leaseInfo.Id);
        logger.LogInformation("Lease {LeaseId} released manually for server {ServerId}.", lease.Id, serverId);

        return Results.Ok(new
        {
            message = "Server was released successfully",
            leaseId = lease.Id,
            leaseStatus = lease.Status,
            serverStatus = ServerStatus.AvailableOff,
            requestedAt = lease.RequestedAt,
            startedAt = lease.StartedAt,
            releasedAt = lease.ReleasedAt
        });
    }
}
