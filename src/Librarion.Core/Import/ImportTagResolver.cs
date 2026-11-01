using Librarion.Core.Data;
using Librarion.Core.Tagging;
using Microsoft.EntityFrameworkCore;

namespace Librarion.Core.Import;

/// <summary>
/// Turns the names a book gives (authors, series, categories, plain tags) into tag ids, a batch of books at a time
/// (agents/docs/features/tags.md#imported-tags, metadata-tags.md#import): the source's mapping first, then a tag of
/// that name or alias within the field, else a new tag (a field value under its field; a plain tag under the source's
/// "Imported: …" category). One query per kind and field for the whole batch, never one per book; a bounded cache
/// keeps the names seen lately (most books of an archive share their authors and genres).
/// </summary>
public sealed class ImportTagResolver
{
    private const int MaxCached = 200_000;
    private static readonly HashSet<string> PersonFields = ["author", "translator", "editor", "illustrator"];

    private readonly int tenantId;
    private readonly Dictionary<string, int> fields;
    private readonly Dictionary<(ImportTagSourceKind Source, int? Field, string Name), int?> cache = [];
    private readonly Dictionary<ImportTagSourceKind, int> categories = [];

    private ImportTagResolver(int tenantId, Dictionary<string, int> fields)
    {
        this.tenantId = tenantId;
        this.fields = fields;
    }

    public static async Task<ImportTagResolver> CreateAsync(LibrarionDbContext db, int tenantId, CancellationToken cancellationToken)
    {
        var fields = await db.Tags.Where(t => t.FieldKey != null).ToDictionaryAsync(t => t.FieldKey!, t => t.Id, cancellationToken);
        return new ImportTagResolver(tenantId, fields);
    }

    /// <summary>A name to resolve: <paramref name="Field"/> null for a plain tag.</summary>
    public sealed record Name(ImportTagSourceKind Source, string? Field, string Value, string? SortName = null);

    /// <summary>The tag a resolved name became; null when it is ignored, empty, or wasn't resolved.</summary>
    public int? Get(Name name) => Key(name) is { } key && cache.TryGetValue(key, out var id) ? id : null;

    /// <summary>The id of a field by key (a new key becomes a field after <see cref="ResolveAsync"/>).</summary>
    public int? FieldId(string key) => fields.TryGetValue(key, out var id) ? id : null;

    /// <summary>Resolves <paramref name="names"/> (creating what is new), so that <see cref="Get"/> answers for each.</summary>
    public async Task ResolveAsync(LibrarionDbContext db, IEnumerable<Name> names, CancellationToken cancellationToken)
    {
        if (cache.Count > MaxCached)
            cache.Clear();
        var list = names.ToList();
        await AddFieldsAsync(db, list.Select(n => n.Field).OfType<string>().Distinct().Where(k => !fields.ContainsKey(k)), cancellationToken);

        var pending = new Dictionary<(ImportTagSourceKind Source, int? Field, string Name), Name>();
        foreach (var name in list)
            if (Key(name) is { } key && !cache.ContainsKey(key))
                pending.TryAdd(key, name);
        if (pending.Count == 0)
            return;

        foreach (var group in pending.Keys.GroupBy(k => (k.Source, k.Field)))
        {
            var normalized = group.Select(k => k.Name).ToList();
            var (source, field) = group.Key;
            var mappings = field is { } fieldId
                ? db.ImportTagMappings.Where(m => m.Source == source && m.FieldId == fieldId && normalized.Contains(m.NormalizedName))
                : db.ImportTagMappings.Where(m => m.Source == source && m.FieldId == null && normalized.Contains(m.NormalizedName));
            foreach (var mapping in await mappings.Select(m => new { m.NormalizedName, m.TagId, m.Ignored }).ToListAsync(cancellationToken))
                Settle((source, field, mapping.NormalizedName), mapping.Ignored ? null : mapping.TagId, pending);
        }

        foreach (var group in pending.Keys.GroupBy(k => k.Field).ToList())
        {
            var normalized = group.Select(k => k.Name).Distinct().ToList();
            var field = group.Key;
            var tags = field is { } fieldId
                ? db.Tags.Where(t => t.FieldId == fieldId && normalized.Contains(t.NormalizedName))
                : db.Tags.Where(t => t.FieldId == null && normalized.Contains(t.NormalizedName));
            var byName = (await tags.Select(t => new { t.NormalizedName, t.Id }).ToListAsync(cancellationToken))
                .GroupBy(t => t.NormalizedName).ToList();
            foreach (var found in byName)
            {
                // A field value: the oldest of its name (a person told apart by a disambiguation is curated by hand).
                // A plain tag: only a unique match, as SFE.
                if (field is null && found.Count() > 1)
                    continue;
                foreach (var key in group.Where(k => k.Name == found.Key))
                    Settle(key, found.Min(t => t.Id), pending);
            }

            var left = group.Where(pending.ContainsKey).Select(k => k.Name).Distinct().ToList();
            if (left.Count == 0)
                continue;
            var aliases = (await db.TagAliases.Where(a => left.Contains(a.NormalizedName))
                    .Select(a => new { a.NormalizedName, a.TagId, a.Tag!.FieldId }).ToListAsync(cancellationToken))
                .Where(a => a.FieldId == field).GroupBy(a => a.NormalizedName).Where(a => a.Count() == 1);
            foreach (var alias in aliases)
                foreach (var key in group.Where(k => k.Name == alias.Key))
                    Settle(key, alias.Single().TagId, pending);
        }

        await CreateAsync(db, pending, cancellationToken);
    }

