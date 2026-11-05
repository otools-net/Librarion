using System.Data.Common;
using System.Linq.Expressions;
using System.Text;
using Librarion.Core.Data;
using Microsoft.EntityFrameworkCore;

// EF1002 (interpolated raw SQL): what is interpolated here are table, column, index and parameter names written
// in this file, which SQL cannot take as parameters; the words searched for always go as a parameter.
#pragma warning disable EF1002

namespace Librarion.Core.Search;

/// <summary>
/// Words to find (agents/docs/features/search.md): every one must be there; <see cref="Prefix"/> words also match
/// longer ones ("hobb" finds "hobbits"), a <see cref="Phrase"/> needs them in a row.
/// </summary>
/// <param name="Words">Folded (<see cref="SearchText.Words"/>).</param>
/// <param name="Plain">The same words lower-cased only, accents kept: tag names are indexed as they are spelled.</param>
public sealed record SearchMatch(IReadOnlyList<string> Words, IReadOnlyList<string> Plain, bool Phrase, bool Prefix);

/// <summary>
/// The database's own full-text search (D3): titles (<see cref="Book.SearchText"/>) and tag names, behind one
/// interface so another engine could replace it. Both give ids as a query to compose (never a list read whole);
/// what the caller may see is filtered by the outer query.
/// </summary>
public interface ISearchIndex
{
    /// <summary>Creates the index when missing (after the migrations: it is not part of the model) and fills it.</summary>
    Task EnsureAsync(LibrarionDbContext db, CancellationToken cancellationToken);

    /// <summary>Ids of books whose title (or file name) has the words.</summary>
    IQueryable<int> Books(LibrarionDbContext db, SearchMatch match);

    /// <summary>Ids of tags (field values and plain tags) whose name has the words.</summary>
    IQueryable<int> Tags(LibrarionDbContext db, SearchMatch match);

    /// <summary>A test of one book as the sort index is walked: its title (or file name) has the words.</summary>
    Expression<Func<Book, bool>> TitleTest(LibrarionDbContext db, SearchMatch match);
}

/// <summary>The index of the database in use.</summary>
public static class SearchIndexes
{
    public static ISearchIndex For(LibrarionDbContext db) => db.Database.IsNpgsql() ? PostgresSearchIndex.Instance : SqliteSearchIndex.Instance;

    private static int counter;

    /// <summary>A parameter with a name of its own: several searches composed in one query must not share one.</summary>
    internal static DbParameter Parameter(LibrarionDbContext db, string value)
    {
        using var command = db.Database.GetDbConnection().CreateCommand();
        var parameter = command.CreateParameter();
        parameter.ParameterName = $"search{Interlocked.Increment(ref counter) & 0xFFFFFF}";
        parameter.Value = value;
        return parameter;
    }
}

/// <summary>
/// SQLite: FTS5 tables over <c>Books.SearchText</c> and <c>Tags.NormalizedName</c> holding the index only
/// (external content), kept by triggers, so every write (EF or <see cref="BulkRows"/>) reaches them.
/// </summary>
internal sealed class SqliteSearchIndex : ISearchIndex
{
    public static readonly SqliteSearchIndex Instance = new();

    public async Task EnsureAsync(LibrarionDbContext db, CancellationToken cancellationToken)
    {
        await EnsureTableAsync(db, "BookSearch", "Books", "SearchText", cancellationToken);
        await EnsureTableAsync(db, "TagSearch", "Tags", "NormalizedName", cancellationToken);
    }

    private static async Task EnsureTableAsync(LibrarionDbContext db, string table, string content, string column, CancellationToken cancellationToken)
    {
        var exists = await db.Database.SqlQuery<int>($"SELECT count(*) AS \"Value\" FROM sqlite_master WHERE type = 'table' AND name = {table}")
            .SingleAsync(cancellationToken) > 0;
        // Accents and case are folded by the tokenizer too: tag names are indexed as spelled.
        await db.Database.ExecuteSqlRawAsync($"""
            CREATE VIRTUAL TABLE IF NOT EXISTS "{table}" USING fts5("{column}", content='{content}', content_rowid='Id', tokenize='unicode61 remove_diacritics 2');
            CREATE TRIGGER IF NOT EXISTS "{content}_{table}_Insert" AFTER INSERT ON "{content}" BEGIN
                INSERT INTO "{table}"(rowid, "{column}") VALUES (new."Id", new."{column}");
            END;
            CREATE TRIGGER IF NOT EXISTS "{content}_{table}_Delete" AFTER DELETE ON "{content}" BEGIN
                INSERT INTO "{table}"("{table}", rowid, "{column}") VALUES ('delete', old."Id", old."{column}");
            END;
            CREATE TRIGGER IF NOT EXISTS "{content}_{table}_Update" AFTER UPDATE OF "{column}" ON "{content}" BEGIN
                INSERT INTO "{table}"("{table}", rowid, "{column}") VALUES ('delete', old."Id", old."{column}");
                INSERT INTO "{table}"(rowid, "{column}") VALUES (new."Id", new."{column}");
            END;
            """, cancellationToken);
        // Rows written before the index existed (a database of an earlier build).
        if (!exists)
            await db.Database.ExecuteSqlRawAsync($"INSERT INTO \"{table}\"(\"{table}\") VALUES ('rebuild')", cancellationToken);
    }

