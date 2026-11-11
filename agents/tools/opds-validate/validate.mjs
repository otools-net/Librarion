#!/usr/bin/env node
// Checks OPDS 1.2 feeds against the official RELAX NG schemas with jing (agents/tools/opds-validate/README.md).
//
//   node validate.mjs fetch            downloads jing and the schemas once into the cache folder (checked by SHA-256)
//   node validate.mjs <file|folder>…   validates the feeds (.xml files; a folder: every .xml in it)
//
// The cache folder: LIBRARION_OPDS_SCHEMAS, else <tmp>/librarion-opds-validate. Nothing is fetched when validating.
import { spawnSync } from 'node:child_process';
import crypto from 'node:crypto';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';

const cache = path.resolve(process.env.LIBRARION_OPDS_SCHEMAS ?? path.join(os.tmpdir(), 'librarion-opds-validate'));

/** What `fetch` brings, pinned: a changed file is refused. */
const sources = [
    {
        file: 'jing.jar',
        url: 'https://repo1.maven.org/maven2/org/relaxng/jing/20241231/jing-20241231.jar',
        sha256: 'ea5e9026244d977e607d8b52212d6871498ece51939f9c49d0e7a77aad91133a',
    },
    {
        file: 'opds.rnc',
        url: 'https://raw.githubusercontent.com/opds-community/specs/6823741263367842d52a526834b3a5d7cefc32f3/schema/1.2/opds.rnc',
        sha256: 'cad97b25a0fb3f79c0ae16279c0fcb899c3d8c05535f9f34289bfdb661b2168c',
    },
    {
        file: 'rfc4287.txt',
        url: 'https://www.rfc-editor.org/rfc/rfc4287.txt',
        sha256: '3bd71d76289560c9e860151f39cebbdc402cfd8b788a62048d8dc42824f2d89c',
    },
];

const sha256 = data => crypto.createHash('sha256').update(data).digest('hex');

/**
 * atom.rnc as RFC 4287 gives it in Appendix B: from `# -*- rnc -*-` to `# EOF`, without the pages' headers, footers and
 * form feeds, unindented. opds.rnc includes it by that name.
 */
function atomSchema(rfc) {
    const lines = rfc.split(/\r?\n/);
    const start = lines.findIndex(line => line.trim() === '# -*- rnc -*-');
    const end = lines.findIndex((line, i) => i > start && line.trim() === '# EOF');
    if (start < 0 || end < 0)
        throw new Error('RFC 4287: the schema of Appendix B was not found');
    return lines.slice(start, end + 1)
        .filter(line => !/^Nottingham & Sayre\s+Standards Track/.test(line) && !/^RFC 4287\s+Atom Format/.test(line))
        .map(line => line.replace(/\f/g, '').replace(/^ {3}/, ''))
        .join('\n') + '\n';
}

/**
 * opds.rnc with its one known bug mended: its IRI rule excludes `[ <>{}|^`"\nrt]`, the backslashes of `\r` and `\t`
 * lost, so every address holding the letter r or t is refused (RELAX NG compact strings have no `\r` / `\t`
 * escapes; the XSD regular expression needs them). The intended set: space, <>{}|^`" and newline, return, tab.
 */
function fixedOpdsSchema(schema) {
    const broken = '[ <>{}|^`"\\nrt]';
    if (!schema.includes(broken))
        throw new Error('opds.rnc: the IRI rule to mend was not found (has the schema changed?)');
    return schema.replace(broken, '[ <>{}|^`"\\n\\r\\t]');
}

/** A file over HTTPS: Node's fetch, else curl (a firewall may let one through and not the other). */
async function download(url) {
    try {
        const response = await fetch(url);
        if (!response.ok)
            throw new Error(`${url}: ${response.status}`);
        return Buffer.from(await response.arrayBuffer());
    } catch (error) {
        const curl = spawnSync('curl', ['-fsSL', '--max-time', '120', url], { maxBuffer: 64 * 1024 * 1024 });
        if (curl.status === 0)
            return curl.stdout;
        throw new Error(`${url}: ${error.cause?.code ?? error.message}; curl: ${curl.error?.message ?? curl.stderr?.toString().trim()} (a firewall may block it)`);
    }
}

async function fetchAll() {
    fs.mkdirSync(cache, { recursive: true });
    for (const source of sources) {
        const target = path.join(cache, source.file);
        if (fs.existsSync(target) && sha256(fs.readFileSync(target)) === source.sha256)
            continue;
        const data = await download(source.url);
        const found = sha256(data);
        if (found !== source.sha256)
            throw new Error(`${source.url}: SHA-256 ${found}, expected ${source.sha256}`);
        fs.writeFileSync(target, data);
    }
    fs.writeFileSync(path.join(cache, 'opds-fixed.rnc'), fixedOpdsSchema(fs.readFileSync(path.join(cache, 'opds.rnc'), 'utf8')));
    fs.writeFileSync(path.join(cache, 'atom.rnc'), atomSchema(fs.readFileSync(path.join(cache, 'rfc4287.txt'), 'utf8')));
    console.log(`jing and the OPDS 1.2 / Atom schemas are in ${cache}`);
}

function validate(targets) {
    for (const file of ['jing.jar', 'opds-fixed.rnc', 'atom.rnc'])
        if (!fs.existsSync(path.join(cache, file))) {
            console.error(`${path.join(cache, file)} is missing: run "node validate.mjs fetch" first (or set LIBRARION_OPDS_SCHEMAS).`);
            return 2;
        }
    const files = targets.flatMap(target => fs.statSync(target).isDirectory()
        ? fs.readdirSync(target).filter(name => name.endsWith('.xml')).sort().map(name => path.join(target, name))
        : [target]);
    if (!files.length) {
        console.error('No feeds to check.');
        return 2;
    }
    // -c: the schema is in the compact syntax. jing names each problem with its file, line and column.
    const run = spawnSync('java', ['-jar', path.join(cache, 'jing.jar'), '-c', path.join(cache, 'opds-fixed.rnc'), ...files], { encoding: 'utf8' });
    if (run.error) {
        console.error(`java: ${run.error.message}`);
        return 2;
    }
    process.stdout.write(run.stdout);
    process.stderr.write(run.stderr);
    if (run.status === 0)
        console.log(`${files.length} feeds match OPDS 1.2.`);
    return run.status ?? 2;
}

const [command, ...rest] = process.argv.slice(2);
if (!command) {
    console.error('node validate.mjs fetch | node validate.mjs <file|folder>…');
    process.exit(2);
}
if (command === 'fetch')
    await fetchAll().catch(error => {
        console.error(error.message);
        process.exit(1);
    });
else
    process.exit(validate([command, ...rest]));
