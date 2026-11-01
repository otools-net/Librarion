using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Librarion.Core.Containers;

namespace Librarion.Tests;

/// <summary>
/// Reading ZIP containers in place (<see cref="ZipDirectory"/>, <see cref="ArchiveEntryNames"/>, ported from SFE with
/// its tests): the directory read record by record, one seek per entry, and the archives a stranger makes to get out of
/// their folder or to fill the disk.
/// </summary>
[TestClass]
public sealed class ZipDirectoryTests
{
    private string folder = "";

    [TestInitialize]
    public void Create()
    {
        folder = Path.Combine(Path.GetTempPath(), "librarion-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
    }

    [TestCleanup]
    public void Delete() => Directory.Delete(folder, recursive: true);

    [TestMethod]
    [DataRow("../evil.fb2", "goes up")]
    [DataRow("a/../../evil.fb2", "goes up")]
    [DataRow("a\\..\\..\\evil.fb2", "goes up")]
    [DataRow("/etc/passwd", "root")]
    [DataRow("\\\\server\\share\\x", "root")]
    [DataRow("C:\\Windows\\evil.dll", "drive")]
    [DataRow("C:evil.dll", "drive")]
    [DataRow("a\0b.fb2", "control")]
    [DataRow("a/.../b.fb2", "dots")]
    [DataRow("", "empty")]
    [DataRow("./", "empty")]
    public void NamesThatLeaveTheirFolderAreRefused(string name, string why)
    {
        Assert.IsNull(ArchiveEntryNames.Split(name, 100, out var reason));
        Assert.Contains(why, reason!);
    }

    [TestMethod]
    public void NamesAreSplitIntoTheirFolders()
    {
        CollectionAssert.AreEqual(new[] { "a", "b", "c.fb2" }, ArchiveEntryNames.Split("a/./b//c.fb2", 100, out _));
        CollectionAssert.AreEqual(new[] { "a", "b.fb2" }, ArchiveEntryNames.Split("a\\b.fb2", 100, out _));
        Assert.IsNull(ArchiveEntryNames.Split(string.Join('/', Enumerable.Repeat("d", 101)), 100, out var reason));
        Assert.Contains("too deep", reason!);
    }

    [TestMethod]
    public void AZipIsListedFromItsDirectoryAndItsEntriesOpenWithOneSeek()
    {
        var big = new string('z', 100_000);
        var zip = TestZips.Zip(folder, "a.zip", ("readme.txt", "hello"), ("Books/", null), ("Books/año/día.fb2", "unicode"), ("Books/big.fb2", big));
        var entries = List(zip);

        CollectionAssert.AreEqual(new[] { "readme.txt", "Books/", "Books/año/día.fb2", "Books/big.fb2" }, entries.Select(e => e.Name).ToList());
        Assert.AreEqual(ArchiveEntryKind.Folder, entries[1].Kind);
        Assert.AreEqual(100_000, entries[3].Size);
        Assert.IsLessThan(1000, entries[3].Packed, "deflated");
        Assert.AreEqual(8, entries[3].Method);
        Assert.AreEqual(System.IO.Hashing.Crc32.HashToUInt32(Encoding.UTF8.GetBytes(big)), entries[3].Crc);
        Assert.IsNotNull(entries[0].Time);
        Assert.AreEqual("unicode", Read(zip, entries[2]));
        Assert.AreEqual(big, Read(zip, entries[3]));

        var stored = TestZips.Zip(folder, "stored.zip", CompressionLevel.NoCompression, ("s.fb2", "stored as is"));
        var entry = List(stored).Single();
        Assert.AreEqual(0, entry.Method);
        Assert.AreEqual("stored as is", Read(stored, entry));
        using var file = File.OpenRead(stored);
        var range = ZipDirectory.Locate(file).StoredRange(file, entry);
        Assert.IsNotNull(range);
        Assert.AreEqual(entry.Size, range.Value.Length);
        file.Position = range.Value.Start;
        var bytes = new byte[range.Value.Length];
        file.ReadExactly(bytes);
        Assert.AreEqual("stored as is", Encoding.UTF8.GetString(bytes), "a stored entry is a slice of the file");
    }

    [TestMethod]
    public void NamesWithoutTheUtf8FlagAreReadAsCodePage437()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var path = Path.Combine(folder, "dos.zip");
        using (var file = File.Create(path))
        using (var zip = new ZipArchive(file, ZipArchiveMode.Create, false, Encoding.GetEncoding(437)))
            zip.CreateEntry("über.fb2");
        Assert.AreEqual("über.fb2", List(path).Single().Name);
    }

