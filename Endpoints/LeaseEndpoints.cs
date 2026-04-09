using Microsoft.EntityFrameworkCore;
using ServerLeasing.Data;
using ServerLeasing.Models;

namespace ServerLeasing.Endpoints;

public static class LeaseEndpoints
{
    public static IEndpointRouteBuilder MapLeaseEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/leases/{leaseId:guid}/status", GetLeaseStatus);
        app.MapPost("/api/leases/{leaseId:guid}/activate", ActivateLease);
        return app;
    }

    private static async Task<IResult> GetLeaseStatus(Guid leaseId, AppDbContext db, ILogger<Program> logger)
    {
        var lease = await db.Leases.FindAsync(leaseId);
        if (lease == null)
        {
            logger.LogWarning("Lease status check failed: lease {LeaseId} not found.", leaseId);
            return Results.NotFound();
        }

        var server = await db.Leases
            .Where(l => l.Id == leaseId)
            .Select(l => l.Server)
            .FirstOrDefaultAsync();

        if (server == null)
        {
            logger.LogWarning("Lease status check failed: server for lease {LeaseId} not found.", leaseId);
            return Results.NotFound();
        }

        var now = DateTime.UtcNow;

        if (lease.Status == LeaseStatus.PendingStartup)
        {
            if (now < lease.ReadyAt)
            {
                logger.LogInformation("Lease {LeaseId} status requested: PendingStartup, not ready.", leaseId);
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

            logger.LogInformation("Lease {LeaseId} status requested: PendingStartup, ready to activate.", leaseId);
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
            logger.LogInformation("Lease {LeaseId} status requested: Active.", leaseId);
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
            logger.LogInformation("Lease {LeaseId} status requested: {LeaseStatus}.", leaseId, lease.Status);
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

        logger.LogWarning("Lease status check failed: unknown status for lease {LeaseId}.", leaseId);
        return Results.BadRequest("Unknown lease status");
    }

    private static async Task<IResult> ActivateLease(Guid leaseId, AppDbContext db, ILogger<Program> logger)
    {
        var lease = await db.Leases
            .AsNoTracking()
            .FirstOrDefaultAsync(l => l.Id == leaseId);

        if (lease is null)
        {
            logger.LogWarning("Activate request failed: lease {LeaseId} not found.", leaseId);
            return Results.NotFound();
        }

        var server = await db.Servers
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == lease.ServerId);

        if (server is null)
        {
            logger.LogWarning("Activate request failed: server for lease {LeaseId} not found.", leaseId);
            return Results.NotFound();
        }

        if (lease.Status == LeaseStatus.Active)
        {
            logger.LogInformation("Activate request ignored: lease {LeaseId} already active.", leaseId);
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
            logger.LogWarning("Activate request conflict: lease {LeaseId} is {LeaseStatus}.", leaseId, lease.Status);
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
            logger.LogWarning("Activate request failed: lease {LeaseId} has invalid status {LeaseStatus}.", leaseId, lease.Status);
            return Results.BadRequest(new
            {
                message = "Invalid lease status.",
                leaseId = lease.Id,
                leaseStatus = lease.Status
            });
        }

        if (!lease.ReadyAt.HasValue)
        {
            logger.LogError("Activate request failed: lease {LeaseId} has null ReadyAt.", leaseId);
            return Results.Problem("Lease has invalid state: ReadyAt is missing.");
        }

        var now = DateTime.UtcNow;

        if (now < lease.ReadyAt.Value)
        {
            logger.LogWarning("Activate request conflict: lease {LeaseId} not ready yet.", leaseId);
            return Results.Conflict(new
            {
                message = "Server is not ready yet.",
                leaseId = lease.Id,
                leaseStatus = lease.Status,
                serverStatus = server.Status,
                readyAt = lease.ReadyAt
            });
        }

        await using var tx = await db.Database.BeginTransactionAsync();

        var leaseActivated = await db.Leases
            .Where(l =>
                l.Id == leaseId &&
                l.Status == LeaseStatus.PendingStartup &&
                l.ReadyAt.HasValue &&
                l.ReadyAt.Value <= now)
            .ExecuteUpdateAsync(update => update
                .SetProperty(l => l.Status, LeaseStatus.Active)
                .SetProperty(l => l.StartedAt, now)
                .SetProperty(l => l.ExpiresAt, now.AddMinutes(20)));

        if (leaseActivated == 0)
        {
            await tx.RollbackAsync();
            logger.LogWarning("Activate request conflict: lease {LeaseId} state changed concurrently.", leaseId);
            return Results.Conflict(new
            {
                message = "Lease state changed, retry status check.",
                leaseId = lease.Id
            });
        }

        await db.Servers
            .Where(s => s.Id == lease.ServerId && s.Status == ServerStatus.Starting)
            .ExecuteUpdateAsync(update => update.SetProperty(s => s.Status, ServerStatus.Leased));

        await tx.CommitAsync();

        var activatedLease = await db.Leases.AsNoTracking().FirstAsync(l => l.Id == leaseId);
        logger.LogInformation("Lease {LeaseId} activated successfully for server {ServerId}.", leaseId, lease.ServerId);

        return Results.Ok(new
        {
            message = "Server was activated successfully.",
            leaseId = activatedLease.Id,
            leaseStatus = activatedLease.Status,
            serverStatus = ServerStatus.Leased,
            requestedAt = activatedLease.RequestedAt,
            readyAt = activatedLease.ReadyAt,
            startedAt = activatedLease.StartedAt,
            expiresAt = activatedLease.ExpiresAt
        });
    }
}
