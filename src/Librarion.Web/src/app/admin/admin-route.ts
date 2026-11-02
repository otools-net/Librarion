import { UrlMatchResult, UrlSegment } from '@angular/router';

// Apart from the page: the routes match the administration's addresses without loading it (it loads when opened).

/**
 * `admin`, `admin/:section`, `admin/:section/:page`, and a library's pages `admin/libraries/:library/:page`: one route,
 * so the page (and its column) stays between sections.
 */
export function adminMatcher(segments: UrlSegment[]): UrlMatchResult | null {
    if (segments[0]?.path !== 'admin')
        return null;
    const library = segments[1]?.path === 'libraries' && /^\d+$/.test(segments[2]?.path ?? '');
    if (segments.length > (library ? 4 : 3))
        return null;
    const posParams: Record<string, UrlSegment> = {};
    if (segments[1])
        posParams['section'] = segments[1];
    if (library) {
        posParams['library'] = segments[2];
        if (segments[3])
            posParams['page'] = segments[3];
    } else if (segments[2]) {
        posParams['page'] = segments[2];
    }
    return { consumed: segments, posParams };
}