    private (ImportTagSourceKind, int?, string)? Key(Name name)
    {
        var normalized = TagNames.Normalize(name.Value);
        if (normalized.Length == 0)
            return null;
        if (normalized.Length > Tag.MaxNameLength)
            normalized = normalized[..Tag.MaxNameLength];
        int? field = null;
        if (name.Field is { } key)
        {
            if (!fields.TryGetValue(key, out var id))
                return null;
            field = id;
        }

        return (name.Source, field, normalized);
    }

    private void Settle((ImportTagSourceKind, int?, string) key, int? tagId, Dictionary<(ImportTagSourceKind, int?, string), Name> pending)
    {
        if (pending.Remove(key))
            cache[key] = tagId;
    }

    /// <summary>New tags for what nothing matched: one per (field, name), whatever the sources that gave it.</summary>
    private async Task CreateAsync(LibrarionDbContext db, Dictionary<(ImportTagSourceKind Source, int? Field, string Name), Name> pending, CancellationToken cancellationToken)
    {
        if (pending.Count == 0)
            return;
        var created = new Dictionary<(int? Field, string Name), Tag>();
        foreach (var ((source, field, normalized), name) in pending)
        {
            if (created.ContainsKey((field, normalized)))
                continue;
            var display = Cut(name.Value.Trim());
            var person = name.Field is { } key && PersonFields.Contains(key);
            var tag = new Tag
            {
                TenantId = tenantId,
                Name = display,
                NormalizedName = normalized,
                SortName = Cut(name.SortName?.Trim() is { Length: > 0 } sort ? sort : person ? TagNames.PersonSortName(display) : display),
                FieldId = field,
            };
            db.Tags.Add(tag);
            db.TagClosure.Add(new TagClosure { TenantId = tenantId, Ancestor = tag, Descendant = tag });
            if (field is { } fieldId)
                db.TagClosure.Add(new TagClosure { TenantId = tenantId, AncestorId = fieldId, Descendant = tag, Depth = 1 });
            else
            {
                var category = await CategoryAsync(db, source, cancellationToken);
                db.TagParents.Add(new TagParent { TenantId = tenantId, Child = tag, ParentId = category });
                db.TagClosure.Add(new TagClosure { TenantId = tenantId, AncestorId = category, Descendant = tag, Depth = 1 });
            }

            created[(field, normalized)] = tag;
        }

        await db.SaveChangesAsync(cancellationToken);
        foreach (var key in pending.Keys)
            cache[key] = created[(key.Field, key.Name)].Id;
    }

    /// <summary>The category new plain tags of a source go under ("Imported: FB2"), made on first need.</summary>
    private async Task<int> CategoryAsync(LibrarionDbContext db, ImportTagSourceKind source, CancellationToken cancellationToken)
    {
        if (categories.TryGetValue(source, out var id))
            return id;
        var name = $"Imported: {source switch { ImportTagSourceKind.Fb2 => "FB2", ImportTagSourceKind.Epub => "EPUB", ImportTagSourceKind.Mobi => "MOBI", ImportTagSourceKind.Pdf => "PDF", _ => source.ToString() }}";
        var normalized = TagNames.Normalize(name);
        id = await db.Tags.Where(t => t.FieldId == null && t.IsCategory && t.NormalizedName == normalized).Select(t => t.Id).FirstOrDefaultAsync(cancellationToken);
        if (id == 0)
        {
            var category = new Tag { TenantId = tenantId, Name = name, NormalizedName = normalized, SortName = name, IsCategory = true };
            db.Tags.Add(category);
            db.TagClosure.Add(new TagClosure { TenantId = tenantId, Ancestor = category, Descendant = category });
            await db.SaveChangesAsync(cancellationToken);
            id = category.Id;
        }

        categories[source] = id;
        return id;
    }

    /// <summary>Fields a book names that the tenant doesn't have yet (FB2 <c>custom-info</c>, Calibre columns) become fields.</summary>
    private async Task AddFieldsAsync(LibrarionDbContext db, IEnumerable<string> keys, CancellationToken cancellationToken)
    {
        var made = new List<Tag>();
        foreach (var key in keys)
        {
            if (key.Length is 0 or > Tag.MaxFieldKeyLength || !key.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-'))
                continue;
            var field = new Tag { TenantId = tenantId, Name = key, NormalizedName = TagNames.Normalize(key), SortName = key, IsCategory = true, FieldKey = key };
            db.Tags.Add(field);
            db.TagClosure.Add(new TagClosure { TenantId = tenantId, Ancestor = field, Descendant = field });
            made.Add(field);
        }

        if (made.Count == 0)
            return;
        await db.SaveChangesAsync(cancellationToken);
        foreach (var field in made)
            fields[field.FieldKey!] = field.Id;
    }

    private static string Cut(string text) => text.Length > Tag.MaxNameLength ? text[..Tag.MaxNameLength] : text;
}
