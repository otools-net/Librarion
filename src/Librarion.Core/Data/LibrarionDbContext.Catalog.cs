using Microsoft.EntityFrameworkCore;

namespace Librarion.Core.Data;

/// <summary>
/// The catalog and the tags: the big tables (millions of books, tens of millions of links). Every list pages by key, so
/// each sort has an index ending in the id (agents/docs/development/scale.md); long strings are indexed through a hash
/// column (<see cref="BookFile.PathKey"/>). Every row carries its tenant for the query filters, so a filter never needs
/// a join.
/// </summary>
public abstract partial class LibrarionDbContext
{
    private void ConfigureCatalog(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Book>(entity =>
        {
            entity.Property(b => b.Title).HasMaxLength(Book.MaxTitleLength);
            entity.Property(b => b.SortTitle).HasMaxLength(Book.MaxSortLength);
            entity.Property(b => b.SortAuthor).HasMaxLength(Book.MaxSortLength);
            entity.HasOne(b => b.Library).WithMany().HasForeignKey(b => b.LibraryId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<Tenant>().WithMany().HasForeignKey(b => b.TenantId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(b => new { b.LibraryId, b.SortTitle, b.Id });
            entity.HasIndex(b => new { b.LibraryId, b.SortAuthor, b.Id });
            entity.HasIndex(b => new { b.LibraryId, b.Added, b.Id });
            // Every library of the tenant at once (most accounts see them all): the library ones can't give one order
            // over several libraries, and a small library inside a big tenant can't use these without scanning far.
            entity.HasIndex(b => new { b.TenantId, b.SortTitle, b.Id });
            entity.HasIndex(b => new { b.TenantId, b.SortAuthor, b.Id });
            entity.HasIndex(b => new { b.TenantId, b.Added, b.Id });
            entity.HasIndex(b => new { b.SeriesId, b.SeriesNumber, b.Id });
            entity.HasIndex(b => new { b.TenantId, b.WorkKey });
            // The missing books of a library, oldest first: only missing rows are in it (agents/docs/features/missing-books.md).
            entity.HasIndex(b => new { b.LibraryId, b.MissingSince, b.Id }).HasFilter("\"MissingSince\" IS NOT NULL");
            Filter(entity, b => b.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<BookFile>(entity =>
        {
            entity.Property(f => f.Path).HasMaxLength(BookFile.MaxPathLength);
            entity.Property(f => f.Format).HasMaxLength(20);
            entity.Property(f => f.ContentHash).HasMaxLength(32);
            entity.HasOne(f => f.Book).WithMany(b => b.Files).HasForeignKey(f => f.BookId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(f => f.Archive).WithMany().HasForeignKey(f => f.ArchiveId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(f => f.Root).WithMany().HasForeignKey(f => f.RootId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(f => f.BookId);
            // A scan looks its files up by path and sweeps the ones it didn't see by id, per root or per archive.
            entity.HasIndex(f => new { f.RootId, f.PathKey });
            entity.HasIndex(f => new { f.ArchiveId, f.PathKey });
            // "Same file" duplicates: candidates by size and CRC first (agents/docs/features/merging.md).
            entity.HasIndex(f => new { f.TenantId, f.Size, f.Crc32 });
            // Missing files of a library by size: what a moved file is matched against after a scan.
            entity.HasIndex(f => new { f.LibraryId, f.Size, f.Id }).HasFilter("\"MissingSince\" IS NOT NULL");
            Filter(entity, f => f.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<Archive>(entity =>
        {
            entity.Property(a => a.Path).HasMaxLength(BookFile.MaxPathLength);
            entity.HasOne(a => a.Library).WithMany().HasForeignKey(a => a.LibraryId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(a => a.Root).WithMany().HasForeignKey(a => a.RootId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(a => new { a.RootId, a.PathKey });
            Filter(entity, a => a.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<LibraryRoot>(entity =>
        {
            entity.Property(r => r.Path).HasMaxLength(LibraryRoot.MaxPathLength);
            entity.HasOne(r => r.Library).WithMany().HasForeignKey(r => r.LibraryId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(r => r.LibraryId);
            Filter(entity, r => r.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<TaskRun>(entity =>
        {
            entity.Property(r => r.Summary).HasMaxLength(TaskRun.MaxSummaryLength);
            entity.Property(r => r.Counts).HasMaxLength(TaskRun.MaxSummaryLength);
            entity.HasOne<Tenant>().WithMany().HasForeignKey(r => r.TenantId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(r => new { r.TenantId, r.Started, r.Id });
            entity.HasIndex(r => r.Outcome);
            Filter(entity, r => r.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<TaskSchedule>(entity =>
        {
            entity.HasOne(s => s.Library).WithMany().HasForeignKey(s => s.LibraryId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(s => s.LibraryId);
            entity.Property(s => s.ImportKind).HasMaxLength(20);
            entity.Property(s => s.Source).HasMaxLength(LibraryRoot.MaxPathLength);
            entity.Property(s => s.Files).HasMaxLength(LibraryRoot.MaxPathLength);
            Filter(entity, s => s.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<BookIdentifier>(entity =>
        {
            entity.HasKey(i => new { i.BookId, i.Scheme, i.Value });
            entity.Property(i => i.Scheme).HasMaxLength(20);
            entity.Property(i => i.Value).HasMaxLength(BookIdentifier.MaxValueLength);
            entity.HasOne(i => i.Book).WithMany(b => b.Identifiers).HasForeignKey(i => i.BookId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(i => new { i.TenantId, i.Scheme, i.Value });
            Filter(entity, i => i.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<BookProperty>(entity =>
        {
            entity.HasKey(p => new { p.BookId, p.Name });
            entity.Property(p => p.Name).HasMaxLength(BookProperty.MaxNameLength);
            entity.Property(p => p.Value).HasMaxLength(BookProperty.MaxValueLength);
            entity.HasOne(p => p.Book).WithMany(b => b.Properties).HasForeignKey(p => p.BookId).OnDelete(DeleteBehavior.Cascade);
            Filter(entity, p => p.TenantId == CurrentTenantId);
        });
    }

    private void ConfigureTags(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Tag>(entity =>
        {
            entity.Property(t => t.Name).HasMaxLength(Tag.MaxNameLength);
            entity.Property(t => t.NormalizedName).HasMaxLength(Tag.MaxNameLength);
            entity.Property(t => t.SortName).HasMaxLength(Tag.MaxNameLength);
            entity.Property(t => t.Shorthand).HasMaxLength(50);
            entity.Property(t => t.Color).HasMaxLength(20);
            entity.Property(t => t.FieldKey).HasMaxLength(Tag.MaxFieldKeyLength);
            entity.Property(t => t.BuiltIn).HasConversion<string>().HasMaxLength(20);
            entity.HasOne<Tenant>().WithMany().HasForeignKey(t => t.TenantId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(t => t.Field).WithMany().HasForeignKey(t => t.FieldId).OnDelete(DeleteBehavior.Cascade);
            // Lookups by name within a field (null: plain tags), and browse pages per field by sort name.
            entity.HasIndex(t => new { t.TenantId, t.FieldId, t.NormalizedName });
            entity.HasIndex(t => new { t.TenantId, t.FieldId, t.SortName, t.Id });
            entity.HasIndex(t => new { t.TenantId, t.FieldKey }).IsUnique();
            // The tag picker's shorthand match (TagPicker).
            entity.HasIndex(t => new { t.TenantId, t.Shorthand }).HasFilter("\"Shorthand\" IS NOT NULL");
            // The few hidden tags, found without reading 650k (search leaves their books out: agents/docs/features/search.md).
            entity.HasIndex(t => new { t.TenantId, t.IsHidden }).HasFilter("\"IsHidden\"");
            Filter(entity, t => t.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<TagAlias>(entity =>
        {
            entity.Property(a => a.Name).HasMaxLength(Tag.MaxNameLength);
            entity.Property(a => a.NormalizedName).HasMaxLength(Tag.MaxNameLength);
            entity.HasOne(a => a.Tag).WithMany(t => t.Aliases).HasForeignKey(a => a.TagId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(a => new { a.TenantId, a.NormalizedName });
            entity.HasIndex(a => a.TagId);
            Filter(entity, a => a.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<TagParent>(entity =>
        {
            entity.HasKey(p => new { p.ChildId, p.ParentId });
            entity.HasOne(p => p.Child).WithMany().HasForeignKey(p => p.ChildId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(p => p.Parent).WithMany().HasForeignKey(p => p.ParentId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(p => p.ParentId);
            Filter(entity, p => p.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<TagClosure>(entity =>
        {
            entity.ToTable("TagClosure");
            entity.HasKey(c => new { c.AncestorId, c.DescendantId });
            entity.HasOne(c => c.Ancestor).WithMany().HasForeignKey(c => c.AncestorId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(c => c.Descendant).WithMany().HasForeignKey(c => c.DescendantId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(c => new { c.DescendantId, c.AncestorId });
            Filter(entity, c => c.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<BookTag>(entity =>
        {
            entity.HasKey(l => new { l.BookId, l.TagId, l.Source });
            entity.HasOne(l => l.Book).WithMany(b => b.Tags).HasForeignKey(l => l.BookId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(l => l.Tag).WithMany().HasForeignKey(l => l.TagId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(l => new { l.TagId, l.BookId });
            // A series' books by number, walked in order whatever the series' size; only links with a number (small).
            entity.HasIndex(l => new { l.TagId, l.Number, l.BookId }).HasFilter("\"Number\" IS NOT NULL");
            Filter(entity, l => l.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<TagCount>(entity =>
        {
            entity.HasKey(c => new { c.LibraryId, c.TagId });
            entity.HasOne<Library>().WithMany().HasForeignKey(c => c.LibraryId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<Tag>().WithMany().HasForeignKey(c => c.TagId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(c => c.TagId);
            Filter(entity, c => c.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<ImportTagMapping>(entity =>
        {
            entity.Property(m => m.NormalizedName).HasMaxLength(Tag.MaxNameLength);
            entity.HasOne<Tenant>().WithMany().HasForeignKey(m => m.TenantId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(m => m.Tag).WithMany().HasForeignKey(m => m.TagId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<Tag>().WithMany().HasForeignKey(m => m.FieldId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(m => new { m.TenantId, m.Source, m.FieldId, m.NormalizedName });
            entity.HasIndex(m => m.TagId);
            Filter(entity, m => m.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<FieldKeyAlias>(entity =>
        {
            entity.Property(a => a.Key).HasMaxLength(Tag.MaxFieldKeyLength);
            entity.HasOne<Tenant>().WithMany().HasForeignKey(a => a.TenantId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(a => a.Field).WithMany().HasForeignKey(a => a.FieldId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(a => new { a.TenantId, a.Key }).IsUnique();
            entity.HasIndex(a => a.FieldId);
            Filter(entity, a => a.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<MergeLog>(entity =>
        {
            entity.Property(l => l.SourceName).HasMaxLength(Tag.MaxNameLength);
            entity.Property(l => l.TargetName).HasMaxLength(Tag.MaxNameLength);
            entity.HasOne<Tenant>().WithMany().HasForeignKey(l => l.TenantId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(l => new { l.TenantId, l.Id });
            entity.HasIndex(l => l.TargetId);
            // A merged tag's old address followed to the tag it went into (TagQueries.MovedAsync).
            entity.HasIndex(l => l.SourceId);
            entity.HasIndex(l => l.ParentId);
            Filter(entity, l => l.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<MergeLogLink>(entity =>
        {
            entity.HasKey(l => new { l.LogId, l.BookId, l.Source });
            entity.HasOne<MergeLog>().WithMany().HasForeignKey(l => l.LogId).OnDelete(DeleteBehavior.Cascade);
            Filter(entity, l => l.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<GroupTagFilter>(entity =>
        {
            entity.HasKey(f => new { f.GroupId, f.TagId });
            entity.HasOne(f => f.Group).WithMany(g => g.TagFilters).HasForeignKey(f => f.GroupId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(f => f.Tag).WithMany().HasForeignKey(f => f.TagId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(f => f.TagId);
            Filter(entity, f => f.Group!.TenantId == CurrentTenantId);
        });
    }
}
