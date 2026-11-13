using Microsoft.EntityFrameworkCore;

namespace OpsMind.Infrastructure.Persistence;

public sealed class OpsMindDbContext(DbContextOptions<OpsMindDbContext> options) : DbContext(options);
