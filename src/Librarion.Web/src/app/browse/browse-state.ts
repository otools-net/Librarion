import { Injectable } from '@angular/core';
import { BookSort } from '../core/models';
import { readPreference, writePreference } from '../core/preferences';

export type BrowseView = 'grid' | 'list';

/** Which lists share a sort: the library and the pages of tags, and series (by number by default). */
export type SortScope = 'library' | 'series' | 'shelf';

const VIEW = 'librarion.browse.view';
const SORT_KEYS: Record<SortScope, string> = {
    library: 'librarion.browse.sort', series: 'librarion.browse.sort.series', shelf: 'librarion.browse.sort.shelf',
};
const DEFAULT_SORT: Record<SortScope, BookSort> = { library: 'title', series: 'number', shelf: 'shelf' };
/** Lists kept for the way back: the library, a value list, a tag page and a few more. */
const KEPT = 6;

/** What a list held when its page was left: shown again as it was, at the same place, on the way back. */
export interface BrowseSnapshot<T = unknown> {
    key: string;
    items: T[];
    next: string | null;
    /** How far down the list was scrolled (its top above the viewport's). */
    offset: number;
}

/**
 * The lists between visits (agents/docs/features/browse.md): the view and sorts chosen, remembered per browser, and
 * the last lists left (their rows, their next page, where they were scrolled), kept for this tab only.
 */
@Injectable({ providedIn: 'root' })
export class BrowseState {
    private snapshots: BrowseSnapshot[] = [];

    view(): BrowseView {
        return readPreference(VIEW) === 'list' ? 'list' : 'grid';
    }

    setView(view: BrowseView): void {
        writePreference(VIEW, view);
    }

    /** The sort chosen for `scope`, when it is one of `sorts`; else the scope's default. */
    sort(scope: SortScope = 'library', sorts: readonly BookSort[] = ['title', 'author', 'added']): BookSort {
        const sort = readPreference(SORT_KEYS[scope]) as BookSort | null;
        return sort && sorts.includes(sort) ? sort : DEFAULT_SORT[scope];
    }

    setSort(sort: BookSort, scope: SortScope = 'library'): void {
        writePreference(SORT_KEYS[scope], sort);
    }

    /** The list left under `key` (what it listed and how), if it was one of the last ones. */
    restore<T>(key: string): BrowseSnapshot<T> | null {
        return (this.snapshots.find(s => s.key === key) as BrowseSnapshot<T> | undefined) ?? null;
    }

    /** Drops the list kept under `key` (it changed: it is loaded again). */
    forget(key: string): void {
        this.snapshots = this.snapshots.filter(s => s.key !== key);
    }

    keep<T>(snapshot: BrowseSnapshot<T>): void {
        this.snapshots = [snapshot as BrowseSnapshot, ...this.snapshots.filter(s => s.key !== snapshot.key)].slice(0, KEPT);
    }
}