    [TestMethod]
    public void AZipOfMoreThan65535EntriesIsReadThroughZip64()
    {
        var path = Path.Combine(folder, "many.zip");
        using (var file = File.Create(path))
        using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
            for (var i = 0; i < 70_000; i++)
                zip.CreateEntry($"f{i / 1000}/{i}.fb2", CompressionLevel.NoCompression);

        var count = 0;
        foreach (var entry in List(path))
            Assert.AreEqual($"f{count / 1000}/{count++}.fb2", entry.Name);
        Assert.AreEqual(70_000, count);
    }

    [TestMethod]
    public void DataPrependedToTheZipShiftsEveryOffset()
    {
        var zip = TestZips.Zip(folder, "plain.zip", ("a.fb2", "first"), ("b.fb2", "second"));
        var shifted = Path.Combine(folder, "shifted.zip");
        File.WriteAllBytes(shifted, [.. new byte[1000], .. File.ReadAllBytes(zip)]);

        using var file = File.OpenRead(shifted);
        var directory = ZipDirectory.Locate(file);
        Assert.AreEqual(1000, directory.Shift);
        var entries = directory.Read(file, CancellationToken.None).ToList();
        Assert.AreEqual("second", Read(shifted, entries[1]));
    }

    [TestMethod]
    public void TheFingerprintChangesWithTheDirectoryOnly()
    {
        var zip = TestZips.Zip(folder, "a.zip", ("a.fb2", "first"), ("b.fb2", "second"));
        var copy = Path.Combine(folder, "copy.zip");
        File.Copy(zip, copy);
        File.SetLastWriteTimeUtc(copy, DateTime.UtcNow.AddDays(-3));
        var other = TestZips.Zip(folder, "other.zip", ("a.fb2", "first"), ("b.fb2", "changed"));

        Assert.AreEqual(Fingerprint(zip), Fingerprint(copy), "touched, not changed");
        Assert.AreNotEqual(Fingerprint(zip), Fingerprint(other), "an entry's CRC and size are in the directory");
    }

    [TestMethod]
    public void AnEntryGivingMoreThanItDeclaresIsCutOffAsBroken()
    {
        // A zip bomb's trick: a directory claiming 1 KB for an entry that inflates to 1 MB.
        var zip = TestZips.Zip(folder, "bomb.zip", ("zeros.fb2", new string('\0', 1024 * 1024)));
        TestZips.PatchDirectory(zip, 24, BitConverter.GetBytes(1024u));
        var entry = List(zip).Single();
        Assert.AreEqual(1024, entry.Size);

        using var file = File.OpenRead(zip);
        using var data = ZipDirectory.Locate(file).OpenEntry(file, entry);
        Assert.Contains("more data than", Assert.Throws<ArchiveException>(() => data.CopyTo(Stream.Null)).Message);
    }

