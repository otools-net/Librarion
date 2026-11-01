// A ZIP written by the test (Node has no zip writer, and a committed sample would be a binary in the repo): deflated
// entries, local headers, central directory and end record, as the format says. Small books only (no Zip64).
import fs from 'node:fs';
import path from 'node:path';
import zlib from 'node:zlib';

export interface ZipEntry {
    name: string;
    data: Buffer;
}

export function writeZip(file: string, entries: ZipEntry[]): void {
    const parts: Buffer[] = [];
    const directory: Buffer[] = [];
    let offset = 0;
    for (const entry of entries) {
        const name = Buffer.from(entry.name, 'utf8');
        const packed = zlib.deflateRawSync(entry.data);
        const crc = zlib.crc32(entry.data);
        const local = Buffer.alloc(30);
        local.writeUInt32LE(0x04034b50, 0);
        local.writeUInt16LE(20, 4);
        local.writeUInt16LE(0x0800, 6); // UTF-8 names
        local.writeUInt16LE(8, 8); // deflate
        local.writeUInt16LE(0, 10);
        local.writeUInt16LE(0x5a21, 12); // 2025-01-01
        local.writeUInt32LE(crc, 14);
        local.writeUInt32LE(packed.length, 18);
        local.writeUInt32LE(entry.data.length, 22);
        local.writeUInt16LE(name.length, 26);
        local.writeUInt16LE(0, 28);

        const central = Buffer.alloc(46);
        central.writeUInt32LE(0x02014b50, 0);
        central.writeUInt16LE(20, 4);
        central.writeUInt16LE(20, 6);
        local.copy(central, 8, 6, 30); // flags … extra length: as the local header
        central.writeUInt16LE(0, 32);
        central.writeUInt16LE(0, 34);
        central.writeUInt16LE(0, 36);
        central.writeUInt32LE(0, 38);
        central.writeUInt32LE(offset, 42);
        directory.push(central, name);

        parts.push(local, name, packed);
        offset += local.length + name.length + packed.length;
    }

    const size = directory.reduce((sum, part) => sum + part.length, 0);
    const end = Buffer.alloc(22);
    end.writeUInt32LE(0x06054b50, 0);
    end.writeUInt16LE(entries.length, 8);
    end.writeUInt16LE(entries.length, 10);
    end.writeUInt32LE(size, 12);
    end.writeUInt32LE(offset, 16);
    fs.mkdirSync(path.dirname(file), { recursive: true });
    fs.writeFileSync(file, Buffer.concat([...parts, ...directory, end]));
}

/** An FB2 with a title and an author, and some text. */
export function fb2(title: string, author: string): Buffer {
    return Buffer.from(`<?xml version="1.0" encoding="utf-8"?>
<FictionBook xmlns="http://www.gribuser.ru/xml/fictionbook/2.0"><description><title-info><genre>sf_fantasy</genre>
<author><first-name>${author.split(' ')[0]}</first-name><last-name>${author.split(' ').slice(1).join(' ')}</last-name></author>
<book-title>${title}</book-title><lang>en</lang></title-info></description>
<body><section><p>${'Once upon a time. '.repeat(200)}</p></section></body></FictionBook>`, 'utf8');
}
