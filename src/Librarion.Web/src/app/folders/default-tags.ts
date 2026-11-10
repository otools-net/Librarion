import { ChangeDetectionStrategy, Component, OnInit, computed, effect, inject, input, output, signal, viewChild } from '@angular/core';
import { Api } from '../core/api.service';
import { errorText } from '../core/errors';
import { I18n, TranslatePipe } from '../core/i18n.service';
import { DefaultTagItem, DefaultTagList, PickedTag } from '../core/models';
import { TaskWatch } from '../core/task-watch';
import { ToastService } from '../core/toast.service';
import { Icon } from '../shared/icon';
import { Sheet } from '../shared/sheet';
import { TagPicker } from '../shared/tag-picker';

/**
 * The default tags of a library or a folder (stage 6 chunk 8c; SFE's default tags in its scope dialog,
 * agents/docs/features/default-tags.md): its own with Remove, which asks whether the books there keep the tag; those
 * it inherits, read only, nearest first; a tag added with the shared picker, for new books only or also the books
 * already there. A big change runs in the background: the progress panel follows it.
 */
@Component({
    selector: 'lib-default-tags',
    imports: [TranslatePipe, Sheet, TagPicker, Icon],
    changeDetection: ChangeDetectionStrategy.OnPush,
    template: `
        <lib-sheet [heading]="'defaultTags.heading' | t" [subtitle]="place()" (closed)="closed.emit()">
            <p class="field-hint">{{ 'defaultTags.intro' | t }}</p>
            @if (list(); as list) {
                <section class="default-tags-own" [attr.aria-label]="'defaultTags.own' | t">
                    <h3 class="field-label">{{ 'defaultTags.own' | t }}</h3>
                    @if (list.own.length) {
                        <ul class="default-tag-rows">
                            @for (tag of list.own; track tag.tagId) {
                                <li class="default-tag-row">
                                    <span class="chip"><span class="tag-dot" [style.background]="tag.color"></span>{{ tag.name }}</span>
                                    @if (removing() === tag.tagId) {
                                        <span class="default-tag-ask" role="group" [attr.aria-label]="'defaultTags.askRemove' | t: { name: tag.name }">
                                            <span>{{ 'defaultTags.askRemove' | t: { name: tag.name } }}</span>
                                            <button type="button" class="button" [disabled]="busy()" (click)="remove(tag, true)">{{ 'defaultTags.removeFromBooks' | t }}</button>
                                            <button type="button" class="button" [disabled]="busy()" (click)="remove(tag, false)">{{ 'defaultTags.keepOnBooks' | t }}</button>
                                            <button type="button" class="button quiet" (click)="removing.set(null)">{{ 'common.cancel' | t }}</button>
                                        </span>
                                    } @else {
                                        <button type="button" class="icon-button" [attr.aria-label]="'defaultTags.remove' | t: { name: tag.name }"
                                                [title]="'defaultTags.remove' | t: { name: tag.name }" (click)="removing.set(tag.tagId)">
                                            <svg libIcon="trash"></svg>
                                        </button>
                                    }
                                </li>
                            }
                        </ul>
                    } @else {
                        <p class="field-hint">{{ 'defaultTags.none' | t }}</p>
                    }
                </section>
                @if (list.inherited.length) {
                    <section [attr.aria-label]="'defaultTags.inherited' | t">
                        <h3 class="field-label">{{ 'defaultTags.inherited' | t }}</h3>
                        <ul class="tag-chips">
                            @for (tag of list.inherited; track tag.tagId + '-' + tag.folderId) {
                                <li class="chip" [title]="from(tag)"><span class="tag-dot" [style.background]="tag.color"></span>{{ tag.name }}
                                    <span class="chip-note">{{ from(tag) }}</span></li>
                            }
                        </ul>
                    </section>
                }
                <form id="default-tags-form" class="default-tags-add" novalidate (submit)="add($event)">
                    <lib-tag-picker inputId="default-tag-add" [label]="'defaultTags.add' | t" [allowCreate]="true" [exclude]="ownIds()"
                                    (picked)="pick($event)" (create)="create($event)" />
                    @if (chosen(); as chosen) {
                        <p class="default-tags-chosen"><span class="chip"><span class="tag-dot" [style.background]="chosen.color"></span>{{ chosen.name }}</span></p>
                        <fieldset class="default-tags-when">
                            <legend class="field-label">{{ 'defaultTags.when' | t }}</legend>
                            <label><input type="radio" name="default-tags-when" [checked]="!existing()" (change)="existing.set(false)"> {{ 'defaultTags.newOnly' | t }}</label>
                            <label><input type="radio" name="default-tags-when" [checked]="existing()" (change)="existing.set(true)">
                                {{ (books() === 1 ? 'defaultTags.alsoThere.one' : 'defaultTags.alsoThere') | t: { count: i18n.number(books()) } }}</label>
                        </fieldset>
                    }
                </form>
            } @else if (!error()) {
                <p class="field-hint" role="status">{{ 'defaultTags.loading' | t }}</p>
            }
            @if (error(); as error) {
                <p class="form-error" role="alert">{{ error }}</p>
            }
            <div sheetFooter class="sheet-actions">
                <button type="button" class="button" [title]="'common.closeHint' | t" (click)="sheet()?.close()">{{ 'common.close' | t }}</button>
                <button type="submit" form="default-tags-form" class="button primary" [disabled]="busy() || !chosen()">{{ 'defaultTags.addButton' | t }}</button>
            </div>
        </lib-sheet>
    `,
})
export class DefaultTags implements OnInit {
    private readonly api = inject(Api);
    protected readonly i18n = inject(I18n);
    private readonly toasts = inject(ToastService);
    private readonly tasks = inject(TaskWatch);

