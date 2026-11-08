import { ChangeDetectionStrategy, Component, OnInit, computed, effect, inject, signal, untracked } from '@angular/core';
import { RouterLink } from '@angular/router';
import { Api, coverUrl } from '../core/api.service';
import { errorText, valueOf } from '../core/errors';
import { I18n, TranslatePipe } from '../core/i18n.service';
import { LibraryContext } from '../core/library-context';
import { BookDetail, BookListItem, DuplicateGroupItem } from '../core/models';
import { TaskWatch } from '../core/task-watch';
import { ToastService } from '../core/toast.service';
import { BookCompare } from '../books/book-compare';
import { ConfirmService } from '../shared/confirm';

const LEVELS = ['', 'file', 'identifier', 'work'] as const;

/**
 * Administration › Catalog › Duplicates (stage 6 chunk 12, `library.merge`; agents/docs/features/merging.md#the-duplicates-page):
 * the possible duplicates of a library, the biggest groups first, each with why (the same file, identifier, or title
 * and author) and its books; Compare and merge opens them side by side, Not duplicates sets them apart for good.
 * Find again rebuilds the groups as a background run.
 */
@Component({
    selector: 'lib-duplicates-section',
    imports: [TranslatePipe, RouterLink, BookCompare],
    changeDetection: ChangeDetectionStrategy.OnPush,
    template: `
        @if (listError(); as error) {
            <p class="form-error" role="alert">{{ error }}</p>
        }
        @if (libraries().length) {
            <div class="toolbar">
                @if (libraries().length > 1) {
                    <label class="inline-field">
                        <span class="field-label">{{ 'duplicates.library' | t }}</span>
                        <select id="duplicates-library" (change)="chooseLibrary(+valueOf($event))">
                            @for (library of libraries(); track library.id) {
                                <option [value]="library.id" [selected]="library.id === library$()">{{ library.name }}</option>
                            }
                        </select>
                    </label>
                }
                <label class="inline-field">
                    <span class="field-label">{{ 'duplicates.level' | t }}</span>
                    <select id="duplicates-level" (change)="chooseLevel(valueOf($event))">
                        @for (level of LEVELS; track level) {
                            <option [value]="level" [selected]="level === level$()">{{ 'duplicates.level.' + (level || 'all') | t }}</option>
                        }
                    </select>
                </label>
                <button type="button" class="button" [title]="'duplicates.rebuildHint' | t" [disabled]="busy()" (click)="rebuild()">{{ 'duplicates.rebuild' | t }}</button>
            </div>
            @if (!built() && !loading()) {
                <p class="field-hint">{{ 'duplicates.notBuilt' | t }}</p>
            }
            <ul class="rows duplicate-groups" [attr.aria-label]="'duplicates.list' | t" [attr.aria-busy]="loading()">
                @for (group of groups(); track group.id) {
                    <li class="duplicate-group">
                        <p class="duplicate-reason">{{ reason(group) }}</p>
                        <ul class="duplicate-books">
                            @for (book of group.books; track book.id) {
                                <li>
                                    <a class="duplicate-book" [routerLink]="['/books', book.id]">
                                        <span class="book-cover"><img [src]="cover(book)" alt="" loading="lazy" decoding="async" width="80" height="120"></span>
                                        <span class="book-text">
                                            <span class="book-title">{{ title(book) }}</span>
                                            <span class="book-authors">{{ book.authors.join(', ') }}</span>
                                            <span class="book-formats">{{ book.formats.join(', ').toUpperCase() }}</span>
                                        </span>
                                    </a>
                                </li>
                            }
                        </ul>
                        @if (unshown(group); as count) {
                            <p class="field-hint">{{ 'duplicates.more' | t: { count } }}</p>
                        }
                        <div class="duplicate-actions">
                            <button type="button" class="button primary" [title]="'compare.openHint' | t" [disabled]="busy()" (click)="comparing.set(group)">{{ 'compare.open' | t }}</button>
                            <button type="button" class="button" [title]="'duplicates.notHint' | t" [disabled]="busy()" (click)="notDuplicates(group)">{{ 'duplicates.not' | t }}</button>
                        </div>
                    </li>
                } @empty {
                    @if (!loading() && built()) {
                        <li class="empty"><p class="empty-text">{{ 'duplicates.none' | t }}</p></li>
                    }
                }
            </ul>
            @if (next()) {
                <button type="button" class="button" [disabled]="loading()" (click)="load(true)">{{ 'tagAdmin.more' | t }}</button>
            }
        } @else if (!loading()) {
            <p class="empty-text">{{ 'duplicates.noLibrary' | t }}</p>
        }

        @if (comparing(); as group) {
            <lib-book-compare [bookIds]="ids(group)" (merged)="merged(group, $event)" (closed)="comparing.set(null)" />
        }
    `,
})
export class DuplicatesSection implements OnInit {
    private readonly i18n = inject(I18n);
    private readonly api = inject(Api);
    private readonly libraryContext = inject(LibraryContext);
    private readonly confirm = inject(ConfirmService);
    private readonly toasts = inject(ToastService);
    private readonly watch = inject(TaskWatch);

