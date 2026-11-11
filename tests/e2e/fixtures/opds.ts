// A reader app as far as the specs need one (agents/docs/features/opds.md): asks the catalog over HTTP only (no
// browser, no cookie unless given), reads its Atom feeds into titles and links, follows them. The feeds are
// Librarion's own, so a few regular expressions read them; schema checks are agents/tools/opds-validate's job.
import { request, type APIRequestContext, type APIResponse } from '@playwright/test';
import { baseURL } from '../paths';
import { expect } from './test';

export interface FeedLink {
    rel: string | null;
    href: string;
    type: string | null;
    title: string | null;
}

export interface FeedEntry {
    id: string;
    title: string;
    links: FeedLink[];
}

export interface Feed {
    id: string;
    title: string;
    links: FeedLink[];
    entries: FeedEntry[];
}

const entities: Record<string, string> = { amp: '&', lt: '<', gt: '>', quot: '"', apos: "'" };

function decode(text: string): string {
    return text.replace(/&(#x[0-9a-f]+|#\d+|\w+);/gi, (whole, name: string) =>
        name[0] === '#' ? String.fromCodePoint(Number.parseInt(name.slice(name[1].toLowerCase() === 'x' ? 2 : 1), name[1].toLowerCase() === 'x' ? 16 : 10))
            : entities[name] ?? whole);
}

function text(xml: string, element: string): string {
    const match = new RegExp(`<${element}(?:\\s[^>]*)?>([^<]*)</${element}>`).exec(xml);
    return match ? decode(match[1]) : '';
}

function links(xml: string): FeedLink[] {
    return [...xml.matchAll(/<link\b([^>]*?)\/?>/g)].map(([, attributes]) => {
        const values = Object.fromEntries([...attributes.matchAll(/([\w:-]+)="([^"]*)"/g)].map(([, name, value]) => [name, decode(value)]));
        return { rel: values['rel'] ?? null, href: values['href'], type: values['type'] ?? null, title: values['title'] ?? null };
    });
}

/** A feed's id, title and links (those of the feed, not its entries'), and each entry's. */
export function parseFeed(xml: string): Feed {
    const [head, ...rest] = xml.split(/<entry(?:\s[^>]*)?>/);
    const entries = rest.map(part => part.slice(0, part.indexOf('</entry>'))).map(entry => ({
        id: text(entry, 'id'), title: text(entry, 'title'), links: links(entry),
    }));
    return { id: text(head, 'id'), title: text(head, 'title'), links: links(head), entries };
}

/** The link of `rel` (and `type`, when given). */
export function linkOf(item: { links: FeedLink[] }, rel: string, type?: string): FeedLink | undefined {
    return item.links.find(link => link.rel === rel && (type === undefined || link.type === type));
}

/** The entry titled `title` (fails when there is none). */
export function entryOf(feed: Feed, title: string): FeedEntry {
    const entry = feed.entries.find(e => e.title === title);
    expect(entry, `"${title}" in ${feed.title}: ${feed.entries.map(e => e.title).join(', ')}`).toBeDefined();
    return entry!;
}

export const AcquisitionRel = 'http://opds-spec.org/acquisition';

/** HTTP as a reader app: Basic with a name and a password (or a key), or nothing (a key in the address). */
export class ReaderApp {
    private constructor(readonly http: APIRequestContext) {}

    static async open(credentials?: { userName: string; secret: string }): Promise<ReaderApp> {
        const extraHTTPHeaders: Record<string, string> = {};
        if (credentials)
            extraHTTPHeaders['Authorization'] = `Basic ${Buffer.from(`${credentials.userName}:${credentials.secret}`).toString('base64')}`;
        return new ReaderApp(await request.newContext({ baseURL, extraHTTPHeaders }));
    }

    get(href: string): Promise<APIResponse> {
        return this.http.get(href, { maxRedirects: 0 });
    }

    /** A feed (200, Atom, of the kind given when given), read. */
    async feed(href: string, kind?: 'navigation' | 'acquisition'): Promise<Feed> {
        const response = await this.get(href);
        expect(response.status(), href).toBe(200);
        const type = response.headers()['content-type'];
        expect(type, href).toContain('application/atom+xml');
        if (kind)
            expect(type, href).toContain(`kind=${kind}`);
        return parseFeed(await response.text());
    }

    /** Follows the link of an entry titled `title` in `feed`. */
    follow(feed: Feed, title: string, rel = 'subsection'): Promise<Feed> {
        const link = linkOf(entryOf(feed, title), rel);
        expect(link, `${rel} of "${title}"`).toBeDefined();
        return this.feed(link!.href);
    }

    dispose(): Promise<void> {
        return this.http.dispose();
    }
}
