import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { Api, coverSampleUrl } from '../core/api.service';
import { errorText } from '../core/errors';
import { I18n, TranslatePipe } from '../core/i18n.service';
import { CoverTemplateInfo, CoverTemplates } from '../core/models';
import { TaskWatch } from '../core/task-watch';
import { ToastService } from '../core/toast.service';
import { ConfirmService } from '../shared/confirm';

/**
 * Administration › Catalog › Cover styles (stage 6 chunk 10b, `tenant.coverStyles`; agents/docs/features/book-display.md#cover-styles):
 * each drawn cover template with a sample in every palette and a switch for new books (one stays on). Turning one off
 * changes no book; its books are given another of those on by a background run, asked for with a button.
 */
@Component({
    selector: 'lib-cover-styles-section',
    imports: [TranslatePipe],
    changeDetection: ChangeDetectionStrategy.OnPush,
    template: `
        @if (error(); as error) {
            <p class="form-error" role="alert">{{ error }}</p>
        }
        @if (list(); as list) {
            <p class="field-hint cover-templates-intro">{{ 'coverStyles.intro' | t: { palettes: list.palettes } }}</p>
            <ul class="cover-templates" [attr.aria-label]="'coverStyles.list' | t">
                @for (template of list.templates; track template.index) {
                    @let name = 'cover.template.' + template.name | t;
                    @let last = template.on && onCount() === 1;
                    <li class="panel cover-template" [class.off]="!template.on">
                        <div class="cover-template-head">
                            <h2>{{ name }}</h2>
                            <label class="switch" [title]="(last ? 'coverStyles.lastOnHint' : template.on ? 'coverStyles.turnOff' : 'coverStyles.turnOn') | t">
                                <input type="checkbox" role="switch" [checked]="template.on" [disabled]="busy() || last"
                                       [attr.aria-label]="'coverStyles.switchLabel' | t: { name }" (change)="toggle(template, $event)">
                                <span class="switch-track" aria-hidden="true"></span>
                            </label>
                        </div>
                        <div class="cover-template-samples">
                            @for (variant of palettes(); track variant) {
                                <img [src]="sample(template.index, variant)" alt="" width="240" height="360" loading="lazy" decoding="async">
                            }
                        </div>
                        @if (!template.on) {
                            <div class="cover-template-off">
                                <p class="field-hint">{{ 'coverStyles.offHint' | t }}</p>
                                <button type="button" class="button" [disabled]="busy()" (click)="reassign(template)">{{ 'coverStyles.reassign' | t }}</button>
                            </div>
                        }
                    </li>
                }
            </ul>
        }
    `,
})
export class CoverStylesSection implements OnInit {
    private readonly i18n = inject(I18n);
    private readonly api = inject(Api);
    private readonly confirm = inject(ConfirmService);
    private readonly toasts = inject(ToastService);
    private readonly watch = inject(TaskWatch);

    protected readonly list = signal<CoverTemplates | null>(null);
    protected readonly busy = signal(false);
    protected readonly error = signal<string | null>(null);
    protected readonly palettes = computed(() => [...Array(this.list()?.palettes ?? 0).keys()]);
    protected readonly onCount = computed(() => this.list()?.templates.filter(t => t.on).length ?? 0);

    async ngOnInit(): Promise<void> {
        try {
            this.list.set(await this.api.coverTemplates());
        } catch (error) {
            this.error.set(errorText(this.i18n, error));
        }
    }

    protected sample(template: number, variant: number): string {
        return coverSampleUrl(template, variant, this.i18n.t('coverStyles.sampleTitle'), this.i18n.t('coverStyles.sampleAuthor'));
    }

    protected async toggle(template: CoverTemplateInfo, event: Event): Promise<void> {
        const input = event.target as HTMLInputElement;
        this.busy.set(true);
        this.error.set(null);
        try {
            this.list.set(await this.api.setCoverTemplate(template.index, input.checked));
        } catch (error) {
            input.checked = template.on;
            this.error.set(errorText(this.i18n, error));
        } finally {
            this.busy.set(false);
        }
    }

    protected async reassign(template: CoverTemplateInfo): Promise<void> {
        const name = this.i18n.t(`cover.template.${template.name}`);
        const ok = await this.confirm.ask({
            title: this.i18n.t('coverStyles.reassignTitle', { name }),
            message: this.i18n.t('coverStyles.reassignConfirm', { name }),
            action: this.i18n.t('coverStyles.reassignAction'),
        });
        if (!ok)
            return;
        this.busy.set(true);
        this.error.set(null);
        try {
            const { runId } = await this.api.reassignCoverTemplate(template.index);
            this.watch.follow(runId);
            this.toasts.show(this.i18n.t('coverStyles.started', { name }));
        } catch (error) {
            this.error.set(errorText(this.i18n, error));
        } finally {
            this.busy.set(false);
        }
    }
}
