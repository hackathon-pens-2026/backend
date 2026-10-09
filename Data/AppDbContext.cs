using Microsoft.EntityFrameworkCore;

namespace SignIt.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
}
