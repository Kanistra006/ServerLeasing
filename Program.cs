using Microsoft.EntityFrameworkCore;
using ServerLeasing.Data;
using ServerLeasing.Models;

var builder = WebApplication.CreateBuilder(args);


builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));
var app = builder.Build();
app.MapGet("/", () => "Hello World!");

app.MapPost("/api/servers", async (CreateServerRequest request, AppDbContext db) =>
{
    var server = new Server()
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
    return Results.Created($"/api/servers/{server.Id}", server);
});

app.MapGet("/api/servers/available", async (AppDbContext db,  string? osName, int? memoryGb, int? diskGb, int? cpuCores) =>
{
    var query = db.Servers
        .AsNoTracking()
        .Where(s => s.Status == ServerStatus.AvailableOn ||
                          s.Status == ServerStatus.AvailableOff);
    if (!string.IsNullOrWhiteSpace(osName))
        query = query.Where(s => s.OsName == osName);

    if (memoryGb.HasValue)
        query = query.Where(s => s.MemoryGb == memoryGb.Value);

    if (diskGb.HasValue)
        query = query.Where(s => s.DiskGb == diskGb.Value);

    if (cpuCores.HasValue)
        query = query.Where(s => s.CpuCores == cpuCores.Value);

    var servers = await query.ToListAsync();
    return Results.Ok(servers);
});

app.MapPost("/api/servers/{serverId:guid}/lease", async (Guid serverId, AppDbContext db) =>
{
    var server = await db.Servers.FindAsync(serverId);
    if (server == null) return Results.NotFound();
    if (server.Status is ServerStatus.Leased or ServerStatus.Starting)
    {
        return Results.Conflict($"Server with id {serverId} is already leased or starting.");
    }

    var now = DateTime.UtcNow;
    Lease lease;
    if (server.Status == ServerStatus.AvailableOn)
    {
        server.Status = ServerStatus.Leased;
        lease = new Lease
        {
            Id = Guid.NewGuid(),
            ServerId = server.Id,
            Status = LeaseStatus.Active,
            RequestedAt = now,
            ReadyAt = now,
            StartedAt = now,
            ExpiresAt = now.AddMinutes(20)
        };
        await db.Leases.AddAsync(lease);
        await db.SaveChangesAsync();

        return Results.Ok(new
        {
            message = "Server leased successfully.",
            leaseId = lease.Id,
            leaseStatus = lease.Status,
            serverStatus = server.Status,
            readyAt = lease.ReadyAt,
            startedAt = lease.StartedAt,
            expiresAt = lease.ExpiresAt
        });
    }
    if (server.Status == ServerStatus.AvailableOff)
    {
        server.Status = ServerStatus.Starting;
        lease = new Lease
        {
            Id = Guid.NewGuid(),
            ServerId = server.Id,
            Status = LeaseStatus.PendingStartup,
            RequestedAt = now,
            ReadyAt = now.AddMinutes(5)
        };
        await db.Leases.AddAsync(lease);
        await db.SaveChangesAsync();
        
        return Results.Ok(new
        {
            message = "Lease created. Server is starting.",
            leaseId = lease.Id,
            leaseStatus = lease.Status,
            serverStatus = server.Status,
            requestedAt = lease.RequestedAt,
            readyAt = lease.ReadyAt
        });
    }
    
    return Results.BadRequest(new
    {
        message = "Invalid server state."
    });
    
});

