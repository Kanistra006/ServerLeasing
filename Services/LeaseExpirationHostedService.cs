using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ServerLeasing.Data;
using ServerLeasing.Models;

namespace ServerLeasing.Services;

public class LeaseExpirationHostedService : BackgroundService
{
    private readonly TimeSpan _checkInterval;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<LeaseExpirationHostedService> _logger;

    public LeaseExpirationHostedService(
        IServiceScopeFactory scopeFactory,
        IOptions<LeaseExpirationOptions> options,
        ILogger<LeaseExpirationHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _checkInterval = options.Value.CheckInterval > TimeSpan.Zero
            ? options.Value.CheckInterval
            : TimeSpan.FromSeconds(15);
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_checkInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ExpireLeasesAsync(stoppingToken);
                await timer.WaitForNextTickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process lease expiration cycle.");
            }
        }
    }

    private async Task ExpireLeasesAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTime.UtcNow;

        var expiredLeaseData = await db.Leases
            .Where(l => l.Status == LeaseStatus.Active && l.ExpiresAt.HasValue && l.ExpiresAt.Value <= now)
            .Select(l => new { l.Id, l.ServerId })
            .ToListAsync(cancellationToken);

        if (expiredLeaseData.Count == 0)
        {
            return;
        }

        var expiredLeaseIds = expiredLeaseData.Select(x => x.Id).ToList();
        var serverIds = expiredLeaseData.Select(x => x.ServerId).Distinct().ToList();

        var updatedLeases = await db.Leases
            .Where(l => expiredLeaseIds.Contains(l.Id) && l.Status == LeaseStatus.Active)
            .ExecuteUpdateAsync(
                update => update.SetProperty(l => l.Status, LeaseStatus.Expired),
                cancellationToken);

        if (updatedLeases == 0)
        {
            return;
        }

        await db.Servers
            .Where(s => serverIds.Contains(s.Id) && s.Status == ServerStatus.Leased)
            .ExecuteUpdateAsync(
                update => update.SetProperty(s => s.Status, ServerStatus.AvailableOff),
                cancellationToken);

        _logger.LogInformation("Auto-expired {Count} lease(s) at {Timestamp}.", updatedLeases, now);
    }
}
