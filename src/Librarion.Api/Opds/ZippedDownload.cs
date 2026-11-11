using System.IO.Compression;
using Librarion.Core.Reading;
using Microsoft.Net.Http.Headers;

namespace Librarion.Api.Opds;

/// <summary>
/// A book file sent inside a ZIP of one entry, zipped while it is sent (agents/docs/features/opds.md: an FB2 as
/// <c>fb2.zip</c>, what many reader apps ask for): never a temp file; no Range and no length, the size is known only
/// at the end. The source is opened by the caller (its problems answered before), and disposed here.
/// </summary>
public sealed class ZippedDownload(BookFileContent content, string name, DateTime? modified) : IResult
{
    public const string ContentType = "application/zip";

    private const int ChunkSize = 64 * 1024;

    public async Task ExecuteAsync(HttpContext httpContext)
    {
        var cancellationToken = httpContext.RequestAborted;
        await using var source = content.Stream;
        var disposition = new ContentDispositionHeaderValue("attachment");
        disposition.SetHttpFileName(name + ".zip");
        httpContext.Response.ContentType = ContentType;
        httpContext.Response.Headers.ContentDisposition = disposition.ToString();
        var body = httpContext.Response.Body;

        // The archive writes into a buffer (parts of it write synchronously, which the response refuses), sent after
        // each chunk: memory stays about a chunk. It can't seek either: sizes and CRC follow the data (a data descriptor).
        var pending = new PendingStream();
        var buffer = new byte[ChunkSize];
        var zip = new ZipArchive(pending, ZipArchiveMode.Create, leaveOpen: true);
        try
        {
            var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
            if (modified is { } time && time.Year is >= 1980 and < 2108)
                entry.LastWriteTime = new DateTimeOffset(DateTime.SpecifyKind(time, DateTimeKind.Utc));
            using (var into = entry.Open())
            {
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    into.Write(buffer, 0, read);
                    await pending.SendAsync(body, cancellationToken);
                }
            }
        }
        finally
        {
            // Writes the central directory into the buffer.
            zip.Dispose();
        }
        await pending.SendAsync(body, cancellationToken);
    }

    /// <summary>A write-only stream gathering what is written until it is sent on.</summary>
    private sealed class PendingStream : Stream
    {
        private readonly MemoryStream _held = new();
        private long _written;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => _written;
            set => throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            _held.Write(buffer, offset, count);
            _written += count;
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            _held.Write(buffer);
            _written += buffer.Length;
        }

        public override void Flush()
        {
        }

        public async Task SendAsync(Stream target, CancellationToken cancellationToken)
        {
            if (_held.Length == 0)
                return;
            await target.WriteAsync(_held.GetBuffer().AsMemory(0, (int)_held.Length), cancellationToken);
            _held.SetLength(0);
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
