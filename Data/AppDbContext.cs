using Microsoft.EntityFrameworkCore;
using ServerLeasing.Models;

namespace ServerLeasing.Data;

public class AppDbContext : DbContext
{
    public  DbSet<Server> Servers { get; set; } = null!;
    public  DbSet<Lease> Leases{ get; set; } = null!;
    
    public AppDbContext(DbContextOptions<AppDbContext> options)
        :base(options)
    {
    }

    
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Server>(server =>
        {
            server.HasKey(x => x.Id);

            server.Property(x => x.OsName).IsRequired();
            server.Property(x => x.MemoryGb).IsRequired();
            server.Property(x => x.DiskGb).IsRequired();
            server.Property(x => x.CpuCores).IsRequired();
            server.Property(x => x.Status).IsRequired();


        });
        modelBuilder.Entity<Lease>(lease =>
        {
            lease.HasKey(x => x.Id);

            lease.Property(x => x.ServerId).IsRequired();
            lease.Property(x => x.Status).IsRequired();
            lease.Property(x => x.RequestedAt).IsRequired();
            lease.Property(x => x.ReadyAt).IsRequired(false);
            lease.Property(x => x.StartedAt).IsRequired(false);
            lease.Property(x => x.ExpiresAt).IsRequired(false);
            lease.Property(x => x.ReleasedAt).IsRequired(false);
        });

        modelBuilder.Entity<Lease>(lease =>
        {
            lease
                .HasOne(l => l.Server)
                .WithMany(s => s.Leases)
                .HasForeignKey(l => l.ServerId);
        });
    }
}