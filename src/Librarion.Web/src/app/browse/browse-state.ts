import { Injectable } from '@angular/core';
import { BookListItem, BookSort } from '../core/models';
import { readPreference, writePreference } from '../core/preferences';

export type BrowseView = 'grid' | 'list';

const VIEW = 'librarion.browse.view';
const SORT = 'librarion.browse.sort';
const SORTS: readonly BookSort[] = ['title', 'author', 'added'];

/** What a list held when its page was left: shown again as it was, at the same place, on the way back. */
export interface BrowseSnapshot {
    key: string;
    items: BookListItem[];
    next: string | null;
    /** How far down the list was scrolled (its top above the viewport's). */
    offset: number;
}

/**
 * The library's list between visits (agents/docs/features/browse.md): the view and sort chosen, remembered per
 * browser, and the last list left (its books, its next page, where it was scrolled), kept for this tab only.
 */
@Injectable({ providedIn: 'root' })
export class BrowseState {
    private snapshot: BrowseSnapshot | null = null;

    view(): BrowseView {
        return readPreference(VIEW) === 'list' ? 'list' : 'grid';
    }

    setView(view: BrowseView): void {
        writePreference(VIEW, view);
    }

    sort(): BookSort {
        const sort = readPreference(SORT) as BookSort | null;
        return sort && SORTS.includes(sort) ? sort : 'title';
    }

    setSort(sort: BookSort): void {
        writePreference(SORT, sort);
    }

    /** The list left under `key` (library and sort), if it was the last one. */
    restore(key: string): BrowseSnapshot | null {
        return this.snapshot?.key === key ? this.snapshot : null;
    }

    keep(snapshot: BrowseSnapshot): void {
        this.snapshot = snapshot;
    }
}