    protected readonly LEVELS = LEVELS;
    protected readonly valueOf = valueOf;
    private readonly allowed = signal<ReadonlySet<number>>(new Set());
    protected readonly libraries = computed(() => this.libraryContext.libraries().filter(l => this.allowed().has(l.id)));
    protected readonly library$ = signal<number | null>(null);
    protected readonly level$ = signal<typeof LEVELS[number]>('');
    protected readonly groups = signal<DuplicateGroupItem[]>([]);
    protected readonly next = signal<string | null>(null);
    protected readonly built = signal(true);
    protected readonly loading = signal(true);
    protected readonly busy = signal(false);
    protected readonly listError = signal<string | null>(null);
    protected readonly comparing = signal<DuplicateGroupItem | null>(null);

    private asked = 0;
    /** The rebuild this page started: the list is read again when it ends. */
    private waiting: number | null = null;

    constructor() {
        effect(() => {
            if (this.waiting !== null && this.watch.ended().some(run => run.id === this.waiting)) {
                this.waiting = null;
                untracked(() => void this.load(false));
            }
        });
    }

    async ngOnInit(): Promise<void> {
        try {
            const [context] = await Promise.all([this.api.duplicateContext(), this.libraryContext.libraries().length ? null : this.libraryContext.load()]);
            this.allowed.set(new Set(context.libraries));
        } catch (error) {
            this.listError.set(errorText(this.i18n, error));
            this.loading.set(false);
            return;
        }
        const current = this.libraryContext.current()?.id;
        const libraries = this.libraries();
        this.library$.set(libraries.find(l => l.id === current)?.id ?? libraries[0]?.id ?? null);
        await this.load(false);
    }

    protected chooseLibrary(id: number): void {
        this.library$.set(id);
        void this.load(false);
    }

    protected chooseLevel(level: string): void {
        this.level$.set(level as typeof LEVELS[number]);
        void this.load(false);
    }

    protected reason(group: DuplicateGroupItem): string {
        return group.level === 'identifier'
            ? this.i18n.t('book.reason.identifier', { scheme: group.scheme?.toUpperCase() ?? '' })
            : this.i18n.t(`book.reason.${group.level}`);
    }

    protected title(book: BookListItem): string {
        return book.title || book.fileName || this.i18n.t('browse.untitled');
    }

    protected cover(book: BookListItem): string {
        return coverUrl(book.id, 's', book.coverVersion);
    }

    /** Its books not shown (only the first few are). */
    protected unshown(group: DuplicateGroupItem): number {
        return group.bookIds.length - group.books.length;
    }

    protected ids(group: DuplicateGroupItem): number[] {
        return group.bookIds;
    }

    protected async load(more: boolean): Promise<void> {
        const library = this.library$();
        if (library === null) {
            this.loading.set(false);
            return;
        }
        const asked = ++this.asked;
        this.loading.set(true);
        this.listError.set(null);
        try {
            const page = await this.api.duplicates(library, this.level$() || null, more ? this.next() : null);
            if (asked !== this.asked)
                return;
            this.groups.set(more ? [...this.groups(), ...page.items] : page.items);
            this.next.set(page.next);
            this.built.set(page.built);
        } catch (error) {
            if (asked === this.asked)
                this.listError.set(errorText(this.i18n, error));
        } finally {
            if (asked === this.asked)
                this.loading.set(false);
        }
    }

    /** Merged: the group leaves the list (its books left out stay possible duplicates of others). */
    protected merged(group: DuplicateGroupItem, book: BookDetail): void {
        this.groups.update(groups => groups.filter(g => g.id !== group.id));
        this.toasts.show(this.i18n.t('duplicates.merged', { title: book.title || book.fileName || '' }));
    }

    protected async notDuplicates(group: DuplicateGroupItem): Promise<void> {
        const ok = await this.confirm.ask({
            title: this.i18n.t('duplicates.notTitle'),
            message: this.i18n.t('duplicates.notConfirm', { count: group.bookIds.length }),
            action: this.i18n.t('duplicates.not'),
        });
        if (!ok)
            return;
        this.busy.set(true);
        try {
            await this.api.notDuplicates(this.ids(group));
            this.groups.update(groups => groups.filter(g => g.id !== group.id));
        } catch (error) {
            this.listError.set(errorText(this.i18n, error));
        } finally {
            this.busy.set(false);
        }
    }

    protected async rebuild(): Promise<void> {
        const ok = await this.confirm.ask({
            title: this.i18n.t('duplicates.rebuildTitle'),
            message: this.i18n.t('duplicates.rebuildConfirm'),
            action: this.i18n.t('duplicates.rebuild'),
        });
        if (!ok)
            return;
        this.busy.set(true);
        try {
            const { runId } = await this.api.buildDuplicates();
            this.waiting = runId;
            this.watch.follow(runId);
            this.toasts.show(this.i18n.t('duplicates.rebuilding'));
        } catch (error) {
            this.listError.set(errorText(this.i18n, error));
        } finally {
            this.busy.set(false);
        }
    }
}
