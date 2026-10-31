using Microsoft.EntityFrameworkCore;

namespace Librarion.Core.Data;

/// <summary>
/// The model, shared by both providers. Each provider has its own derived context
/// (<see cref="SqliteLibrarionDbContext"/>, <see cref="PostgresLibrarionDbContext"/>) only so that each keeps its own
/// migrations; code asks for <see cref="TenantDb"/> (or <see cref="IDbContextFactory{LibrarionDbContext}"/>) and never
/// knows which one it got (agents/docs/development/database.md).
/// </summary>
public abstract class LibrarionDbContext(DbContextOptions options) : DbContext(options)
{
    public const int MaxNameLength = 100;

    /// <summary>
    /// The tenant this context works for: every tenant-owned set then shows that tenant's rows only (query filters,
    /// agents/docs/features/tenants.md). Null = the whole server, for server-wide code (<see cref="TenantDb.ForServerAsync"/>).
    /// </summary>
    public int? TenantScope { get; set; }

    public DbSet<ServerProperty> ServerProperties => Set<ServerProperty>();
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<Library> Libraries => Set<Library>();
    public DbSet<UserAccount> Users => Set<UserAccount>();
    public DbSet<UserGroup> Groups => Set<UserGroup>();
    public DbSet<GroupMember> GroupMembers => Set<GroupMember>();
    public DbSet<GroupRight> GroupRights => Set<GroupRight>();
    public DbSet<GroupLibrary> GroupLibraries => Set<GroupLibrary>();
    public DbSet<TenantDisabledRight> TenantDisabledRights => Set<TenantDisabledRight>();
    public DbSet<Setting> Settings => Set<Setting>();
    public DbSet<SecurityEvent> SecurityEvents => Set<SecurityEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ServerProperty>(entity =>
        {
            entity.ToTable("ServerProperties");
            entity.HasKey(property => property.Key);
            entity.Property(property => property.Key).HasMaxLength(ServerProperty.MaxKeyLength);
        });

        modelBuilder.Entity<Tenant>(entity =>
        {
            entity.Property(t => t.Slug).HasMaxLength(MaxNameLength);
            entity.Property(t => t.Name).HasMaxLength(MaxNameLength);
            entity.HasIndex(t => t.Slug).IsUnique();
            entity.HasQueryFilter(t => TenantScope == null || t.Id == TenantScope);
        });

