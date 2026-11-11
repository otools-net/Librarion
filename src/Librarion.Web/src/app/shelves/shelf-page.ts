import { ChangeDetectionStrategy, Component, computed, effect, inject, signal, untracked, viewChild } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { Api } from '../core/api.service';
import { errorText, valueOf } from '../core/errors';
import { I18n, TranslatePipe } from '../core/i18n.service';
import { BookSort, ShelfInfo } from '../core/models';
import { ShelfStore } from '../core/shelf-store';
import { ToastService } from '../core/toast.service';
import { BookList } from '../browse/book-list';
import { ConfirmService } from '../shared/confirm';
import { Icon } from '../shared/icon';
import { EmptyState, Skeleton } from '../shared/placeholders';
import { Sheet } from '../shared/sheet';
import { shelfLink } from './shelf-link';

const SHELF_SORTS: readonly BookSort[] = ['shelf', 'title', 'author', 'added'];

/**
 * A shelf (`/shelves/<id>/<name>`; agents/docs/features/shelves.md): its name and count over its books (`BookList`
 * with the shelf: the library's list, in the shelf's own order by default, reordered by drag, keys or Move; also by
 * title, author or date added), Rename and Delete (asking first; the books stay in their libraries).
 */
@Component({
    selector: 'lib-shelf-page',
    imports: [TranslatePipe, Icon, EmptyState, Skeleton, RouterLink, BookList, Sheet],
    changeDetection: ChangeDetectionStrategy.OnPush,
    host: { class: 'shelf-page' },
    template: `
        @if (shelf(); as shelf) {
            <lib-book-list [shelf]="shelf.id" [label]="'shelf.books' | t: { name: shelf.name }" [sorts]="sorts" sortScope="shelf"
                           sortLabels="shelf.sort." emptyText="shelf.empty">
                <a class="tag-kind" routerLink="/shelves"><svg libIcon="shelf"></svg>{{ 'shelves.one' | t }}</a>
                <h1>{{ shelf.name }}</h1>
                <p class="browse-count">{{ 'shelf.count' | t: { count: i18n.number(shelf.bookCount) } }}</p>
                <button listTools type="button" class="button" [title]="'shelf.renameHint' | t" (click)="startRename(shelf)">
                    <svg libIcon="edit"></svg><span class="tool-label">{{ 'shelf.rename' | t }}</span>
                </button>
                <button listTools type="button" class="button" [title]="'shelf.deleteHint' | t" [disabled]="busy()" (click)="remove(shelf)">
                    <svg libIcon="trash"></svg><span class="tool-label">{{ 'common.delete' | t }}</span>
                </button>
                <span emptyExtra>{{ 'shelf.emptyHow' | t }}</span>
            </lib-book-list>
            @if (renaming()) {
                <lib-sheet [heading]="'shelf.rename' | t" (closed)="renaming.set(false)">
                    <form id="shelf-rename-form" novalidate (submit)="rename($event, shelf)">
                        <label class="field">
                            <span class="field-label">{{ 'shelf.name' | t }}</span>
                            <input type="text" name="shelf-name" maxlength="100" autocomplete="off" required [value]="name()" (input)="name.set(valueOf($event))">
                        </label>
                        @if (renameError(); as error) {
                            <p class="form-error" role="alert">{{ error }}</p>
                        }
                    </form>
                    <div sheetFooter class="sheet-actions">
                        <button type="button" class="button" [title]="'common.cancelHint' | t" (click)="sheet()?.close()">{{ 'common.cancel' | t }}</button>
                        <button type="submit" form="shelf-rename-form" class="button primary" [title]="'common.saveHint' | t"
                                [disabled]="busy() || !name().trim()">{{ 'common.save' | t }}</button>
                    </div>
                </lib-sheet>
            }
        } @else if (missing()) {
            <h1>{{ 'shelf.missingTitle' | t }}</h1>
            <p class="empty large" libEmpty="shelf">
                {{ 'shelf.missing' | t }}
                <a routerLink="/shelves">{{ 'shelf.toShelves' | t }}</a>
            </p>
        } @else if (error(); as error) {
            <p class="form-error" role="alert">{{ error }} <button type="button" class="button" (click)="load()">{{ 'common.retry' | t }}</button></p>
        } @else {
            <lib-skeleton [rows]="6" label="shelf.loading" />
        }
    `,
})
export class ShelfPage {
    private readonly api = inject(Api);
    protected readonly i18n = inject(I18n);
    private readonly route = inject(ActivatedRoute);
    private readonly router = inject(Router);
    private readonly store = inject(ShelfStore);
    private readonly confirm = inject(ConfirmService);
    private readonly toasts = inject(ToastService);

