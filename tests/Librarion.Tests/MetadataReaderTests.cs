using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Librarion.Core.Data;
using Librarion.Core.Import;
using Librarion.Core.Import.Metadata;

namespace Librarion.Tests;

/// <summary>
/// Metadata from the heads of books (<see cref="MetadataReaders"/>, agents/docs/features/metadata-tags.md): each
/// format's fields, read no further than needed, and the hostile XML a stranger writes.
/// </summary>
[TestClass]
public sealed class MetadataReaderTests
{
    private static readonly MetadataReaders Readers = new();

    static MetadataReaderTests() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    private const string Fb2 = """
        <?xml version="1.0" encoding="windows-1251"?>
        <FictionBook xmlns="http://www.gribuser.ru/xml/fictionbook/2.0" xmlns:l="http://www.w3.org/1999/xlink">
          <description>
            <title-info>
              <genre>sf_fantasy</genre><genre>made_up_code</genre>
              <author><first-name>Аркадий</first-name><middle-name>Натанович</middle-name><last-name>Стругацкий</last-name></author>
              <author><first-name>Борис</first-name><last-name>Стругацкий</last-name></author>
              <author><nickname>Anonymous</nickname></author>
              <book-title>Понедельник начинается в субботу</book-title>
              <annotation><p>Первый абзац.</p><p>Второй   абзац.</p></annotation>
              <keywords>magic, science; humor</keywords>
              <date value="1965-01-01">1965</date>
              <coverpage><image l:href="#cover.jpg"/></coverpage>
              <lang>ru</lang>
              <translator><first-name>Leonid</first-name><last-name>Renen</last-name></translator>
              <sequence name="НИИЧАВО" number="1"/>
            </title-info>
            <src-title-info><book-title>Not this one</book-title></src-title-info>
            <document-info><author><nickname>Scanner</nickname><id>maker-of-files</id></author><id>8f5c-41d2</id><publisher><id>publisher-of-files</id></publisher></document-info>
            <publish-info><publisher>Детская литература</publisher><year>1965</year><isbn>978-5-17-090334-4</isbn></publish-info>
          </description>
          <body><section><p>Text that is never read.</p></section></body>
          <binary id="cover.jpg" content-type="image/jpeg">AAAA</binary>
        </FictionBook>
        """;

    [TestMethod]
    public void AnFb2DescriptionGivesEveryFieldInItsCodePage()
    {
        var bytes = Encoding.GetEncoding(1251).GetBytes(Fb2);
        var metadata = Read("fb2", bytes)!;

        Assert.AreEqual(ImportTagSourceKind.Fb2, metadata.Source);
        Assert.AreEqual("Понедельник начинается в субботу", metadata.Title);
        Assert.AreEqual("Первый абзац.\n\nВторой абзац.", metadata.Description);
        var authors = metadata.FieldValues.Where(v => v.Field == "author").ToList();
        CollectionAssert.AreEqual(new[] { "Аркадий Натанович Стругацкий", "Борис Стругацкий", "Anonymous" }, authors.Select(a => a.Value).ToList());
        Assert.AreEqual("Стругацкий, Аркадий Натанович", authors[0].SortName);
        CollectionAssert.AreEqual(new int?[] { 0, 1, 2 }, authors.Select(a => a.Ordinal).ToList());
        CollectionAssert.AreEqual(new[] { "Fantasy", "made_up_code" }, Values(metadata, "category"));
        CollectionAssert.AreEqual(new[] { "Leonid Renen" }, Values(metadata, "translator"));
        var series = metadata.FieldValues.Single(v => v.Field == "series");
        Assert.AreEqual(("НИИЧАВО", 1.0), (series.Value, series.Number));
        CollectionAssert.AreEqual(new[] { "ru" }, Values(metadata, "language"));
        CollectionAssert.AreEqual(new[] { "1965" }, Values(metadata, "year"));
        CollectionAssert.AreEqual(new[] { "Детская литература" }, Values(metadata, "publisher"));
        CollectionAssert.AreEqual(new[] { "magic", "science", "humor" }, metadata.PlainTags.ToList());
        CollectionAssert.AreEquivalent(new[] { ("fb2-id", "8f5c-41d2"), ("isbn", "9785170903344") }, metadata.Ids.ToList(),
            "the document's id, not the ids of the file maker or publisher inside <document-info>");
    }

    [TestMethod]
    public void AnFb2IsReadNoFurtherThanItsDescription()
    {
        var head = Encoding.UTF8.GetBytes(Fb2.Replace("windows-1251", "utf-8"));
        var huge = new GuardedStream(head, readableBeyond: 64 * 1024);
        Assert.IsNotNull(Fb2Reader.Read(huge));
        Assert.IsFalse(huge.ReadPastGuard, "the text and the covers after </description> aren't read");
    }

