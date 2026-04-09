using Microsoft.EntityFrameworkCore;
using ServerLeasing.Data;
using ServerLeasing.Services;
using ServerLeasing.Endpoints;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<AppDbContext>(options =>
{
    var provider = builder.Configuration["Database:Provider"];
    var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");

    if (string.Equals(provider, "Sqlite", StringComparison.OrdinalIgnoreCase))
    {
        options.UseSqlite(connectionString);
        return;
    }

    options.UseNpgsql(connectionString);
});

builder.Services.Configure<LeaseExpirationOptions>(builder.Configuration.GetSection("LeaseExpiration"));
builder.Services.AddHostedService<LeaseExpirationHostedService>();

var app = builder.Build();

app.MapGet("/", () => "Hello World!");
app.MapServerEndpoints();
app.MapLeaseEndpoints();

app.Run();

public partial class Program
{
}
