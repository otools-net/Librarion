import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { Api } from '../core/api.service';
import { errorText, valueOf } from '../core/errors';
import { I18n, TranslatePipe } from '../core/i18n.service';
import { ShelfInfo } from '../core/models';
import { ShelfStore } from '../core/shelf-store';
import { Icon } from '../shared/icon';
import { EmptyState, Skeleton } from '../shared/placeholders';
import { shelfLink } from './shelf-link';

/**
 * Shelves (`/shelves`; agents/docs/features/shelves.md): the account's shelves by name with their counts, each
 * opening its page, and New shelf. Never paged: an account has at most the setting `shelves.maxPerAccount`.
 */
@Component({
    selector: 'lib-shelves-page',
    imports: [TranslatePipe, Icon, EmptyState, Skeleton, RouterLink],
    changeDetection: ChangeDetectionStrategy.OnPush,
    host: { class: 'shelves-page' },
    template: `
        <header class="browse-head">
            <div class="browse-title">
                <h1>{{ 'shelves.title' | t }}</h1>
                <p class="browse-count">{{ 'shelves.intro' | t }}</p>
            </div>
        </header>
        <form class="shelf-new" novalidate (submit)="create($event)">
            <label class="field">
                <span class="field-label">{{ 'shelfPick.new' | t }}</span>
                <input type="text" name="shelf-name" maxlength="100" autocomplete="off" [value]="name()" (input)="name.set(valueOf($event))">
            </label>
            <button type="submit" class="button primary" [title]="'shelves.createHint' | t" [disabled]="busy() || !name().trim()">
                <svg libIcon="plus"></svg>{{ 'shelves.create' | t }}
            </button>
        </form>
        @if (error(); as error) {
            <p class="form-error" role="alert">{{ error }}</p>
        }
        @if (store.shelves().length) {
            <ul class="shelf-list" [attr.aria-label]="'shelves.title' | t">
                @for (shelf of store.shelves(); track shelf.id) {
                    <li>
                        <a class="shelf-row" [routerLink]="link(shelf)">
                            <svg libIcon="shelf"></svg>
                            <span class="shelf-row-name">{{ shelf.name }}</span>
                            <span class="shelf-row-count">{{ 'shelf.count' | t: { count: i18n.number(shelf.bookCount) } }}</span>
                        </a>
                    </li>
                }
            </ul>
        } @else if (store.loaded()) {
            <p class="empty large" libEmpty="shelf">{{ 'shelves.empty' | t }}</p>
        } @else {
            <lib-skeleton [rows]="4" label="shelves.loading" />
        }
    `,
})
export class ShelvesPage {
    private readonly api = inject(Api);
    protected readonly i18n = inject(I18n);
    protected readonly store = inject(ShelfStore);
    private readonly router = inject(Router);

    protected readonly name = signal('');
    protected readonly busy = signal(false);
    protected readonly error = signal<string | null>(null);
    protected readonly valueOf = valueOf;

    constructor() {
        // Asked again on this page: counts change with merges and with other sessions of the account.
        void this.store.load(true);
    }

    protected link(shelf: ShelfInfo): (string | number)[] {
        return shelfLink(shelf);
    }

    protected async create(event: Event): Promise<void> {
        event.preventDefault();
        const name = this.name().trim();
        if (!name)
            return;
        this.busy.set(true);
        this.error.set(null);
        try {
            const shelf = await this.api.createShelf(name);
            this.store.put(shelf);
            this.name.set('');
            await this.router.navigate(shelfLink(shelf));
        } catch (error) {
            this.error.set(errorText(this.i18n, error));
        } finally {
            this.busy.set(false);
        }
    }
}
