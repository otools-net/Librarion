using Microsoft.EntityFrameworkCore;

namespace Librarion.Core.Data;

/// <summary>
/// The model, shared by both providers. Each provider has its own derived context
/// (<see cref="SqliteLibrarionDbContext"/>, <see cref="PostgresLibrarionDbContext"/>) only so that each keeps its own
/// migrations; code asks for <see cref="TenantDb"/> (or <see cref="IDbContextFactory{LibrarionDbContext}"/>) and never
/// knows which one it got (agents/docs/development/database.md).
/// </summary>
public abstract partial class LibrarionDbContext(DbContextOptions options) : DbContext(options)
{
    public const int MaxNameLength = 100;

    /// <summary>
    /// The tenant this context works for: every tenant-owned set then shows that tenant's rows only (query filters,
    /// agents/docs/features/tenants.md). Null = the whole server, for server-wide code (<see cref="TenantDb.ForServerAsync"/>).
    /// Set before the context's first use (<see cref="TenantDb"/> does): it chooses the model.
    /// </summary>
    /// <remarks>
    /// Two models, one with the filters and one without (<see cref="ScopeModelCacheKeyFactory"/>), rather than one
    /// filter saying <c>TenantScope == null || TenantId == TenantScope</c>: EF sends that as <c>(@p OR "TenantId" = @s)</c>,
    /// which no index serves, and every big query then scanned and sorted the whole table (found by the stage 2
    /// benchmark at 20k books). The filters read <c>"TenantId" = @s</c> and the indexes starting with it work.
    /// </remarks>
    public int? TenantScope { get; set; }

    /// <summary>The scope's tenant as the filters read it (never null in a scoped model).</summary>
    public int CurrentTenantId => TenantScope ?? 0;

