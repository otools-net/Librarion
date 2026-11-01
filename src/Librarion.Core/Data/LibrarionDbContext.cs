using Microsoft.EntityFrameworkCore;

namespace Librarion.Core.Data;

/// <summary>
/// The model, shared by both providers. Each provider has its own derived context
/// (<see cref="SqliteLibrarionDbContext"/>, <see cref="PostgresLibrarionDbContext"/>) only so that each keeps its own
/// migrations; code asks for <see cref="IDbContextFactory{LibrarionDbContext}"/> and never knows which one it got
/// (agents/docs/development/database.md).
/// </summary>
public abstract class LibrarionDbContext(DbContextOptions options) : DbContext(options)
{
    public DbSet<ServerProperty> ServerProperties => Set<ServerProperty>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ServerProperty>(entity =>
        {
            entity.ToTable("ServerProperties");
            entity.HasKey(property => property.Key);
            entity.Property(property => property.Key).HasMaxLength(ServerProperty.MaxKeyLength);
        });
    }
}

/// <summary>The SQLite flavour of <see cref="LibrarionDbContext"/>: its migrations are in <c>Data/Migrations/Sqlite</c>.</summary>
public sealed class SqliteLibrarionDbContext(DbContextOptions<SqliteLibrarionDbContext> options) : LibrarionDbContext(options);

/// <summary>The PostgreSQL flavour of <see cref="LibrarionDbContext"/>: its migrations are in <c>Data/Migrations/Postgres</c>.</summary>
public sealed class PostgresLibrarionDbContext(DbContextOptions<PostgresLibrarionDbContext> options) : LibrarionDbContext(options);
