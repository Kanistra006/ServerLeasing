namespace ServerLeasing.Models;

public class Lease
{
    public Guid Id { get; set; }
    public Guid ServerId { get; set; }
    public LeaseStatus Status { get; set; }
    public DateTime RequestedAt { get; set;} 
    public DateTime? ReadyAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public DateTime? ReleasedAt { get; set; }
    
    public Server? Server { get; set; }
}