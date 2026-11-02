import {
    ChangeDetectionStrategy, Component, DestroyRef, ElementRef, afterRenderEffect, computed, effect, inject, signal, untracked, viewChild,
} from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { Api } from '../core/api.service';
import { errorText, valueOf } from '../core/errors';
import { I18n, TranslatePipe } from '../core/i18n.service';
import { LibraryContext } from '../core/library-context';
import { FieldValue } from '../core/models';
import { BrowseState } from '../browse/browse-state';
import { ListWindow } from '../browse/list-window';
import { Icon } from '../shared/icon';
import { EmptyState, Skeleton } from '../shared/placeholders';
import { FieldCatalog } from './field-catalog';

/** Values asked for at a time. */
const PAGE = 100;
/** How long typing waits before the list jumps. */
const JUMP_DELAY = 300;

/**
 * A field's values (`fields/:key`: authors, series…) or the plain tags (`tags`), A–Z by sort name, each with its
 * books in the library being browsed, those without any left out (agents/docs/features/browse.md). Pages by key as
 * the end nears, only the rows in view drawn (`ListWindow`); "Go to" starts the list at a name (`?from=`, kept in the
 * address so the way back finds it).
 */
@Component({
    selector: 'lib-values-page',
    imports: [TranslatePipe, Icon, EmptyState, Skeleton, RouterLink],
    changeDetection: ChangeDetectionStrategy.OnPush,
    host: { class: 'values-page' },
    template: `
        <header class="browse-head">
            <div class="browse-title">
                <h1><svg [libIcon]="catalog.icon(field())"></svg> {{ title() }}</h1>
                @if (library(); as library) {
                    <p class="browse-count">{{ 'values.in' | t: { name: library.name } }}</p>
                }
            </div>
            <form class="values-jump" role="search" (submit)="$event.preventDefault(); jumpNow()">
                <label>
                    <span class="visually-hidden">{{ 'values.jump' | t }}</span>
                    <input type="search" enterkeyhint="go" autocomplete="off" [placeholder]="'values.jumpHint' | t" [title]="'values.jumpTitle' | t"
                           [value]="from()" (input)="typed($event)">
                </label>
            </form>
        </header>

        @if (items().length) {
            <ul #list class="values" [attr.aria-label]="title()"
                [style.grid-template-columns]="'repeat(' + window.columns() + ', minmax(0, 1fr))'"
                [style.padding-top.px]="window.range().before" [style.padding-bottom.px]="window.range().after">
                @for (entry of shown(); track entry.value.id) {
                    @let value = entry.value;
                    <li class="value">
                        <a [routerLink]="['/tags', value.id]" [title]="name(value)">
                            <span class="value-name">{{ name(value) }}</span>
                            @if (value.bookCount != null) {
                                <span class="value-count" [attr.aria-label]="'browse.count' | t: { count: i18n.number(value.bookCount) }">{{ i18n.number(value.bookCount) }}</span>
                            }
                        </a>
                    </li>
                }
            </ul>
            @if (loading()) {
                <p class="browse-more" role="status">{{ 'values.loadingMore' | t }}</p>
            }
        } @else if (error(); as error) {
            <p class="form-error" role="alert">{{ error }}</p>
        } @else if (loading() || !loaded() || next()) {
            <lib-skeleton [rows]="8" label="values.loading" />
        } @else if (unknown()) {
            <p class="empty large" libEmpty="tag">{{ 'values.noField' | t }}</p>
        } @else {
            <p class="empty large" [libEmpty]="catalog.icon(field())">{{ (from() ? 'values.emptyFrom' : 'values.empty') | t: { from: from() } }}</p>
        }
        @if (error() && items().length) {
            <p class="form-error" role="alert">{{ error() }} <button type="button" class="button" (click)="more()">{{ 'common.retry' | t }}</button></p>
        }
    `,
})
export class ValuesPage {
    private readonly api = inject(Api);
    protected readonly i18n = inject(I18n);
    protected readonly catalog = inject(FieldCatalog);
    private readonly state = inject(BrowseState);
    private readonly router = inject(Router);
    private readonly route = inject(ActivatedRoute);
    private readonly libraries = inject(LibraryContext);
    private readonly list = viewChild<ElementRef<HTMLElement>>('list');