    [TestMethod]
    public void EntitiesAndDtdsInXmlAreNotExpanded()
    {
        var bomb = """
            <?xml version="1.0"?>
            <!DOCTYPE lolz [<!ENTITY lol "lol"><!ENTITY lol2 "&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;"><!ENTITY lol3 "&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;">
            <!ENTITY ext SYSTEM "file:///etc/passwd">]>
            <FictionBook><description><title-info><book-title>&lol3;&ext;</book-title></title-info></description></FictionBook>
            """;
        try
        {
            var metadata = Read("fb2", Encoding.UTF8.GetBytes(bomb));
            Assert.IsTrue(metadata?.Title is null or { Length: < 100 }, "nothing expanded");
        }
        catch (System.Xml.XmlException)
        {
            // Refused outright: as good.
        }
    }

    [TestMethod]
    public void AnEpub2PackageGivesCreatorsByRoleAndCalibresSeries()
    {
        var opf = """
            <?xml version="1.0" encoding="utf-8"?>
            <package xmlns="http://www.idpf.org/2007/opf" version="2.0" unique-identifier="id">
              <metadata xmlns:dc="http://purl.org/dc/elements/1.1/" xmlns:opf="http://www.idpf.org/2007/opf">
                <dc:title>The Talisman</dc:title>
                <dc:creator opf:role="aut" opf:file-as="King, Stephen">Stephen King</dc:creator>
                <dc:creator opf:role="aut">Peter Straub</dc:creator>
                <dc:contributor opf:role="trl">Someone Else</dc:contributor>
                <dc:contributor opf:role="bkp">calibre</dc:contributor>
                <dc:subject>Fantasy</dc:subject><dc:subject>Horror</dc:subject>
                <dc:language>en-US</dc:language>
                <dc:publisher>Viking</dc:publisher>
                <dc:date>1984-10-08T00:00:00+00:00</dc:date>
                <dc:description>&lt;p&gt;Jack &amp;amp; the Territories.&lt;/p&gt;</dc:description>
                <dc:identifier id="id" opf:scheme="uuid">urn:uuid:0D8F3C5E-1111-2222-3333-444455556666</dc:identifier>
                <dc:identifier opf:scheme="ISBN">0-670-69199-2</dc:identifier>
                <meta name="calibre:series" content="The Talisman"/>
                <meta name="calibre:series_index" content="1.0"/>
              </metadata>
            </package>
            """;
        var metadata = Read("epub", Epub(opf), seekable: false)!;

        Assert.AreEqual("The Talisman", metadata.Title);
        Assert.AreEqual("Jack & the Territories.", metadata.Description);
        var authors = metadata.FieldValues.Where(v => v.Field == "author").ToList();
        CollectionAssert.AreEqual(new[] { "Stephen King", "Peter Straub" }, authors.Select(a => a.Value).ToList());
        Assert.AreEqual("King, Stephen", authors[0].SortName);
        CollectionAssert.AreEqual(new[] { "Someone Else" }, Values(metadata, "translator"), "a book producer is no field");
        CollectionAssert.AreEqual(new[] { "Fantasy", "Horror" }, Values(metadata, "category"));
        CollectionAssert.AreEqual(new[] { "en" }, Values(metadata, "language"));
        CollectionAssert.AreEqual(new[] { "1984" }, Values(metadata, "year"));
        Assert.AreEqual(1.0, metadata.FieldValues.Single(v => v.Field == "series").Number);
        CollectionAssert.AreEquivalent(new[] { ("uuid", "0d8f3c5e-1111-2222-3333-444455556666"), ("isbn", "0670691992") }, metadata.Ids.ToList());
    }

    [TestMethod]
    public void AnEpub3PackageGivesRefinedRolesAndItsCollection()
    {
        var opf = """
            <package xmlns="http://www.idpf.org/2007/opf" version="3.0">
              <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
                <dc:title>Dune Messiah</dc:title>
                <dc:creator id="c1">Frank Herbert</dc:creator>
                <meta refines="#c1" property="role" scheme="marc:relators">aut</meta>
                <meta refines="#c1" property="file-as">Herbert, Frank</meta>
                <dc:creator id="c2">An Illustrator</dc:creator>
                <meta refines="#c2" property="role">ill</meta>
                <meta property="belongs-to-collection" id="s1">Dune</meta>
                <meta refines="#s1" property="collection-type">series</meta>
                <meta refines="#s1" property="group-position">2</meta>
                <dc:language>eng</dc:language>
              </metadata>
            </package>
            """;
        var metadata = Read("epub", Epub(opf))!;

        var author = metadata.FieldValues.Single(v => v.Field == "author");
        Assert.AreEqual(("Frank Herbert", "Herbert, Frank"), (author.Value, author.SortName));
        CollectionAssert.AreEqual(new[] { "An Illustrator" }, Values(metadata, "illustrator"));
        var series = metadata.FieldValues.Single(v => v.Field == "series");
        Assert.AreEqual(("Dune", 2.0), (series.Value, series.Number));
        CollectionAssert.AreEqual(new[] { "en" }, Values(metadata, "language"));
    }

