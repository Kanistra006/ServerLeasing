namespace ServerLeasing.Models;

public record CreateServerRequest(string OsName, int MemoryGb, int DiskGb, int CpuCores);