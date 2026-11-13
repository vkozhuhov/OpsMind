using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace OpsMind.Infrastructure.Persistence.Migrations;

[DbContext(typeof(OpsMindDbContext))]
[Migration("20261113000000_InitialInfrastructure")]
public sealed class InitialInfrastructure : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS vector;");
        migrationBuilder.Sql("CREATE SCHEMA IF NOT EXISTS iam;");
        migrationBuilder.Sql("CREATE SCHEMA IF NOT EXISTS knowledge;");
        migrationBuilder.Sql("CREATE SCHEMA IF NOT EXISTS chat;");
        migrationBuilder.Sql("CREATE SCHEMA IF NOT EXISTS rag;");
        migrationBuilder.Sql("CREATE SCHEMA IF NOT EXISTS ops;");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DROP SCHEMA IF EXISTS ops;");
        migrationBuilder.Sql("DROP SCHEMA IF EXISTS rag;");
        migrationBuilder.Sql("DROP SCHEMA IF EXISTS chat;");
        migrationBuilder.Sql("DROP SCHEMA IF EXISTS knowledge;");
        migrationBuilder.Sql("DROP SCHEMA IF EXISTS iam;");
        migrationBuilder.Sql("DROP EXTENSION IF EXISTS vector;");
    }
}