    [TestMethod]
    public void ACbzGivesItsComicInfo()
    {
        var info = """
            <ComicInfo><Title>The Long Halloween</Title><Series>Batman</Series><Number>3</Number><Year>1997</Year>
            <Writer>Jeph Loeb</Writer><Penciller>Tim Sale</Penciller><Publisher>DC</Publisher><Genre>Superhero, Crime</Genre>
            <LanguageISO>en</LanguageISO><Summary>Holiday killer.</Summary></ComicInfo>
            """;
        var metadata = Read("cbz", ZipOf(("001.jpg", "x"), ("ComicInfo.xml", info)))!;

        Assert.AreEqual("The Long Halloween", metadata.Title);
        Assert.AreEqual("Holiday killer.", metadata.Description);
        CollectionAssert.AreEqual(new[] { "Jeph Loeb" }, Values(metadata, "author"));
        CollectionAssert.AreEqual(new[] { "Tim Sale" }, Values(metadata, "illustrator"));
        CollectionAssert.AreEqual(new[] { "Superhero", "Crime" }, Values(metadata, "category"));
        Assert.AreEqual(3.0, metadata.FieldValues.Single(v => v.Field == "series").Number);
    }

    [TestMethod]
    public void AMobiGivesItsFullNameAndExth()
    {
        var metadata = Read("azw3", Mobi("The Hobbit, or There and Back Again",
            (100, "J. R. R. Tolkien"), (101, "Allen & Unwin"), (104, "978-0-261-10221-7"), (105, "Fantasy"), (106, "1937-09-21"), (524, "en")))!;

        Assert.AreEqual("The Hobbit, or There and Back Again", metadata.Title);
        CollectionAssert.AreEqual(new[] { "J. R. R. Tolkien" }, Values(metadata, "author"));
        CollectionAssert.AreEqual(new[] { "Allen & Unwin" }, Values(metadata, "publisher"));
        CollectionAssert.AreEqual(new[] { "1937" }, Values(metadata, "year"));
        CollectionAssert.AreEqual(new[] { ("isbn", "9780261102217") }, metadata.Ids.ToList());
        Assert.IsNull(Read("mobi", Encoding.ASCII.GetBytes(new string('x', 200))), "not a MOBI");
    }

    [TestMethod]
    public void APdfGivesItsInformationDictionaryOrItsXmp()
    {
        var info = Pdf("1 0 obj\n<< /Type /Catalog >>\nendobj\n12 0 obj\n<< /Title (Gödel, Escher, Bach \\(GEB\\)) /Author <FEFF0044006F00750067> /Keywords (logic, music) /CreationDate (D:2001) >>\nendobj\n",
            "trailer\n<< /Root 1 0 R /Info 12 0 R >>\n");
        var metadata = Read("pdf", info)!;
        Assert.AreEqual("Gödel, Escher, Bach (GEB)", metadata.Title);
        CollectionAssert.AreEqual(new[] { "Doug" }, Values(metadata, "author"), "UTF-16 hex string");
        CollectionAssert.AreEqual(new[] { "logic", "music" }, Values(metadata, "category"));

        var xmp = Pdf("""
            1 0 obj
            << /Type /Metadata /Subtype /XML >>
            stream
            <x:xmpmeta xmlns:x="adobe:ns:meta/"><rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
            <rdf:Description xmlns:dc="http://purl.org/dc/elements/1.1/">
            <dc:title><rdf:Alt><rdf:li xml:lang="x-default">Structure and Interpretation</rdf:li></rdf:Alt></dc:title>
            <dc:creator><rdf:Seq><rdf:li>Harold Abelson</rdf:li><rdf:li>Gerald Sussman</rdf:li></rdf:Seq></dc:creator>
            </rdf:Description></rdf:RDF></x:xmpmeta>
            endstream
            endobj

            """, "trailer\n<< /Root 1 0 R >>\n");
        var fromXmp = Read("pdf", xmp)!;
        Assert.AreEqual("Structure and Interpretation", fromXmp.Title);
        CollectionAssert.AreEqual(new[] { "Harold Abelson", "Gerald Sussman" }, Values(fromXmp, "author"));

        Assert.IsNull(Read("pdf", Pdf("1 0 obj\n<< /Title (Microsoft Word - draft.doc) >>\nendobj\n", "trailer\n<< /Info 1 0 R >>\n")), "a program's leftover is no title");
    }

