namespace ServerLeasing.Models;

public class Server
{
    public Guid Id { get; set; }
    public string OsName { get; set; } = string.Empty;
    public int MemoryGb { get; set; }
    public int DiskGb { get; set; }
    public int CpuCores { get; set; }
    public ServerStatus Status { get; set; }
    public ICollection<Lease> Leases { get; set; } = new List<Lease>();
}