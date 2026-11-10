using System.Text;
using System.Xml;
using Librarion.Api.Security;
using Librarion.Core.Access;
using Microsoft.AspNetCore.Authorization;

namespace Librarion.Api.Opds;

/// <summary>
/// The OPDS catalog for reader apps under <c>/opds</c> (agents/docs/features/opds.md): GET only, signed in by
/// <see cref="OpdsAuthenticationHandler"/> (never the cookie), each route naming its right; anything else under
/// <c>/opds</c> is 404, never the client's page.
/// </summary>
public static class OpdsEndpoints
{
    public const string Prefix = "/opds";
    public const string AtomNamespace = "http://www.w3.org/2005/Atom";
    public const string NavigationType = "application/atom+xml;profile=opds-catalog;kind=navigation";

    public static void MapOpdsEndpoints(this IEndpointRouteBuilder app)
    {
        var opds = app.MapGroup(Prefix)
            .RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = OpdsAuthenticationHandler.Scheme })
            .ExcludeFromDescription();

        opds.MapGet("", Root).RequireRight(Rights.Opds);
        // Every other address, file-like ones too (opensearch.xml): not found, before the client's fallback takes it.
        opds.MapFallback("{**path}", () => TypedResults.NotFound()).AllowAnonymous();
    }

    /// <summary>The root feed; a placeholder until the feeds are built (stage 7 chunk 5).</summary>
    private static IResult Root(HttpContext context)
    {
        var self = context.Request.PathBase + Prefix;
        var text = new StringBuilder();
        using (var xml = XmlWriter.Create(text, new XmlWriterSettings { OmitXmlDeclaration = true }))
        {
            xml.WriteStartElement("feed", AtomNamespace);
            xml.WriteElementString("id", AtomNamespace, "urn:librarion:root");
            xml.WriteElementString("title", AtomNamespace, "Librarion");
            xml.WriteElementString("updated", AtomNamespace, XmlConvert.ToString(DateTimeOffset.UtcNow));
            xml.WriteStartElement("author", AtomNamespace);
            xml.WriteElementString("name", AtomNamespace, "Librarion");
            xml.WriteEndElement();
            foreach (var rel in new[] { "self", "start" })
            {
                xml.WriteStartElement("link", AtomNamespace);
                xml.WriteAttributeString("rel", rel);
                xml.WriteAttributeString("href", self);
                xml.WriteAttributeString("type", NavigationType);
                xml.WriteEndElement();
            }
            xml.WriteEndElement();
        }
        return TypedResults.Text("<?xml version=\"1.0\" encoding=\"utf-8\"?>" + text, NavigationType, Encoding.UTF8);
    }
}
