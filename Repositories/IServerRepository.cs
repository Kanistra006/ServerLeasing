using ServerLeasing.Models;

namespace ServerLeasing.Repositories;

public interface IServerRepository
{
    public Task AddAsync(Server server);
}