        modelBuilder.Entity<Library>(entity =>
        {
            entity.Property(l => l.Name).HasMaxLength(MaxNameLength);
            entity.HasOne(l => l.Tenant).WithMany(t => t.Libraries).HasForeignKey(l => l.TenantId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(l => new { l.TenantId, l.Name });
            entity.HasQueryFilter(l => TenantScope == null || l.TenantId == TenantScope);
        });

        modelBuilder.Entity<UserAccount>(entity =>
        {
            entity.ToTable("Users");
            entity.Property(u => u.UserName).HasMaxLength(MaxNameLength);
            entity.Property(u => u.NormalizedUserName).HasMaxLength(MaxNameLength);
            entity.Property(u => u.DisplayName).HasMaxLength(MaxNameLength);
            entity.Property(u => u.Source).HasMaxLength(50);
            entity.Property(u => u.ExternalId).HasMaxLength(200);
            entity.Property(u => u.PasswordHash).HasMaxLength(200);
            entity.Property(u => u.SecurityStamp).HasMaxLength(64);
            entity.HasIndex(u => u.NormalizedUserName).IsUnique();
            entity.HasIndex(u => new { u.TenantId, u.NormalizedUserName });
            entity.HasIndex(u => new { u.Source, u.ExternalId });
            entity.HasOne(u => u.Tenant).WithMany().HasForeignKey(u => u.TenantId).OnDelete(DeleteBehavior.Cascade);
            entity.HasQueryFilter(u => TenantScope == null || u.TenantId == TenantScope);
        });

        modelBuilder.Entity<UserGroup>(entity =>
        {
            entity.ToTable("Groups");
            entity.Property(g => g.Name).HasMaxLength(MaxNameLength);
            entity.Property(g => g.ExternalName).HasMaxLength(200);
            entity.Property(g => g.BuiltIn).HasConversion<string>().HasMaxLength(30);
            entity.HasOne(g => g.Tenant).WithMany(t => t.Groups).HasForeignKey(g => g.TenantId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(g => new { g.TenantId, g.Name });
            entity.HasQueryFilter(g => TenantScope == null || g.TenantId == TenantScope);
        });

        modelBuilder.Entity<GroupMember>(entity =>
        {
            entity.HasKey(m => new { m.GroupId, m.UserId });
            entity.HasOne(m => m.Group).WithMany(g => g.Members).HasForeignKey(m => m.GroupId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(m => m.User).WithMany(u => u.Groups).HasForeignKey(m => m.UserId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(m => m.UserId);
            entity.HasQueryFilter(m => TenantScope == null || m.Group!.TenantId == TenantScope);
        });

        modelBuilder.Entity<GroupRight>(entity =>
        {
            entity.HasKey(r => new { r.GroupId, r.Right });
            entity.Property(r => r.Right).HasMaxLength(MaxNameLength);
            entity.HasOne(r => r.Group).WithMany(g => g.Rights).HasForeignKey(r => r.GroupId).OnDelete(DeleteBehavior.Cascade);
            entity.HasQueryFilter(r => TenantScope == null || r.Group!.TenantId == TenantScope);
        });

        modelBuilder.Entity<GroupLibrary>(entity =>
        {
            entity.HasKey(l => new { l.GroupId, l.LibraryId });
            entity.HasOne(l => l.Group).WithMany(g => g.Libraries).HasForeignKey(l => l.GroupId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(l => l.Library).WithMany().HasForeignKey(l => l.LibraryId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(l => l.LibraryId);
            entity.HasQueryFilter(l => TenantScope == null || l.Group!.TenantId == TenantScope);
        });

        modelBuilder.Entity<TenantDisabledRight>(entity =>
        {
            entity.HasKey(r => new { r.TenantId, r.Right });
            entity.Property(r => r.Right).HasMaxLength(MaxNameLength);
            entity.HasOne(r => r.Tenant).WithMany(t => t.DisabledRights).HasForeignKey(r => r.TenantId).OnDelete(DeleteBehavior.Cascade);
            entity.HasQueryFilter(r => TenantScope == null || r.TenantId == TenantScope);
        });

        modelBuilder.Entity<Setting>(entity =>
        {
            entity.HasKey(s => new { s.Level, s.OwnerId, s.Key });
            entity.Property(s => s.Level).HasConversion<string>().HasMaxLength(20);
            entity.Property(s => s.Key).HasMaxLength(MaxNameLength);
            entity.Property(s => s.Value).HasMaxLength(4000);
            entity.HasIndex(s => s.TenantId);
            entity.HasQueryFilter(s => TenantScope == null || s.TenantId == TenantScope);
        });

        modelBuilder.Entity<SecurityEvent>(entity =>
        {
            entity.Property(e => e.Kind).HasConversion<string>().HasMaxLength(30);
            entity.Property(e => e.UserName).HasMaxLength(SecurityEvent.MaxTextLength);
            entity.Property(e => e.Address).HasMaxLength(64);
            entity.Property(e => e.Detail).HasMaxLength(SecurityEvent.MaxTextLength);
            entity.HasIndex(e => new { e.TenantId, e.Id });
            entity.HasIndex(e => e.Time);
            entity.HasQueryFilter(e => TenantScope == null || e.TenantId == TenantScope);
        });
    }
}

/// <summary>The SQLite flavour of <see cref="LibrarionDbContext"/>: its migrations are in <c>Data/Migrations/Sqlite</c>.</summary>
public sealed class SqliteLibrarionDbContext(DbContextOptions<SqliteLibrarionDbContext> options) : LibrarionDbContext(options);

/// <summary>The PostgreSQL flavour of <see cref="LibrarionDbContext"/>: its migrations are in <c>Data/Migrations/Postgres</c>.</summary>
public sealed class PostgresLibrarionDbContext(DbContextOptions<PostgresLibrarionDbContext> options) : LibrarionDbContext(options);
