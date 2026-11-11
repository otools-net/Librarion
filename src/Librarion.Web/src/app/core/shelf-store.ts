import { Injectable, inject, signal } from '@angular/core';
import { Api } from './api.service';
import { ShelfInfo } from './models';
import { SessionService } from './session.service';

/**
 * The account's shelves (agents/docs/features/shelves.md), as the sidebar, the Shelves page and Add to shelf show
 * them: loaded once per account and tenant, kept in step by the changes made here (a shelf made, renamed, deleted,
 * books put on or taken off). At most the setting `shelves.maxPerAccount` (100 by default): never paged.
 */
@Injectable({ providedIn: 'root' })
export class ShelfStore {
    private readonly api = inject(Api);
    private readonly session = inject(SessionService);
    private readonly list = signal<ShelfInfo[]>([]);
    private readonly done = signal(false);
    private loadedFor: string | null = null;

    readonly shelves = this.list.asReadonly();
    /** True once the list has come (or failed: then it stays empty and the pages report their own errors). */
    readonly loaded = this.done.asReadonly();

    /** The shelves of the session's account in its tenant: asked again only when they change, or with `again`. */
    async load(again = false): Promise<void> {
        const session = this.session.session();
        const owner = `${session?.user?.id}/${session?.tenant?.id}`;
        if (owner === this.loadedFor && !again)
            return;
        this.loadedFor = owner;
        try {
            const shelves = await this.api.shelves();
            if (this.loadedFor === owner)
                this.list.set(shelves);
        } catch {
            if (this.loadedFor === owner)
                this.list.set([]);
        } finally {
            if (this.loadedFor === owner)
                this.done.set(true);
        }
    }

    /** Forgets the list (signed out, or no right to shelves). */
    clear(): void {
        this.loadedFor = null;
        this.list.set([]);
        this.done.set(false);
    }

    /** A shelf made, renamed or changed: put in (by name, as the server lists them) or replaced. */
    put(shelf: ShelfInfo): void {
        this.list.update(list => [...list.filter(s => s.id !== shelf.id), shelf]
            .sort((a, b) => a.name.localeCompare(b.name, undefined, { sensitivity: 'base' })));
    }

    remove(id: number): void {
        this.list.update(list => list.filter(s => s.id !== id));
    }

    get(id: number): ShelfInfo | null {
        return this.list().find(s => s.id === id) ?? null;
    }
}
