/**
 * Readable addresses of tag pages (agents/docs/features/browse.md): `/<field key>/<id>/<name>` for a field's value
 * (`/author/1234/Stephen King`), `/tag/<id>/<name>` for a plain tag. The id finds the tag, the name is decorative:
 * the tag page replaces an address without it, or with an old one, by this one.
 */

import { UrlMatchResult, UrlSegment } from '@angular/router';

/**
 * First segments the app or the server routes itself: a field with such a key (one an import made, a Calibre column
 * named "books") has its values under `/fields/<key>/<id>/<name>` instead.
 */
export const RESERVED_KEYS: ReadonlySet<string> = new Set([
    'setup', 'login', 'fields', 'tags', 'tag', 'books', 'search', 'account', 'admin', 'api', 'openapi',
]);

/** The name's length in the address, in characters; past it the name is cut (the id finds the tag anyway). */
export const MAX_NAME_LENGTH = 80;

export interface LinkedTag {
    id: number;
    name: string;
    /** The tag's field key; null or absent for a plain tag. */
    field?: string | null;
}

/** The name as the address shows it: trimmed, cut to {@link MAX_NAME_LENGTH} characters (never half a surrogate pair). */
export function addressName(name: string): string {
    const characters = Array.from(name.trim().replace(/\s+/g, ' '));
    return characters.length <= MAX_NAME_LENGTH ? characters.join('') : characters.slice(0, MAX_NAME_LENGTH).join('').trimEnd();
}

/** The commands of a tag's page for `routerLink` or `navigate` (each segment encoded by the router). */
export function tagLink(tag: LinkedTag): (string | number)[] {
    const name = addressName(tag.name);
    const tail: (string | number)[] = name ? [tag.id, name] : [tag.id];
    if (!tag.field)
        return ['/tag', ...tail];
    return RESERVED_KEYS.has(tag.field) ? ['/fields', tag.field, ...tail] : ['/', tag.field, ...tail];
}

/**
 * `<key>/<id>` and `<key>/<id>/<name>` with a key as fields have them and a numeric id, so another unknown address
 * (`/no/such/page`) still finds no page. Matched after every route of the app.
 */
export function fieldValueMatcher(segments: UrlSegment[]): UrlMatchResult | null {
    if (segments.length < 2 || segments.length > 3 || !/^[a-z0-9-]{1,50}$/.test(segments[0].path) || !/^\d+$/.test(segments[1].path))
        return null;
    const posParams: Record<string, UrlSegment> = { key: segments[0], id: segments[1] };
    if (segments[2])
        posParams['name'] = segments[2];
    return { consumed: segments, posParams };
}
