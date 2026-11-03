using System.IO.Compression;
using Librarion.Core.Data;
using Librarion.Core.Import;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Librarion.Tests.Infrastructure;

/// <summary>A set-up server whose first library has one root, a temp folder, and its administrator's client.</summary>
internal sealed class Shelf : IAsyncDisposable
{
    private readonly TestCatalog catalog;

    private Shelf(TestCatalog catalog, WebApplicationFactory<Program> factory, HttpClient admin, string root, int tenantId, int libraryId)
    {
        this.catalog = catalog;
        Factory = factory;
        Admin = admin;
        Root = root;
        TenantId = tenantId;
        LibraryId = libraryId;
    }

    public WebApplicationFactory<Program> Factory { get; }
    public HttpClient Admin { get; }
    public string Root { get; }
    public int TenantId { get; }
    public int LibraryId { get; }

    public static async Task<Shelf> CreateAsync(CancellationToken cancellationToken)
    {
        var catalog = new TestCatalog();
        var factory = ApiClients.CreateFactory(catalog);
        var (admin, session) = await ApiClients.SetUpAsync(factory, cancellationToken);
        var tenantId = session.Tenant!.Id;
        var root = Path.Combine(catalog.BasePath, "books");
        Directory.CreateDirectory(root);
        await using var db = await factory.Services.GetRequiredService<TenantDb>().ForTenantAsync(tenantId, cancellationToken);
        var library = await db.Libraries.SingleAsync(cancellationToken);
        db.LibraryRoots.Add(new LibraryRoot { TenantId = tenantId, LibraryId = library.Id, Path = root, Created = DateTime.UtcNow });
        await db.SaveChangesAsync(cancellationToken);
        return new Shelf(catalog, factory, admin, root, tenantId, library.Id);
    }

    public Task<ScanResult> ScanAsync(CancellationToken cancellationToken) =>
        Factory.Services.GetRequiredService<LibraryScanner>().ScanAsync(TenantId, LibraryId, null, cancellationToken);

    public Task<LibrarionDbContext> DbAsync(CancellationToken cancellationToken) =>
        Factory.Services.GetRequiredService<TenantDb>().ForTenantAsync(TenantId, cancellationToken);

    /// <summary>The download address of the file at <paramref name="path"/> (relative to the root, or an entry name).</summary>
    public async Task<string> UrlOfAsync(string path, CancellationToken cancellationToken)
    {
        await using var db = await DbAsync(cancellationToken);
        var file = await db.BookFiles.SingleAsync(f => f.Path == path, cancellationToken);
        return $"/api/books/{file.BookId}/files/{file.Id}";
    }

    public async Task<HttpResponseMessage> GetAsync(string url, Action<HttpRequestMessage> change, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        change(request);
        return await Admin.SendAsync(request, cancellationToken);
    }

    public string Write(string relative, string text)
    {
        var path = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    public string Zip(string relative, CompressionLevel level, params (string Name, string? Text)[] entries)
    {
        var path = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.Delete(path);
        return TestZips.Zip(Path.GetDirectoryName(path)!, Path.GetFileName(path), level, entries);
    }

    public async ValueTask DisposeAsync()
    {
        Admin.Dispose();
        await Factory.DisposeAsync();
        catalog.Dispose();
    }
}
