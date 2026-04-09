namespace ServerLeasing.Services;

public class LeaseExpirationOptions
{
    public TimeSpan CheckInterval { get; set; } = TimeSpan.FromSeconds(15);
}
