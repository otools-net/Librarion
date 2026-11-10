import { ChangeDetectionStrategy, Component, DestroyRef, ElementRef, inject, signal, viewChild } from '@angular/core';
import { NavigationEnd, Router } from '@angular/router';
import { filter } from 'rxjs';
import { Api } from '../core/api.service';
import { I18n, TranslatePipe } from '../core/i18n.service';
import { LibraryContext } from '../core/library-context';
import { SearchSuggestion } from '../core/models';
import { Icon } from '../shared/icon';
import { FieldCatalog } from '../tags/field-catalog';

/** How long typing waits before suggestions are asked. */
const SUGGEST_DELAY = 150;

/**
 * The top bar's search (agents/docs/features/search.md): Enter opens the results (`search?q=`); while typing, the
 * last term's suggestions (fields, then authors, series, tags… by name, the most used in the library first) in a
 * list under the box, chosen with the arrows and Enter, or a tap. A field chosen asks for its values at once.
 */
@Component({
    selector: 'lib-search-box',
    imports: [TranslatePipe, Icon],
    changeDetection: ChangeDetectionStrategy.OnPush,
    host: { class: 'search', '(document:pointerdown)': 'pointerDown($event)' },
    template: `
        <form role="search" (submit)="$event.preventDefault(); submit()">
            <svg libIcon="search"></svg>
            <input #box type="search" name="q" autocomplete="off" enterkeyhint="search" spellcheck="false"
                   role="combobox" aria-autocomplete="list" aria-controls="search-suggestions" [attr.aria-expanded]="open()"
                   [attr.aria-activedescendant]="active() >= 0 ? 'search-option-' + active() : null"
                   [attr.aria-label]="'shell.search.label' | t" [placeholder]="'shell.search.placeholder' | t" [title]="'shell.search.hint' | t"
                   [value]="text()" (input)="typed($event)" (keydown)="key($event)" (focus)="suggestSoon()">
        </form>
        @if (open()) {
            <ul id="search-suggestions" class="search-suggestions" role="listbox" [attr.aria-label]="'shell.search.suggestions' | t">
                @for (item of items(); track item.insert; let i = $index) {
                    <li [id]="'search-option-' + i" role="option" [attr.aria-selected]="i === active()" [class.active]="i === active()"
                        (mousedown)="$event.preventDefault()" (click)="choose(item)">
                        @if (item.kind === 'special') {
                            <svg [libIcon]="item.name === 'duplicates' ? 'copy' : 'tag'"></svg>
                            <span class="suggestion-name">{{ 'search.special.' + item.name | t }}</span>
                            <span class="suggestion-kind">{{ item.insert }}</span>
                        } @else if (item.kind === 'field') {
                            <svg [libIcon]="catalog.icon(item.name)"></svg>
                            <span class="suggestion-name">{{ catalog.one(item.name) }}</span>
                            <span class="suggestion-kind">{{ item.insert }}</span>
                        } @else {
                            <svg [libIcon]="catalog.icon(item.field)"></svg>
                            <span class="suggestion-name">{{ catalog.value(item.field, item.name) }}</span>
                            <span class="suggestion-kind">{{ item.field ? catalog.one(item.field) : ('tags.one' | t) }}</span>
                            @if (item.bookCount != null) {
                                <span class="suggestion-count">{{ i18n.number(item.bookCount) }}</span>
                            }
                        }
                    </li>
                }
            </ul>
        }
    `,
})
export class SearchBox {
    private readonly api = inject(Api);
    protected readonly i18n = inject(I18n);
    protected readonly catalog = inject(FieldCatalog);
    private readonly router = inject(Router);
    private readonly libraries = inject(LibraryContext);
    private readonly host = inject<ElementRef<HTMLElement>>(ElementRef).nativeElement;
    private readonly box = viewChild.required<ElementRef<HTMLInputElement>>('box');

    protected readonly text = signal('');
    protected readonly items = signal<SearchSuggestion[]>([]);
    protected readonly open = signal(false);
    protected readonly active = signal(-1);
    /** Where the term the suggestions are for starts in the text. */
    private from = 0;
    private timer: ReturnType<typeof setTimeout> | undefined;
    private asked = 0;

    constructor() {
        // The results page's search stays in the box; elsewhere the box keeps what was typed.
        const follow = () => {
            const url = this.router.parseUrl(this.router.url);
            if (url.root.children['primary']?.segments[0]?.path === 'search')
                this.text.set(url.queryParams['q'] ?? '');
        };
        follow();
        const subscription = this.router.events.pipe(filter(event => event instanceof NavigationEnd)).subscribe(() => {
            follow();
            this.close();
        });
        inject(DestroyRef).onDestroy(() => {
            subscription.unsubscribe();
            clearTimeout(this.timer);
        });
    }

    protected typed(event: Event): void {
        this.text.set((event.target as HTMLInputElement).value);
        this.suggestSoon();
    }

    protected suggestSoon(): void {
        void this.catalog.load();
        clearTimeout(this.timer);
        this.timer = setTimeout(() => void this.suggest(), SUGGEST_DELAY);
    }

    protected key(event: KeyboardEvent): void {
        const count = this.items().length;
        switch (event.key) {
            case 'ArrowDown':
            case 'ArrowUp':
                if (!this.open() || !count)
                    return;
                event.preventDefault();
                this.active.update(i => event.key === 'ArrowDown' ? (i + 1) % count : (i <= 0 ? count : i) - 1);
                return;
            case 'Enter':
                if (this.open() && this.active() >= 0) {
                    event.preventDefault();
                    this.choose(this.items()[this.active()]);
                }
                return;
            case 'Escape':
                if (this.open()) {
                    event.preventDefault();
                    this.close();
                }
                return;
        }
    }

    /** The suggestion in place of the term being typed; a value or a special term ends the term, a field waits for its value. */
    protected choose(item: SearchSuggestion): void {
        const text = this.text().slice(0, this.from) + item.insert + (item.kind === 'field' ? '' : ' ');
        this.text.set(text);
        const box = this.box().nativeElement;
        box.value = text;
        box.focus();
        box.setSelectionRange(text.length, text.length);
        this.close();
        if (item.kind === 'field')
            this.suggestSoon();
    }

    protected submit(): void {
        const q = this.text().trim();
        if (!q)
            return;
        clearTimeout(this.timer);
        this.asked++;
        this.close();
        // The results in view: a phone's keyboard goes down.
        this.box().nativeElement.blur();
        void this.router.navigate(['/search'], { queryParams: { q } });
    }

    /** A press outside the box and its list closes the list (SFE: `document:pointerdown`). */
    protected pointerDown(event: PointerEvent): void {
        if (this.open() && !this.host.contains(event.target as Node))
            this.close();
    }

    private async suggest(): Promise<void> {
        const text = this.text();
        const asked = ++this.asked;
        if (!text.trim() || document.activeElement !== this.box().nativeElement) {
            this.close();
            return;
        }
        try {
            const list = await this.api.suggestions(text, this.libraries.current()?.id);
            if (asked !== this.asked)
                return;
            this.from = list.from;
            this.items.set(list.items);
            this.active.set(-1);
            this.open.set(list.items.length > 0);
        } catch {
            // Suggestions are a help: without them the search still works.
            if (asked === this.asked)
                this.close();
        }
    }

    private close(): void {
        this.open.set(false);
        this.active.set(-1);
    }
}
