import { Injectable, computed, inject, signal } from '@angular/core';
import { Api } from './api.service';
import { LibraryInfo } from './models';
import { readPreference, writePreference } from './preferences';

const CHOSEN = 'librarion.library';

/**
 * The libraries the session can open and the one being browsed (the top bar's switcher; browse, search and the book
 * pages read it). The choice is kept per browser; a library no longer reachable falls back to the first.
 */
@Injectable({ providedIn: 'root' })
export class LibraryContext {
    private readonly api = inject(Api);
    private readonly list = signal<LibraryInfo[]>([]);
    private readonly chosen = signal<number | null>(Number(readPreference(CHOSEN)) || null);

    readonly libraries = this.list.asReadonly();
    readonly current = computed<LibraryInfo | null>(() => {
        const libraries = this.list();
        return libraries.find(library => library.id === this.chosen()) ?? libraries[0] ?? null;
    });

    /** Asks the server again (after signing in, switching tenants, adding or deleting a library). */
    async load(): Promise<void> {
        try {
            this.list.set(await this.api.libraries());
        } catch {
            // The switcher just stays empty: the pages report their own errors.
            this.list.set([]);
        }
    }

    choose(id: number): void {
        this.chosen.set(id);
        writePreference(CHOSEN, String(id));
    }
}