    [TestMethod]
    public void LanguagesAndIsbnsAreNormalized()
    {
        Assert.AreEqual("ru", Languages.Normalize("rus"));
        Assert.AreEqual("en", Languages.Normalize("en-GB"));
        Assert.AreEqual("de", Languages.Normalize("ger"));
        Assert.IsNull(Languages.Normalize("12"));
        Assert.AreEqual("080442957X", Isbns.Normalize("0-8044-2957-x"));
        Assert.AreEqual("9780306406157", Isbns.Normalize("urn:isbn:978-0-306-40615-7"));
        Assert.IsNull(Isbns.Normalize("12345"));
        Assert.IsNull(Read("djvu", [1, 2, 3]), "no reader: kept by its name");
    }

    private static BookMetadata? Read(string format, byte[] bytes, bool seekable = true) =>
        Readers.Read(format, () => seekable ? new MemoryStream(bytes) : new GuardedStream(bytes, int.MaxValue), bytes.Length, CancellationToken.None);

    private static List<string> Values(BookMetadata metadata, string field) => metadata.FieldValues.Where(v => v.Field == field).Select(v => v.Value).ToList();

    private static byte[] Epub(string opf) => ZipOf(
        ("mimetype", "application/epub+zip"),
        ("META-INF/container.xml", """<?xml version="1.0"?><container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container"><rootfiles><rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/></rootfiles></container>"""),
        ("OEBPS/content.opf", opf));

    private static byte[] ZipOf(params (string Name, string Text)[] entries)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, text) in entries)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
                writer.Write(text);
            }

        return memory.ToArray();
    }

    private static byte[] Pdf(string objects, string trailer) => Encoding.Latin1.GetBytes("%PDF-1.4\n" + objects + "xref\n0 1\n0000000000 65535 f \n" + trailer + "startxref\n0\n%%EOF\n");

    /// <summary>A PalmDB with one record: a MOBI header (UTF-8) with its full name and an EXTH block.</summary>
    private static byte[] Mobi(string title, params (int Type, string Text)[] exth)
    {
        var records = new List<byte>();
        foreach (var (type, text) in exth)
        {
            var data = Encoding.UTF8.GetBytes(text);
            var record = new byte[8 + data.Length];
            BinaryPrimitives.WriteUInt32BigEndian(record, (uint)type);
            BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(4), (uint)record.Length);
            data.CopyTo(record, 8);
            records.AddRange(record);
        }

        var exthBlock = new byte[12 + records.Count];
        "EXTH"u8.CopyTo(exthBlock);
        BinaryPrimitives.WriteUInt32BigEndian(exthBlock.AsSpan(4), (uint)exthBlock.Length);
        BinaryPrimitives.WriteUInt32BigEndian(exthBlock.AsSpan(8), (uint)exth.Length);
        records.CopyTo(exthBlock, 12);

        const int record0 = 80;
        const int mobiLength = 232;
        var titleBytes = Encoding.UTF8.GetBytes(title);
        var titleOffset = 16 + mobiLength + exthBlock.Length;
        var file = new byte[record0 + titleOffset + titleBytes.Length + 8];
        Encoding.ASCII.GetBytes("hobbit").CopyTo(file, 0);
        "BOOKMOBI"u8.CopyTo(file.AsSpan(60));
        BinaryPrimitives.WriteUInt16BigEndian(file.AsSpan(76), 1);
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(78), record0);
        var mobi = record0 + 16;
        "MOBI"u8.CopyTo(file.AsSpan(mobi));
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(mobi + 4), mobiLength);
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(mobi + 12), 65001);
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(mobi + 68), (uint)titleOffset);
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(mobi + 72), (uint)titleBytes.Length);
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(mobi + 0x70), 0x40);
        exthBlock.CopyTo(file, mobi + mobiLength);
        titleBytes.CopyTo(file, record0 + titleOffset);
        return file;
    }

    /// <summary>Not seekable (an archive entry's data), and it notes a read past <paramref name="readableBeyond"/> bytes after its data.</summary>
    private sealed class GuardedStream(byte[] data, int readableBeyond) : Stream
    {
        private long position;
        private readonly long guard = Math.Min((long)data.Length + readableBeyond, long.MaxValue);
        public bool ReadPastGuard { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            // Past the data: a huge book's body, endless.
            if (position >= data.Length)
            {
                if (readableBeyond == int.MaxValue)
                    return 0;
                if (position + buffer.Length > guard)
                    ReadPastGuard = true;
                buffer.Fill((byte)'x');
                position += buffer.Length;
                return buffer.Length;
            }

            var count = (int)Math.Min(buffer.Length, data.Length - position);
            data.AsSpan((int)position, count).CopyTo(buffer);
            position += count;
            return count;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