    [TestMethod]
    public void AnEntryCutShortOrDamagedIsBroken()
    {
        var zip = TestZips.Zip(folder, "short.zip", ("b.fb2", new string('b', 5000)));
        TestZips.PatchDirectory(zip, 24, BitConverter.GetBytes(6000u));
        var entry = List(zip).Single();
        using (var file = File.OpenRead(zip))
        using (var data = ZipDirectory.Locate(file).OpenEntry(file, entry))
            Assert.Contains("ends before", Assert.Throws<ArchiveException>(() => data.CopyTo(Stream.Null)).Message);

        var crc = TestZips.Zip(folder, "crc.zip", ("c.fb2", "checked at the end"));
        TestZips.PatchDirectory(crc, 16, BitConverter.GetBytes(12345u));
        var wrong = List(crc).Single();
        using (var file = File.OpenRead(crc))
        using (var data = ZipDirectory.Locate(file).OpenEntry(file, wrong))
            Assert.Contains("checksum", Assert.Throws<ArchiveException>(() => data.CopyTo(Stream.Null)).Message);
    }

    [TestMethod]
    public void ADirectoryThatLiesIsRefused()
    {
        var zip = TestZips.Zip(folder, "count.zip", ("a.fb2", "a"));
        // The end record claims 60 000 entries in a directory of one.
        var bytes = File.ReadAllBytes(zip);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(bytes.Length - 22 + 10), 60_000);
        File.WriteAllBytes(zip, bytes);
        Assert.Contains("more entries than", Assert.Throws<ArchiveException>(() => List(zip)).Message);

        var outside = TestZips.Zip(folder, "offset.zip", ("a.fb2", "a"));
        TestZips.PatchDirectory(outside, 42, BitConverter.GetBytes(1_000_000u));
        var entry = List(outside).Single();
        using (var file = File.OpenRead(outside))
            Assert.Contains("outside", Assert.Throws<ArchiveException>(() => ZipDirectory.Locate(file).OpenEntry(file, entry)).Message);

        var cut = TestZips.Zip(folder, "cut.zip", ("a.fb2", "a"));
        File.WriteAllBytes(cut, File.ReadAllBytes(cut)[..^10]);
        Assert.Throws<ArchiveException>(() => List(cut));

        var text = Path.Combine(folder, "not-a.zip");
        File.WriteAllText(text, "just text, long enough to have an end record's room");
        Assert.Throws<ArchiveException>(() => List(text));
    }

    [TestMethod]
    public void EncryptedEntriesAreListedButNotOpened()
    {
        var zip = TestZips.Zip(folder, "secret.zip", ("a.fb2", "a"));
        TestZips.PatchDirectory(zip, 8, BitConverter.GetBytes((ushort)1)); // flags: encrypted
        var entry = List(zip).Single();
        Assert.IsTrue(entry.Encrypted);
        using var file = File.OpenRead(zip);
        Assert.Contains("encrypted", Assert.Throws<ArchiveException>(() => ZipDirectory.Locate(file).OpenEntry(file, entry)).Message);
    }

    [TestMethod]
    public void LinksMadeOnUnixAreListedAsLinks()
    {
        var zip = TestZips.Zip(folder, "link.zip", ("link", "/etc/passwd"));
        TestZips.PatchDirectory(zip, 4, [0x14, 3]); // made by: Unix
        TestZips.PatchDirectory(zip, 38, BitConverter.GetBytes(0xA1FFu << 16)); // mode: a symbolic link
        Assert.AreEqual(ArchiveEntryKind.Link, List(zip).Single().Kind);
    }

    [TestMethod]
    public void OtherMethodsAreReadThroughSharpCompress()
    {
        // Zstandard (method 93): one entry written here, its data by SharpCompress' encoder.
        var text = string.Concat(Enumerable.Repeat("Zstandard inside a zip. ", 200));
        var plain = Encoding.UTF8.GetBytes(text);
        using var packed = new MemoryStream();
        using (var encoder = new SharpCompress.Compressors.ZStandard.CompressionStream(packed, 3))
            encoder.Write(plain);
        var zip = Path.Combine(folder, "zstd.zip");
        File.WriteAllBytes(zip, TestZips.OneEntry("zstd.fb2", method: 93, packed.ToArray(), plain));

        var entry = List(zip).Single();
        Assert.AreEqual(93, entry.Method);
        Assert.AreEqual(text, Read(zip, entry));

        File.WriteAllBytes(zip, TestZips.OneEntry("odd.fb2", method: 77, plain, plain));
        using var file = File.OpenRead(zip);
        Assert.Contains("(77)", Assert.Throws<ArchiveException>(() => ZipDirectory.Locate(file).OpenEntry(file, List(zip).Single())).Message);
    }

    private static List<ArchiveEntry> List(string path)
    {
        using var file = File.OpenRead(path);
        return ZipDirectory.Locate(file).Read(file, CancellationToken.None).ToList();
    }

    private static long Fingerprint(string path)
    {
        using var file = File.OpenRead(path);
        return ZipDirectory.Locate(file).Fingerprint(file, CancellationToken.None);
    }

    private static string Read(string path, ArchiveEntry entry)
    {
        using var file = File.OpenRead(path);
        using var data = ZipDirectory.Locate(file).OpenEntry(file, entry);
        using var reader = new StreamReader(data);
        return reader.ReadToEnd();
    }
}