    private void Filter<TEntity>(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<TEntity> entity,
        System.Linq.Expressions.Expression<Func<TEntity, bool>> filter) where TEntity : class
    {
        if (TenantScope is not null)
            entity.HasQueryFilter(filter);
    }

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
    public DbSet<Book> Books => Set<Book>();
    public DbSet<BookFile> BookFiles => Set<BookFile>();
    public DbSet<Archive> Archives => Set<Archive>();
    public DbSet<BookIdentifier> BookIdentifiers => Set<BookIdentifier>();
    public DbSet<BookProperty> BookProperties => Set<BookProperty>();
    public DbSet<Tag> Tags => Set<Tag>();
    public DbSet<TagAlias> TagAliases => Set<TagAlias>();
    public DbSet<TagParent> TagParents => Set<TagParent>();
    public DbSet<TagClosure> TagClosure => Set<TagClosure>();
    public DbSet<BookTag> BookTags => Set<BookTag>();
    public DbSet<TagCount> TagCounts => Set<TagCount>();
    public DbSet<GroupTagFilter> GroupTagFilters => Set<GroupTagFilter>();
    public DbSet<ImportTagMapping> ImportTagMappings => Set<ImportTagMapping>();
    public DbSet<FieldKeyAlias> FieldKeyAliases => Set<FieldKeyAlias>();
    public DbSet<BookTagSuppression> BookTagSuppressions => Set<BookTagSuppression>();
    public DbSet<MergeLog> MergeLogs => Set<MergeLog>();
    public DbSet<MergeLogLink> MergeLogLinks => Set<MergeLogLink>();
    public DbSet<LibraryRoot> LibraryRoots => Set<LibraryRoot>();
    public DbSet<TaskRun> TaskRuns => Set<TaskRun>();
    public DbSet<TaskSchedule> TaskSchedules => Set<TaskSchedule>();

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
            Filter(entity, t => t.Id == CurrentTenantId);
        });

        modelBuilder.Entity<Library>(entity =>
        {
            entity.Property(l => l.Name).HasMaxLength(MaxNameLength);
            entity.HasOne(l => l.Tenant).WithMany(t => t.Libraries).HasForeignKey(l => l.TenantId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(l => new { l.TenantId, l.Name });
            Filter(entity, l => l.TenantId == CurrentTenantId);
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
            Filter(entity, u => u.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<UserGroup>(entity =>
        {
            entity.ToTable("Groups");
            entity.Property(g => g.Name).HasMaxLength(MaxNameLength);
            entity.Property(g => g.ExternalName).HasMaxLength(200);
            entity.Property(g => g.BuiltIn).HasConversion<string>().HasMaxLength(30);
            entity.HasOne(g => g.Tenant).WithMany(t => t.Groups).HasForeignKey(g => g.TenantId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(g => new { g.TenantId, g.Name });
            Filter(entity, g => g.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<GroupMember>(entity =>
        {
            entity.HasKey(m => new { m.GroupId, m.UserId });
            entity.HasOne(m => m.Group).WithMany(g => g.Members).HasForeignKey(m => m.GroupId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(m => m.User).WithMany(u => u.Groups).HasForeignKey(m => m.UserId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(m => m.UserId);
            Filter(entity, m => m.Group!.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<GroupRight>(entity =>
        {
            entity.HasKey(r => new { r.GroupId, r.Right });
            entity.Property(r => r.Right).HasMaxLength(MaxNameLength);
            entity.HasOne(r => r.Group).WithMany(g => g.Rights).HasForeignKey(r => r.GroupId).OnDelete(DeleteBehavior.Cascade);
            Filter(entity, r => r.Group!.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<GroupLibrary>(entity =>
        {
            entity.HasKey(l => new { l.GroupId, l.LibraryId });
            entity.HasOne(l => l.Group).WithMany(g => g.Libraries).HasForeignKey(l => l.GroupId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(l => l.Library).WithMany().HasForeignKey(l => l.LibraryId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(l => l.LibraryId);
            Filter(entity, l => l.Group!.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<TenantDisabledRight>(entity =>
        {
            entity.HasKey(r => new { r.TenantId, r.Right });
            entity.Property(r => r.Right).HasMaxLength(MaxNameLength);
            entity.HasOne(r => r.Tenant).WithMany(t => t.DisabledRights).HasForeignKey(r => r.TenantId).OnDelete(DeleteBehavior.Cascade);
            Filter(entity, r => r.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<Setting>(entity =>
        {
            entity.HasKey(s => new { s.Level, s.OwnerId, s.Key });
            entity.Property(s => s.Level).HasConversion<string>().HasMaxLength(20);
            entity.Property(s => s.Key).HasMaxLength(MaxNameLength);
            entity.Property(s => s.Value).HasMaxLength(4000);
            entity.HasIndex(s => s.TenantId);
            Filter(entity, s => s.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<SecurityEvent>(entity =>
        {
            entity.Property(e => e.Kind).HasConversion<string>().HasMaxLength(30);
            entity.Property(e => e.UserName).HasMaxLength(SecurityEvent.MaxTextLength);
            entity.Property(e => e.Address).HasMaxLength(64);
            entity.Property(e => e.Detail).HasMaxLength(SecurityEvent.MaxTextLength);
            entity.HasIndex(e => new { e.TenantId, e.Id });
            entity.HasIndex(e => e.Time);
            Filter(entity, e => e.TenantId == CurrentTenantId);
        });

        ConfigureCatalog(modelBuilder);
        ConfigureTags(modelBuilder);
    }
}

/// <summary>Builds one model for tenant contexts (with the query filters) and one for server contexts (without).</summary>
public sealed class ScopeModelCacheKeyFactory : Microsoft.EntityFrameworkCore.Infrastructure.IModelCacheKeyFactory
{
    public object Create(DbContext context, bool designTime) =>
        (context.GetType(), context is LibrarionDbContext { TenantScope: not null }, designTime);
}

/// <summary>The SQLite flavour of <see cref="LibrarionDbContext"/>: its migrations are in <c>Data/Migrations/Sqlite</c>.</summary>
public sealed class SqliteLibrarionDbContext(DbContextOptions<SqliteLibrarionDbContext> options) : LibrarionDbContext(options)
{
    /// <summary>
    /// Every time is stored in UTC, but SQLite keeps no zone: read back, a time is said to be UTC (else it went out as
    /// JSON without its "Z" and browsers read it as their own local time). PostgreSQL's <c>timestamptz</c> does this itself.
    /// </summary>
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
        configurationBuilder.Properties<DateTime?>().HaveConversion<UtcDateTimeConverter>();
    }

    private sealed class UtcDateTimeConverter() : Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTime, DateTime>(
        value => value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : value,
        value => DateTime.SpecifyKind(value, DateTimeKind.Utc));
}

/// <summary>The PostgreSQL flavour of <see cref="LibrarionDbContext"/>: its migrations are in <c>Data/Migrations/Postgres</c>.</summary>
public sealed class PostgresLibrarionDbContext(DbContextOptions<PostgresLibrarionDbContext> options) : LibrarionDbContext(options);
