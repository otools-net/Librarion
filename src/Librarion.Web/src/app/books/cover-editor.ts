import { ChangeDetectionStrategy, Component, OnInit, computed, effect, inject, input, output, signal, viewChild } from '@angular/core';
import { Api, coverPreviewUrl, coverUrl } from '../core/api.service';
import { errorText } from '../core/errors';
import { I18n, TranslatePipe } from '../core/i18n.service';
import { BookDetail, CoverStyles } from '../core/models';
import { Sheet } from '../shared/sheet';

/** The largest picture sent (`CoverUploads.MaxBytes`): a bigger one is refused here, before it is sent. */
const MAX_BYTES = 5 * 1024 * 1024;

/**
 * A book's cover chosen on its page (stage 6 chunk 10, `library.edit`; agents/docs/features/book-display.md): a picture
 * uploaded (JPEG, PNG or WebP), a generated style picked from previews of the book in each template and palette, or
 * back to the book's own cover. Each choice applies at once and is kept through re-imports.
 */
@Component({
    selector: 'lib-cover-editor',
    imports: [TranslatePipe, Sheet],
    changeDetection: ChangeDetectionStrategy.OnPush,
    template: `
        <lib-sheet [heading]="'cover.heading' | t" [wide]="true" (closed)="closed.emit()">
            <div class="cover-editor">
                <div class="cover-editor-now">
                    <span class="book-page-cover"><img [src]="current()" alt="" width="600" height="900" decoding="async"></span>
                    <p class="field-hint">{{ ('cover.source.' + book().coverSource) | t }}</p>
                </div>
                <div class="cover-editor-choices">
                    <div class="cover-editor-actions">
                        <label class="button primary cover-upload" [class.disabled]="busy()">
                            {{ 'cover.upload' | t }}
                            <input type="file" accept="image/jpeg,image/png,image/webp" [disabled]="busy()" (change)="upload($event)">
                        </label>
                        @if (book().cover?.chosen) {
                            <button type="button" class="button" [title]="'cover.ownHint' | t" [disabled]="busy()" (click)="own()">{{ 'cover.own' | t }}</button>
                        }
                    </div>
                    <p class="field-hint">{{ 'cover.uploadHint' | t }}</p>
                    @if (error(); as error) {
                        <p class="form-error" role="alert">{{ error }}</p>
                    }
                    <h3 class="cover-styles-title">{{ 'cover.styles' | t }}</h3>
                    @if (styles(); as styles) {
                        @for (template of styles.templates; track template; let t = $index) {
                            @if (offered(t)) {
                                <div class="cover-style-row" role="group" [attr.aria-label]="'cover.template.' + template | t">
                                    <span class="cover-style-name">{{ 'cover.template.' + template | t }}</span>
                                    <div class="cover-style-options">
                                        @for (variant of palettes(); track variant) {
                                            <button type="button" class="cover-style" [attr.aria-pressed]="isCurrent(t, variant)" [disabled]="busy()"
                                                    [attr.aria-label]="'cover.styleLabel' | t: { template: ('cover.template.' + template | t), palette: variant + 1 }"
                                                    (click)="style(t, variant)">
                                                <img [src]="preview(t, variant)" alt="" width="80" height="120" loading="lazy" decoding="async">
                                            </button>
                                        }
                                    </div>
                                </div>
                            }
                        }
                    }
                </div>
            </div>
        </lib-sheet>
    `,
})
export class CoverEditor implements OnInit {
    private readonly api = inject(Api);
    private readonly i18n = inject(I18n);

    readonly book = input.required<BookDetail>();
    /** The book after each change. */
    readonly changed = output<BookDetail>();
    readonly closed = output<void>();

    private readonly sheet = viewChild(Sheet);
    protected readonly styles = signal<CoverStyles | null>(null);
    protected readonly busy = signal(false);
    protected readonly error = signal<string | null>(null);
    protected readonly palettes = computed(() => [...Array(this.styles()?.palettes ?? 0).keys()]);
    protected readonly current = computed(() => coverUrl(this.book().id, 'l', this.book().coverVersion));

    constructor() {
        effect(() => this.sheet()?.open());
    }

    async ngOnInit(): Promise<void> {
        try {
            this.styles.set(await this.api.coverStyles());
        } catch (error) {
            this.error.set(errorText(this.i18n, error));
        }
    }

    protected preview(template: number, variant: number): string {
        return coverPreviewUrl(this.book().id, template, variant, this.book().coverVersion);
    }

    /** A template the tenant has on, or the book's own (Cover styles, stage 6 chunk 10b). */
    protected offered(template: number): boolean {
        return !this.styles()?.off.includes(template) || this.book().cover?.template === template;
    }

    /** The style the book shows now: only when its cover is a generated one. */
    protected isCurrent(template: number, variant: number): boolean {
        const cover = this.book().cover;
        return this.book().coverSource === 'generated' && cover?.template === template && cover.variant === variant;
    }

    protected async upload(event: Event): Promise<void> {
        const input = event.target as HTMLInputElement;
        const file = input.files?.[0];
        input.value = '';
        if (!file)
            return;
        if (file.size > MAX_BYTES) {
            this.error.set(this.i18n.t('error.cover.tooBig'));
            return;
        }
        await this.apply(() => this.api.uploadCover(this.book().id, file));
    }

    protected style(template: number, variant: number): Promise<void> {
        return this.apply(() => this.api.coverStyle(this.book().id, template, variant));
    }

    protected own(): Promise<void> {
        return this.apply(() => this.api.useOwnCover(this.book().id));
    }

    private async apply(change: () => Promise<BookDetail>): Promise<void> {
        this.busy.set(true);
        this.error.set(null);
        try {
            this.changed.emit(await change());
        } catch (error) {
            this.error.set(errorText(this.i18n, error));
        } finally {
            this.busy.set(false);
        }
    }
}
