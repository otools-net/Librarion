import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal, untracked, viewChild } from '@angular/core';
import { RouterLink } from '@angular/router';
import { Api } from '../core/api.service';
import { I18n, TranslatePipe } from '../core/i18n.service';
import { FacetList, FacetValue } from '../core/models';
import { Icon, IconName } from '../shared/icon';
import { Sheet } from '../shared/sheet';
import { FieldCatalog } from '../tags/field-catalog';

/** The terms of a search, split on the spaces outside quotes (as the server reads them). */
export function searchTerms(query: string): string[] {
    const terms: string[] = [];
    let current = '';
    let quoted = false;
    for (const char of query) {
        if (char === '"')
            quoted = !quoted;
        if (/\s/.test(char) && !quoted) {
            if (current)
                terms.push(current);
            current = '';
        } else {
            current += char;
        }
    }
    if (current)
        terms.push(current);
    return terms;
}

/**
 * Refine (D-5.8, agents/docs/features/search.md): a button in the list's tools opening a sheet with each field's most
 * frequent values among the books of the library or the search, with counts (`api/search/facets`). A value narrows
 * the search to its books, its minus leaves them out; a value the search already has is marked and takes it away.
 * The counts are asked again whenever the library or the search changes; `list` is there for the page's count.
 */
@Component({
    selector: 'lib-facets',
    imports: [TranslatePipe, RouterLink, Icon, Sheet],
    changeDetection: ChangeDetectionStrategy.OnPush,
    template: `
        <button type="button" class="button facets-button" [disabled]="!fields().length" [title]="'facets.hint' | t" (click)="sheet().open()">
            <svg libIcon="filter"></svg><span>{{ 'facets.button' | t }}</span>
        </button>
        <lib-sheet [heading]="'facets.title' | t" [subtitle]="subtitle()" [wide]="true">
            <div class="facets">
                @for (facet of fields(); track facet.field) {
                    <section class="facet" [attr.aria-label]="fieldTitle(facet.field)">
                        <h3 class="facet-title"><svg [libIcon]="icon(facet.field)"></svg>{{ fieldTitle(facet.field) }}</h3>
                        <ul class="facet-values">
                            @for (value of facet.values; track value.term) {
                                @let name = valueName(facet.field, value);
                                @let chosen = terms().includes(value.term);
                                <li class="facet-row">
                                    <a class="facet-value" [class.chosen]="chosen" [routerLink]="['/search']" [queryParams]="{ q: narrowed(value) }"
                                       [attr.aria-current]="chosen ? 'true' : null" (click)="sheet().close()"
                                       [title]="(chosen ? 'facets.removeHint' : 'facets.narrowHint') | t: { name: name }">
                                        @if (chosen) {
                                            <svg libIcon="check"></svg>
                                        }
                                        <span class="facet-name">{{ name }}</span>
                                        <span class="facet-count">{{ i18n.number(value.count) }}</span>
                                    </a>
                                    <a class="icon-button facet-exclude" [routerLink]="['/search']" [queryParams]="{ q: excluded(value) }" (click)="sheet().close()"
                                       [title]="'facets.excludeHint' | t: { name: name }" [attr.aria-label]="'facets.exclude' | t: { name: name }">
                                        <svg libIcon="minus"></svg>
                                    </a>
                                </li>
                            }
                        </ul>
                    </section>
                }
            </div>
        </lib-sheet>
    `,
})
export class Facets {
    private readonly api = inject(Api);
    protected readonly i18n = inject(I18n);
    private readonly catalog = inject(FieldCatalog);
    protected readonly sheet = viewChild.required(Sheet);

    readonly library = input.required<number>();
    /** The search shown; null or empty: the whole library. */
    readonly query = input<string | null>(null);

    /** The counts for the library and search shown; null while asked. */
    readonly list = signal<FacetList | null>(null);
    protected readonly fields = computed(() => this.list()?.fields ?? []);
    protected readonly terms = computed(() => searchTerms(this.query() ?? ''));
    protected readonly subtitle = computed(() => {
        const list = this.list();
        if (!list)
            return null;
        const count = this.i18n.number(list.counted);
        return this.i18n.t(list.capped ? 'facets.capped' : 'facets.counted', { count });
    });
    private requested = 0;

    constructor() {
        void this.catalog.load();
        effect(() => {
            const library = this.library();
            const query = this.query();
            untracked(() => void this.load(library, query));
        });
    }

    protected fieldTitle(field: string): string {
        if (field === 'format')
            return this.i18n.t('facets.formats');
        return field === 'tag' ? this.i18n.t('tags.title') : this.catalog.title(field);
    }

    protected icon(field: string): IconName {
        return field === 'format' ? 'book' : this.catalog.icon(field === 'tag' ? null : field);
    }

    protected valueName(field: string, value: FacetValue): string {
        return field === 'format' ? value.name.toUpperCase() : this.catalog.value(field, value.name);
    }

    /** The search with the value's term, or without it when it has it already. */
    protected narrowed(value: FacetValue): string {
        const terms = this.terms();
        const others = terms.filter(term => term !== value.term && term !== `-${value.term}`);
        return (terms.includes(value.term) ? others : [...others, value.term]).join(' ');
    }

    /** The search leaving the value's books out. */
    protected excluded(value: FacetValue): string {
        return [...this.terms().filter(term => term !== value.term && term !== `-${value.term}`), `-${value.term}`].join(' ');
    }

    private async load(library: number, query: string | null): Promise<void> {
        const asked = ++this.requested;
        this.list.set(null);
        try {
            const list = await this.api.facets(library, query);
            if (asked === this.requested)
                this.list.set(list);
        } catch {
            // Secondary to the list: without counts the button stays disabled.
        }
    }
}
