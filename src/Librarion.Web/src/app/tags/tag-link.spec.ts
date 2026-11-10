import { routes } from '../app.routes';
import { UrlSegment } from '@angular/router';
import { addressName, fieldValueMatcher, MAX_NAME_LENGTH, RESERVED_KEYS, tagLink } from './tag-link';

describe('Tag addresses', () => {
    it('puts a value under its field\'s key and a plain tag under /tag, with the name', () => {
        expect(tagLink({ id: 1234, name: 'Stephen King', field: 'author' })).toEqual(['/', 'author', 1234, 'Stephen King']);
        expect(tagLink({ id: 9, name: 'Horror', field: 'category' })).toEqual(['/', 'category', 9, 'Horror']);
        expect(tagLink({ id: 5, name: 'To read', field: null })).toEqual(['/tag', 5, 'To read']);
        expect(tagLink({ id: 5, name: 'To read' })).toEqual(['/tag', 5, 'To read']);
    });

    it('puts a key that clashes with a page under /fields', () => {
        expect(tagLink({ id: 3, name: 'Owned', field: 'books' })).toEqual(['/fields', 'books', 3, 'Owned']);
        expect(tagLink({ id: 3, name: 'x', field: 'api' })).toEqual(['/fields', 'api', 3, 'x']);
    });

    it('reserves the first segment of every route of the app', () => {
        const first = (paths: (string | undefined)[]) => paths.map(p => p?.split('/')[0]).filter(p => p && !p.startsWith(':') && p !== '**') as string[];
        const segments = first([...routes.map(r => r.path), ...routes.flatMap(r => r.children ?? []).map(r => r.path), 'admin']);
        expect(segments.length).toBeGreaterThan(5);
        for (const segment of segments)
            expect(RESERVED_KEYS.has(segment), segment).toBe(true);
    });

    it('trims the name, folds its spaces and cuts it, never halving a character', () => {
        expect(addressName('  Stephen   King ')).toBe('Stephen King');
        const long = '😀'.repeat(MAX_NAME_LENGTH + 10);
        expect(Array.from(addressName(long))).toHaveLength(MAX_NAME_LENGTH);
        expect(tagLink({ id: 4, name: '   ', field: 'author' })).toEqual(['/', 'author', 4]);
    });

    it('matches a key and a numeric id, with or without the name, and nothing else', () => {
        const match = (url: string) => fieldValueMatcher(url.split('/').map(p => new UrlSegment(p, {})));
        expect(match('author/12/Stephen King')?.posParams?.['id'].path).toBe('12');
        expect(match('author/12')?.posParams?.['key'].path).toBe('author');
        for (const url of ['no/such/page', 'author', 'author/12/name/more', 'Author/12', 'author/x12'])
            expect(match(url), url).toBeNull();
    });
});
