// The OPDS catalog as a reader app sees it (stage 7 chunk 12, agents/docs/features/opds.md): HTTP only, no browser.
// With the account's password (Basic): the root lists the libraries, a library's catalog, Authors, the author, the
// books, a download loose and one from inside a ZIP (the same bytes), the FB2 zipped on the fly, a search through the
// OpenSearch template, a shelf in its own order. With an API key as the password, and with a key in the address alone
// (every link keeping it, down to a download). Without credentials 401; with the web app's cookie only, 401 too.
// Once per run (desktop): a reader app has no screen to fit.
import fs from 'node:fs';
import path from 'node:path';
import { request } from '@playwright/test';
import { asAdmin, makeAccount, uniqueName } from '../fixtures/accounts';
import { AcquisitionRel, ReaderApp, entryOf, linkOf, type Feed } from '../fixtures/opds';
import { expect, test } from '../fixtures/test';
import { fb2, writeZip } from '../fixtures/zip';
import { baseURL, signedOut, work } from '../paths';

const headers = { 'X-Librarion-Request': '1' };
const Fb2ZipType = 'application/fb2+zip';

/** A library of its own (as fixtures/library.ts, without a page): written, added and scanned as the administrator. */
async function library(name: string, write: (folder: string) => void): Promise<number> {
    const folder = path.join(work, 'books', name);
    fs.rmSync(folder, { recursive: true, force: true });
    fs.mkdirSync(folder, { recursive: true });
    write(folder);
    const { id } = await asAdmin<{ id: number }>('POST', 'api/libraries', { name });
    await asAdmin('POST', `api/libraries/${id}/roots`, { path: folder });
    const { runId } = await asAdmin<{ runId: number }>('POST', `api/libraries/${id}/scan`);
    await expect.poll(async () => (await asAdmin<{ outcome: string | null }>('GET', `api/tasks/${runId}`)).outcome, { timeout: 30_000 }).toBe('succeeded');
    return id;
}

/** The book's file link (not the zipped one) in an acquisition feed. */
function fileLink(feed: Feed, title: string) {
    return entryOf(feed, title).links.find(l => l.rel === AcquisitionRel && l.type !== Fb2ZipType);
}

