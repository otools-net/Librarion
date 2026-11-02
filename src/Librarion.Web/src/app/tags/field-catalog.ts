import { Injectable, computed, inject, signal } from '@angular/core';
import { Api } from '../core/api.service';
import { I18n } from '../core/i18n.service';
import { FieldInfo } from '../core/models';
import { SessionService } from '../core/session.service';
import { IconName } from '../shared/icon';

const ICONS: Record<string, IconName> = {
    author: 'author', translator: 'author', editor: 'author', illustrator: 'author', series: 'series', category: 'category',
    publisher: 'publisher', language: 'language', year: 'calendar',
};

/**
 * The tenant's fields (`api/fields`), asked once per tenant, and how a field and its values are named
 * (agents/docs/features/browse.md): built-in fields by their key's texts (`fields.name.author` "Authors",
 * `fields.one.author` "Author"), others by their own name; a `language:` value by the language's name.
 */
@Injectable({ providedIn: 'root' })
export class FieldCatalog {
    private readonly api = inject(Api);
    private readonly i18n = inject(I18n);
    private readonly session = inject(SessionService);
    private readonly list = signal<FieldInfo[]>([]);
    private loadedFor: number | null | undefined;
    private loading: Promise<void> | null = null;

    readonly fields = this.list.asReadonly();
    readonly loaded = signal(false);
    private readonly tenant = computed(() => this.session.session()?.tenant?.id ?? null);

    /** Asks the server unless the fields of this tenant are known. */
    load(): Promise<void> {
        const tenant = this.tenant();
        if (tenant === this.loadedFor)
            return this.loading ?? Promise.resolve();
        this.loadedFor = tenant;
        this.loading = this.api.fields().then(
            fields => this.list.set(fields),
            () => this.list.set([]),
        ).finally(() => this.loaded.set(true));
        return this.loading;
    }

    find(key: string): FieldInfo | undefined {
        return this.list().find(field => field.key === key);
    }

    /** The field's name as a page title: "Authors"; a field not known by its key. */
    title(key: string): string {
        const field = this.find(key);
        return field && !field.builtIn ? field.name : this.text(`fields.name.${key}`, field?.name ?? key);
    }

    /** One of its values: "Author"; `name` for a field this client doesn't know. */
    one(key: string, name?: string | null): string {
        const field = this.find(key);
        return field && !field.builtIn ? field.name : this.text(`fields.one.${key}`, name ?? field?.name ?? key);
    }

    icon(key: string | null): IconName {
        return key ? (ICONS[key] ?? 'tag') : 'tag';
    }

    /** A value as shown: a language by its name in the UI language ("ru" → "Russian"), the others as they are. */
    value(field: string | null, name: string): string {
        if (field === 'language' && /^[a-z]{2,3}(-[A-Za-z0-9]+)*$/.test(name)) {
            try {
                return new Intl.DisplayNames([this.i18n.language()], { type: 'language' }).of(name) ?? name;
            } catch {
                return name;
            }
        }
        return name;
    }

    private text(key: string, fallback: string): string {
        const text = this.i18n.t(key);
        return text === key ? fallback : text;
    }
}