    readonly library = input.required<number>();
    /** The folder; null for the library's own default tags. */
    readonly folder = input<number | null>(null);
    /** The library's or folder's name, under the heading. */
    readonly place = input.required<string>();
    /** How many files are there (the folder's, with those under it; the library's books): what "also those there" reaches. */
    readonly books = input(0);
    readonly closed = output<void>();
    /** The own default tags changed (the page's badge and line follow). */
    readonly changed = output<void>();

    protected readonly sheet = viewChild(Sheet);
    protected readonly list = signal<DefaultTagList | null>(null);
    protected readonly chosen = signal<{ id: number | null; name: string; color: string | null } | null>(null);
    protected readonly existing = signal(false);
    protected readonly removing = signal<number | null>(null);
    protected readonly busy = signal(false);
    protected readonly error = signal<string | null>(null);
    protected readonly ownIds = computed(() => this.list()?.own.map(tag => tag.tagId) ?? []);

    constructor() {
        effect(() => this.sheet()?.open());
    }

    async ngOnInit(): Promise<void> {
        await this.load();
    }

    protected from(tag: DefaultTagItem): string {
        return tag.from === null ? this.i18n.t('defaultTags.fromLibrary') : this.i18n.t('defaultTags.from', { name: tag.from });
    }

    protected pick(tag: PickedTag): void {
        this.chosen.set({ id: tag.id, name: tag.name, color: tag.color });
    }

    protected create(name: string): void {
        if (name.trim())
            this.chosen.set({ id: null, name: name.trim(), color: null });
    }

    protected async add(event: Event): Promise<void> {
        event.preventDefault();
        const chosen = this.chosen();
        if (!chosen)
            return;
        await this.work(() => this.api.addDefaultTag(this.library(), {
            folder: this.folder(), tag: chosen.id !== null ? { id: chosen.id } : { name: chosen.name }, existing: this.existing(),
        }), 'defaultTags.added', chosen.name);
        this.chosen.set(null);
        this.existing.set(false);
    }

    protected async remove(tag: DefaultTagItem, fromBooks: boolean): Promise<void> {
        await this.work(() => this.api.removeDefaultTag(this.library(), tag.tagId, this.folder(), fromBooks), 'defaultTags.removed', tag.name);
        this.removing.set(null);
    }

    private async work(call: () => Promise<{ runId: number | null; links: number }>, done: string, name: string): Promise<void> {
        this.busy.set(true);
        this.error.set(null);
        try {
            const work = await call();
            if (work.runId !== null) {
                this.tasks.follow(work.runId);
                this.toasts.show(this.i18n.t('defaultTags.running', { name }));
            } else {
                this.toasts.show(this.i18n.t(done, { name, count: this.i18n.number(work.links) }));
            }
            this.changed.emit();
            await this.load();
        } catch (error) {
            this.error.set(errorText(this.i18n, error));
        } finally {
            this.busy.set(false);
        }
    }

    private async load(): Promise<void> {
        try {
            this.list.set(await this.api.defaultTags(this.library(), this.folder()));
        } catch (error) {
            this.error.set(errorText(this.i18n, error));
        }
    }
}