test.describe('@opds', () => {
    test.use({ storageState: signedOut });

    test('a reader app browses, searches and downloads with a password, a key, a key in the address @desktop-only', async ({ }, testInfo) => {
        const name = uniqueName('Opds', testInfo);
        const suffix = name.slice(name.lastIndexOf('-') + 1);
        const author = `Ann Reader${suffix}`;
        const books = { Alpha: fb2('Alpha', author), Beta: fb2('Beta', author), Gamma: fb2('Gamma', author) };
        const libraryId = await library(name, folder => {
            fs.writeFileSync(path.join(folder, 'Alpha.fb2'), books.Alpha);
            fs.writeFileSync(path.join(folder, 'Beta.fb2'), books.Beta);
            writeZip(path.join(folder, 'pack.zip'), [{ name: 'Gamma.fb2', data: books.Gamma }]);
        });
        const account = await makeAccount('opds', testInfo);

        // Nothing, or the web app's cookie only: refused, with the Basic challenge.
        const nobody = await ReaderApp.open();
        const none = await nobody.get('/opds');
        expect(none.status()).toBe(401);
        expect(none.headers()['www-authenticate']).toContain('Basic realm="Librarion"');
        const web = await request.newContext({ baseURL, extraHTTPHeaders: headers });
        expect((await web.post('api/session/login', { data: { userName: account.userName, password: account.password, remember: false } })).ok()).toBe(true);
        expect((await web.get('api/session')).ok(), 'the cookie signs in on the API').toBe(true);
        expect((await web.get('/opds')).status(), 'never on the catalog').toBe(401);

        // The password: root → the library → Authors → the author → the books.
        const app = await ReaderApp.open({ userName: account.userName, secret: account.password });
        const wrong = await ReaderApp.open({ userName: account.userName, secret: 'not-the-password' });
        expect((await wrong.get('/opds')).status()).toBe(401);
        const root = await app.feed('/opds', 'navigation');
        expect(linkOf(entryOf(root, name), 'subsection')?.href).toBe(`/opds/l/${libraryId}`);
        const catalog = await app.follow(root, name);
        expect(catalog.entries.slice(0, 2).map(e => e.title)).toEqual(['Recently added', 'All books by title']);
        const authors = await app.follow(catalog, 'Authors');
        const byAuthor = await app.follow(authors, author);
        expect(byAuthor.entries.map(e => e.title)).toEqual(['Alpha', 'Beta', 'Gamma']);

        // Downloads: a loose file, one inside the ZIP (the same bytes as written), an FB2 zipped while sent.
        const loose = await app.get(fileLink(byAuthor, 'Alpha')!.href);
        expect(loose.status()).toBe(200);
        expect(Buffer.compare(await loose.body(), books.Alpha)).toBe(0);
        const inZip = await app.get(fileLink(byAuthor, 'Gamma')!.href);
        expect(inZip.status()).toBe(200);
        expect(Buffer.compare(await inZip.body(), books.Gamma), 'read from inside pack.zip').toBe(0);
        const zipped = entryOf(byAuthor, 'Beta').links.find(l => l.type === Fb2ZipType)!;
        const zip = await app.get(zipped.href);
        expect(zip.status()).toBe(200);
        expect((await zip.body()).subarray(0, 2).toString('latin1')).toBe('PK');
        const cover = await app.get(linkOf(entryOf(byAuthor, 'Alpha'), 'http://opds-spec.org/image/thumbnail')!.href);
        expect(cover.headers()['content-type']).toBe('image/jpeg');

        // Search through the template the feed gives.
        const template = catalog.links.find(l => l.rel === 'search' && l.href.includes('{searchTerms}'))!.href;
        const found = await app.feed(template.replace('{searchTerms}', encodeURIComponent(`gamma author:"${author}"`)), 'acquisition');
        expect(found.entries.map(e => e.title)).toEqual(['Gamma']);

        // A shelf (made in the web app's session), in its own order.
        const shelf = await (await web.post('api/shelves', { data: { name: `Next ${suffix}` } })).json() as { id: number };
        const ids = Object.fromEntries((await app.feed(`/opds/l/${libraryId}/titles`)).entries.map(e => [e.title, Number(e.id.split(':').pop())]));
        expect((await web.post(`api/shelves/${shelf.id}/books`, { data: { bookIds: [ids['Gamma'], ids['Alpha']] } })).ok()).toBe(true);
        const withShelves = await app.feed(`/opds/l/${libraryId}`);
        const shelves = await app.follow(withShelves, 'My shelves');
        const onShelf = await app.follow(shelves, `Next ${suffix}`);
        expect(onShelf.entries.map(e => e.title)).toEqual(['Gamma', 'Alpha']);

        // An API key as the password, and one in the address alone.
        const key = (await (await web.post('api/account/api-keys', { data: { name: 'Reader', scopes: ['opds'], pathAllowed: true } })).json() as { key: string }).key;
        const keyed = await ReaderApp.open({ userName: account.userName, secret: key });
        expect((await keyed.feed('/opds')).entries.some(e => e.title === name)).toBe(true);
        const prefix = `/opds/k/${encodeURIComponent(key)}`;
        const byAddress = await nobody.feed(prefix, 'navigation');
        expect(byAddress.links.every(l => l.href.startsWith(prefix)), 'every link keeps the key').toBe(true);
        const keyCatalog = await nobody.follow(byAddress, name);
        const keyShelf = await nobody.follow(await nobody.follow(keyCatalog, 'My shelves'), `Next ${suffix}`);
        const download = fileLink(keyShelf, 'Gamma')!.href;
        expect(download.startsWith(`${prefix}/l/${libraryId}/books/`)).toBe(true);
        expect(Buffer.compare(await (await nobody.get(download)).body(), books.Gamma)).toBe(0);

        await Promise.all([nobody.dispose(), web.dispose(), app.dispose(), wrong.dispose(), keyed.dispose()]);
    });
});
