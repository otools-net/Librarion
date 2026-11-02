import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { RouterLink } from '@angular/router';
import { TranslatePipe } from '../core/i18n.service';
import { Icon } from '../shared/icon';
import { Skeleton } from '../shared/placeholders';
import { FieldCatalog } from './field-catalog';

/**
 * Every way to browse by value (`fields`; agents/docs/features/browse.md): the tenant's fields (built-in ones first,
 * then custom ones such as imported columns) and the plain tags, each opening its A–Z list.
 */
@Component({
    selector: 'lib-fields-page',
    imports: [TranslatePipe, Icon, Skeleton, RouterLink],
    changeDetection: ChangeDetectionStrategy.OnPush,
    host: { class: 'fields-page' },
    template: `
        <header class="browse-head">
            <div class="browse-title">
                <h1>{{ 'fields.title' | t }}</h1>
                <p class="browse-count">{{ 'fields.intro' | t }}</p>
            </div>
        </header>
        @if (catalog.loaded()) {
            <ul class="field-cards">
                @for (field of catalog.fields(); track field.id) {
                    <li>
                        <a class="field-card" [routerLink]="['/fields', field.key]">
                            <svg [libIcon]="catalog.icon(field.key)"></svg>
                            <span class="field-card-name">{{ catalog.title(field.key) }}</span>
                            @if (!field.builtIn) {
                                <span class="field-card-key">{{ field.key }}</span>
                            }
                        </a>
                    </li>
                }
                <li>
                    <a class="field-card" routerLink="/tags">
                        <svg libIcon="tag"></svg>
                        <span class="field-card-name">{{ 'tags.title' | t }}</span>
                    </a>
                </li>
            </ul>
        } @else {
            <lib-skeleton [rows]="4" label="fields.loading" />
        }
    `,
})
export class FieldsPage {
    protected readonly catalog = inject(FieldCatalog);

    constructor() {
        void this.catalog.load();
    }
}