    private readonly params = toSignal(this.route.paramMap, { initialValue: this.route.snapshot.paramMap });
    private readonly query = toSignal(this.route.queryParamMap, { initialValue: this.route.snapshot.queryParamMap });
    /** The field's key; null: the plain tags. */
    protected readonly field = computed(() => this.params().get('key'));
    protected readonly from = computed(() => this.query().get('from') ?? '');
    protected readonly library = this.libraries.current;
    protected readonly title = computed(() => {
        this.catalog.fields();
        const field = this.field();
        return field ? this.catalog.title(field) : this.i18n.t('tags.title');
    });

    protected readonly items = signal<FieldValue[]>([]);
    private readonly next = signal<string | null>(null);
    protected readonly loading = signal(false);
    protected readonly loaded = signal(false);
    protected readonly error = signal<string | null>(null);
    protected readonly unknown = signal(false);
    private readonly key = computed(() => `values|${this.field() ?? ''}|${this.library()?.id ?? ''}|${this.from()}`);

    protected readonly window = new ListWindow({
        element: () => this.list()?.nativeElement,
        count: () => this.items().length,
        grid: () => true,
        minVar: '--value-min',
        minDefault: 240,
        item: '.value',
    });
    protected readonly shown = computed(() => {
        const { start, end } = this.window.range();
        return this.items().slice(start, end).map((value, i) => ({ value, index: start + i }));
    });
    private requested = 0;
    private jumpTimer: ReturnType<typeof setTimeout> | undefined;
    private pending = '';

    constructor() {
        void this.catalog.load();
        void this.libraries.load();

        effect(() => {
            const key = this.key();
            untracked(() => this.start(key));
        });

        // Near the end of what is loaded (or a short page that read nothing to show): the next page.
        effect(() => {
            const { end } = this.window.range();
            const count = this.items().length;
            if (this.next() && end >= count - this.window.perRow() * 3 && !untracked(this.loading) && !untracked(this.error))
                untracked(() => void this.more());
        });

        afterRenderEffect(() => {
            this.shown();
            this.window.measure();
        });

        inject(DestroyRef).onDestroy(() => {
            clearTimeout(this.jumpTimer);
            if (this.items().length)
                this.state.keep({ key: this.key(), items: this.items(), next: this.next(), offset: this.window.offset() });
        });
    }

    protected name(value: FieldValue): string {
        return this.catalog.value(this.field(), value.name);
    }

    protected typed(event: Event): void {
        this.pending = valueOf(event);
        clearTimeout(this.jumpTimer);
        this.jumpTimer = setTimeout(() => this.jumpNow(), JUMP_DELAY);
    }

    /** The list from the name typed, the address keeping it (replaced: typing isn't a step back). */
    protected jumpNow(): void {
        clearTimeout(this.jumpTimer);
        const from = this.pending.trim();
        if (from === this.from())
            return;
        void this.router.navigate([], { relativeTo: this.route, queryParams: { from: from || null }, replaceUrl: true });
    }

    /** The next page, by key. */
    protected async more(): Promise<void> {
        if (this.loading())
            return;
        const asked = this.requested;
        this.loading.set(true);
        this.error.set(null);
        try {
            const page = await this.api.tagValues(this.field(), {
                library: this.library()?.id, from: this.from(), after: this.next(), limit: PAGE,
            });
            if (asked !== this.requested)
                return;
            this.items.update(items => [...items, ...page.items]);
            this.next.set(page.next);
        } catch (error) {
            if (asked !== this.requested)
                return;
            if ((error as { status?: number }).status === 404 && !this.items().length)
                this.unknown.set(true);
            else
                this.error.set(errorText(this.i18n, error));
        } finally {
            if (asked === this.requested) {
                this.loading.set(false);
                this.loaded.set(true);
            }
        }
    }

    private start(key: string): void {
        this.requested++;
        this.loading.set(false);
        this.error.set(null);
        this.unknown.set(false);
        this.pending = this.from();
        const kept = this.state.restore<FieldValue>(key);
        if (kept) {
            this.items.set(kept.items);
            this.next.set(kept.next);
            this.loaded.set(true);
            this.window.restore(kept.offset);
            return;
        }
        this.items.set([]);
        this.next.set(null);
        this.loaded.set(false);
        this.window.restore(null);
        scrollTo({ top: 0 });
        void this.more();
    }
}
