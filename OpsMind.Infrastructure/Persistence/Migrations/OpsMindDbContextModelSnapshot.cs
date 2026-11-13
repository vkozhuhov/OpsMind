using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using OpsMind.Infrastructure.Persistence;

namespace OpsMind.Infrastructure.Persistence.Migrations;

[DbContext(typeof(OpsMindDbContext))]
public sealed class OpsMindDbContextModelSnapshot : ModelSnapshot
{
    protected override void BuildModel(ModelBuilder modelBuilder)
    {
        modelBuilder.HasAnnotation("ProductVersion", "10.0.15");
    }
}