    protected readonly sorts = SHELF_SORTS;
    private readonly params = toSignal(this.route.paramMap, { initialValue: this.route.snapshot.paramMap });
    private readonly id = computed(() => Number(this.params().get('id')));
    private readonly loaded = signal<ShelfInfo | null>(null);
    /** The shelf as the store has it now (its count follows books put on and taken off), else as loaded. */
    protected readonly shelf = computed(() => {
        const loaded = this.loaded();
        return loaded && (this.store.shelves().find(s => s.id === loaded.id) ?? loaded);
    });
    protected readonly missing = signal(false);
    protected readonly error = signal<string | null>(null);
    protected readonly busy = signal(false);
    protected readonly renaming = signal(false);
    protected readonly name = signal('');
    protected readonly renameError = signal<string | null>(null);
    protected readonly sheet = viewChild(Sheet);
    protected readonly valueOf = valueOf;
    private requested = 0;

    constructor() {
        effect(() => {
            this.id();
            untracked(() => void this.load());
        });
        effect(() => this.sheet()?.open());
    }

    protected async load(): Promise<void> {
        const asked = ++this.requested;
        const id = this.id();
        if (this.loaded()?.id !== id)
            this.loaded.set(null);
        this.missing.set(false);
        this.error.set(null);
        if (!Number.isInteger(id) || id <= 0) {
            this.missing.set(true);
            return;
        }
        try {
            const shelf = await this.api.shelf(id);
            if (asked !== this.requested)
                return;
            this.loaded.set(shelf);
            this.store.put(shelf);
            await this.readable(shelf);
        } catch (error) {
            if (asked !== this.requested)
                return;
            if ((error as { status?: number }).status === 404)
                this.missing.set(true);
            else
                this.error.set(errorText(this.i18n, error));
        }
    }

    /** The address with the shelf's current name, in place of this one (no history entry). */
    private async readable(shelf: ShelfInfo): Promise<void> {
        const tree = this.router.createUrlTree(shelfLink(shelf), { queryParams: this.route.snapshot.queryParams });
        if (this.router.serializeUrl(tree) !== this.router.url)
            await this.router.navigateByUrl(tree, { replaceUrl: true });
    }

    protected startRename(shelf: ShelfInfo): void {
        this.name.set(shelf.name);
        this.renameError.set(null);
        this.renaming.set(true);
    }

    protected async rename(event: Event, shelf: ShelfInfo): Promise<void> {
        event.preventDefault();
        const name = this.name().trim();
        if (!name)
            return;
        this.busy.set(true);
        this.renameError.set(null);
        try {
            const renamed = await this.api.renameShelf(shelf.id, name);
            this.loaded.set(renamed);
            this.store.put(renamed);
            this.sheet()?.close();
            await this.readable(renamed);
        } catch (error) {
            this.renameError.set(errorText(this.i18n, error));
        } finally {
            this.busy.set(false);
        }
    }

    protected async remove(shelf: ShelfInfo): Promise<void> {
        const ok = await this.confirm.ask({
            title: this.i18n.t('shelf.deleteTitle', { name: shelf.name }),
            message: this.i18n.t('shelf.deleteMessage', { count: this.i18n.number(shelf.bookCount) }),
            action: this.i18n.t('common.delete'),
            danger: true,
        });
        if (!ok)
            return;
        this.busy.set(true);
        try {
            await this.api.deleteShelf(shelf.id);
            this.store.remove(shelf.id);
            this.toasts.show(this.i18n.t('shelf.deleted', { name: shelf.name }));
            await this.router.navigateByUrl('/shelves');
        } catch (error) {
            this.toasts.error(errorText(this.i18n, error));
        } finally {
            this.busy.set(false);
        }
    }
}
