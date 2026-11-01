namespace Librarion.Api.Security;

/// <summary>
/// The rights a route needs (any one of them), read by <c>EveryRouteNamesARight</c>: every <c>/api</c> and
/// <c>/openapi</c> route carries this or <c>.AllowAnonymous()</c>. The rights catalog and the
/// <c>RequireRight(...)</c> extension that adds it come in stage 1 (agents/docs/features/rights.md).
/// </summary>
public sealed record RightMetadata(IReadOnlyList<string> AnyOf);