app.MapGet("/api/leases/{leaseId:guid}/status", async (Guid leaseId, AppDbContext db) =>
{
    var lease = await db.Leases.FindAsync(leaseId);
    if (lease == null) return Results.NotFound();
    var server = await db.Leases
        .Where(l => l.Id == leaseId)
        .Select(l => l.Server)
        .FirstOrDefaultAsync();
    if (server == null) return Results.NotFound();
    var now = DateTime.UtcNow;

    if (lease.Status == LeaseStatus.PendingStartup)
    {
        if (now < lease.ReadyAt)
        {
            return Results.Ok(new
            {
                message = "Server is still starting.",
                leaseId = lease.Id,
                leaseStatus = lease.Status,
                serverStatus = server.Status,
                requestedAt = lease.RequestedAt,
                readyAt = lease.ReadyAt,
                isReadyToActivate = false
            });
        }
        return Results.Ok(new
        {
            message = "Server is ready to be leased.",
            leaseId = lease.Id,
            leaseStatus = lease.Status,
            serverStatus = server.Status,
            requestedAt = lease.RequestedAt,
            readyAt = lease.ReadyAt,
            isReadyToActivate = true
        });
    }

    if (lease.Status == LeaseStatus.Active)
    {
        return Results.Ok(new
        {
            message = "Server is already active.",
            leaseId = lease.Id,
            leaseStatus = lease.Status,
            serverStatus = server.Status,
            requestedAt = lease.RequestedAt,
            readyAt = lease.ReadyAt
        });
    }

    if (lease.Status is LeaseStatus.Released or LeaseStatus.Expired)
    {
        return Results.Ok(new
        {
            message = "Lease was expired or released",
            leaseId = lease.Id,
            leaseStatus = lease.Status,
            serverStatus = server.Status,
            requestedAt = lease.RequestedAt,
            readyAt = lease.ReadyAt
        });
    }

    return Results.BadRequest("Unknown lease status");

});

app.MapPost("/api/servers/{serverId:guid}/release", async (Guid serverId, AppDbContext db) =>
{
    var lease = await db.Leases
        .FirstOrDefaultAsync(l => l.ServerId == serverId && l.Status == LeaseStatus.Active);
    if (lease is null) return Results.NotFound();
    lease.Status = LeaseStatus.Released;
    lease.ReleasedAt = DateTime.UtcNow;
    var server = await db.Servers.FindAsync(serverId);
    if (server is null) return Results.NotFound();
    server.Status = ServerStatus.AvailableOff;
    await db.SaveChangesAsync();
    return Results.Ok(new
    {
        message = "Server was released successfully",
        leaseId = lease.Id,
        leaseStatus = lease.Status,
        serverStatus = server.Status,
        requestedAt = lease.RequestedAt,
        startedAt = lease.StartedAt,
        releasedAt = lease.ReleasedAt
    });
    
});

app.MapPost("/api/leases/{leaseId:guid}/activate", async (Guid leaseId, AppDbContext db) =>
{
    var lease = await db.Leases.FindAsync(leaseId);
    if (lease is null) return Results.NotFound();

    var server = await db.Servers.FindAsync(lease.ServerId);
    if (server is null) return Results.NotFound();

    if (lease.Status == LeaseStatus.Active)
    {
        return Results.Ok(new
        {
            message = "Lease is already active.",
            leaseId = lease.Id,
            leaseStatus = lease.Status,
            serverStatus = server.Status,
            startedAt = lease.StartedAt,
            expiresAt = lease.ExpiresAt
        });
    }

    if (lease.Status is LeaseStatus.Released or LeaseStatus.Expired)
    {
        return Results.Conflict(new
        {
            message = "Lease can no longer be activated.",
            leaseId = lease.Id,
            leaseStatus = lease.Status,
            serverStatus = server.Status
        });
    }

    if (lease.Status != LeaseStatus.PendingStartup)
    {
        return Results.BadRequest(new
        {
            message = "Invalid lease status.",
            leaseId = lease.Id,
            leaseStatus = lease.Status
        });
    }

    if (!lease.ReadyAt.HasValue)
    {
        return Results.Problem("Lease has invalid state: ReadyAt is missing.");
    }

    var now = DateTime.UtcNow;

    if (now < lease.ReadyAt.Value)
    {
        return Results.Conflict(new
        {
            message = "Server is not ready yet.",
            leaseId = lease.Id,
            leaseStatus = lease.Status,
            serverStatus = server.Status,
            readyAt = lease.ReadyAt
        });
    }

    lease.Status = LeaseStatus.Active;
    lease.StartedAt = now;
    lease.ExpiresAt = now.AddMinutes(20);
    server.Status = ServerStatus.Leased;

    await db.SaveChangesAsync();

    return Results.Ok(new
    {
        message = "Server was activated successfully.",
        leaseId = lease.Id,
        leaseStatus = lease.Status,
        serverStatus = server.Status,
        requestedAt = lease.RequestedAt,
        readyAt = lease.ReadyAt,
        startedAt = lease.StartedAt,
        expiresAt = lease.ExpiresAt
    });
});

app.Run();