namespace Librarion.Core.Data;

/// <summary>A library of a tenant: its books, and which groups reach it. Its folders and scans come in stage 3.</summary>
public sealed class Library
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public Tenant? Tenant { get; set; }

    public string Name { get; set; } = string.Empty;

    public DateTime Created { get; set; }

    /// <summary>Books in it (not merged away), kept by imports: never counted per request.</summary>
    public int BookCount { get; set; }
}