/// <summary>Zips made by the tests (never committed binaries): <see cref="ZipArchive"/>, or byte by byte.</summary>
internal static class TestZips
{
    /// <summary>A zip of <paramref name="entries"/> (a null text = a folder entry).</summary>
    public static string Zip(string folder, string name, params (string Name, string? Text)[] entries) =>
        Zip(folder, name, CompressionLevel.Optimal, entries);

    public static string Zip(string folder, string name, CompressionLevel level, params (string Name, string? Text)[] entries)
    {
        var path = Path.Combine(folder, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var file = File.Create(path);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);
        foreach (var (entryName, text) in entries)
        {
            var entry = zip.CreateEntry(entryName, level);
            if (text is null)
                continue;
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write(text);
        }
        return path;
    }

    /// <summary>Writes <paramref name="bytes"/> at <paramref name="offset"/> of a central-directory record (the first by default).</summary>
    public static void PatchDirectory(string zip, int offset, byte[] bytes, int record = 0)
    {
        var content = File.ReadAllBytes(zip);
        var at = -1;
        for (var i = 0; i <= record; i++)
        {
            var next = content.AsSpan(at + 1).IndexOf("PK\x01\x02"u8);
            Assert.IsGreaterThanOrEqualTo(0, next, $"central-directory record {i}");
            at += next + 1;
        }
        bytes.CopyTo(content, at + offset);
        File.WriteAllBytes(zip, content);
    }

    /// <summary>A zip of one entry whose data is <paramref name="packed"/> as <paramref name="method"/> says (CRC and size of <paramref name="plain"/>).</summary>
    public static byte[] OneEntry(string name, ushort method, byte[] packed, byte[] plain)
    {
        var nameBytes = Encoding.UTF8.GetBytes(name);
        var crc = System.IO.Hashing.Crc32.HashToUInt32(plain);
        using var zip = new MemoryStream();
        using var writer = new BinaryWriter(zip);
        void Common()
        {
            writer.Write((ushort)0x0800);
            writer.Write(method);
            writer.Write((ushort)0);
            writer.Write((ushort)0x21);
            writer.Write(crc);
            writer.Write((uint)packed.Length);
            writer.Write((uint)plain.Length);
            writer.Write((ushort)nameBytes.Length);
            writer.Write((ushort)0);
        }
        writer.Write(0x04034b50u);
        writer.Write((ushort)63);
        Common();
        writer.Write(nameBytes);
        writer.Write(packed);
        var directory = zip.Position;
        writer.Write(0x02014b50u);
        writer.Write((ushort)63);
        writer.Write((ushort)63);
        Common();
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write(0u);
        writer.Write(0u);
        writer.Write(nameBytes);
        var size = zip.Position - directory;
        writer.Write(0x06054b50u);
        writer.Write(0u);
        writer.Write((ushort)1);
        writer.Write((ushort)1);
        writer.Write((uint)size);
        writer.Write((uint)directory);
        writer.Write((ushort)0);
        writer.Flush();
        return zip.ToArray();
    }
}