    public IQueryable<int> Books(LibrarionDbContext db, SearchMatch match) =>
        Query(db, "BookSearch", Fts5Query(match.Words, match));

    public IQueryable<int> Tags(LibrarionDbContext db, SearchMatch match) =>
        Query(db, "TagSearch", Fts5Query(match.Words, match));

    // The index's matches read once into a list (FTS5 hands compact ids), each book looked up in it. "+ 0": the id no
    // longer matches the key, so SQLite tests the book walked instead of starting from the matches.
    public Expression<Func<Book, bool>> TitleTest(LibrarionDbContext db, SearchMatch match)
    {
        var titles = Books(db, match);
        return b => titles.Contains(b.Id + 0);
    }

    private static IQueryable<int> Query(LibrarionDbContext db, string table, string expression)
    {
        var parameter = SearchIndexes.Parameter(db, expression);
        return db.Database.SqlQueryRaw<int>($"SELECT rowid AS \"Value\" FROM \"{table}\" WHERE \"{table}\" MATCH @{parameter.ParameterName}", parameter);
    }

    /// <summary>An FTS5 query: <c>"hobbit" AND "ba"*</c>, or <c>"the dark tower"*</c> for a phrase. Words are letters and digits only.</summary>
    private static string Fts5Query(IReadOnlyList<string> words, SearchMatch match)
    {
        if (match.Phrase)
            return $"\"{string.Join(' ', words)}\"{(match.Prefix && words[^1].Length > 1 ? "*" : "")}";
        return string.Join(" AND ", words.Select(w => $"\"{w}\"{(match.Prefix && w.Length > 1 ? "*" : "")}"));
    }
}

/// <summary>
/// PostgreSQL: GIN indexes over the <c>simple</c> configuration's vectors (no stemming: the libraries are in every
/// language), queried with the very expression indexed. Tag names keep their accents here (SQLite folds them):
/// each word is asked as spelled and folded.
/// </summary>
internal sealed class PostgresSearchIndex : ISearchIndex
{
    public static readonly PostgresSearchIndex Instance = new();

    private const string BookVector = "to_tsvector('simple'::regconfig, \"SearchText\")";
    private const string TagVector = "to_tsvector('simple'::regconfig, regexp_replace(\"NormalizedName\", '[^[:alnum:]]+', ' ', 'g'))";

    public async Task EnsureAsync(LibrarionDbContext db, CancellationToken cancellationToken) =>
        await db.Database.ExecuteSqlRawAsync($"""
            CREATE INDEX IF NOT EXISTS "IX_Books_SearchText_Words" ON "Books" USING gin ({BookVector});
            CREATE INDEX IF NOT EXISTS "IX_Tags_NormalizedName_Words" ON "Tags" USING gin ({TagVector});
            """, cancellationToken);

    public IQueryable<int> Books(LibrarionDbContext db, SearchMatch match) =>
        Query(db, "Books", BookVector, TsQuery(match.Words, match.Words, match));

    public IQueryable<int> Tags(LibrarionDbContext db, SearchMatch match) =>
        Query(db, "Tags", TagVector, TsQuery(match.Words, match.Plain, match));

    // The book walked tested itself: its title's words (a few microseconds). Reading every match first (a hash of 120k
    // titles from the heap) took most of 1.7 s for a common word.
    public Expression<Func<Book, bool>> TitleTest(LibrarionDbContext db, SearchMatch match)
    {
        var query = TsQuery(match.Words, match.Words, match);
        return b => EF.Functions.ToTsVector("simple", b.SearchText).Matches(EF.Functions.ToTsQuery("simple", query));
    }

    private static IQueryable<int> Query(LibrarionDbContext db, string table, string vector, string expression)
    {
        var parameter = SearchIndexes.Parameter(db, expression);
        return db.Database.SqlQueryRaw<int>(
            $"SELECT \"Id\" AS \"Value\" FROM \"{table}\" WHERE {vector} @@ to_tsquery('simple'::regconfig, @{parameter.ParameterName})", parameter);
    }

    /// <summary>A tsquery: <c>hobbit &amp; ba:*</c>, a phrase with <c>&lt;-&gt;</c>; a word spelled two ways as <c>(jose:* | josé:*)</c>.</summary>
    private static string TsQuery(IReadOnlyList<string> folded, IReadOnlyList<string> plain, SearchMatch match)
    {
        var parts = new List<string>();
        for (var i = 0; i < folded.Count; i++)
        {
            var prefix = match.Prefix && (!match.Phrase || i == folded.Count - 1);
            var spellings = new[] { folded[i], i < plain.Count ? plain[i] : folded[i] }.Distinct().Select(w => Lexeme(w, prefix)).ToList();
            parts.Add(spellings.Count == 1 ? spellings[0] : $"({string.Join(" | ", spellings)})");
        }

        return string.Join(match.Phrase ? " <-> " : " & ", parts);
    }

    /// <summary>A quoted lexeme: the words are letters and digits, the quotes keep the parser from reading anything into them.</summary>
    private static string Lexeme(string word, bool prefix)
    {
        var text = new StringBuilder("'").Append(word.Replace("'", "''", StringComparison.Ordinal)).Append('\'');
        if (prefix && word.Length > 1)
            text.Append(":*");
        return text.ToString();
    }
}
