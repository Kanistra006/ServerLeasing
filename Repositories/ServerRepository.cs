using ServerLeasing.Data;
using ServerLeasing.Models;

namespace ServerLeasing.Repositories;

public class ServerRepository : IServerRepository
{
    private readonly AppDbContext _context;

    public ServerRepository(AppDbContext context)
    {
        _context = context;
    }

    public async Task AddAsync(Server server)
    {
        await _context.Servers.AddAsync(server);
        await _context.SaveChangesAsync();
    }
}