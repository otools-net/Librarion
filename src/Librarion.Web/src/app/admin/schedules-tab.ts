import { ChangeDetectionStrategy, Component, OnInit, computed, effect, inject, input, signal, viewChild } from '@angular/core';
import { Api } from '../core/api.service';
import { errorText, fieldErrors, valueOf } from '../core/errors';
import { I18n, TranslatePipe } from '../core/i18n.service';
import { LibraryInfo, ScheduleInfo, ScheduleInput, ScheduleKind } from '../core/models';
import { ToastService } from '../core/toast.service';
import { ConfirmService } from '../shared/confirm';
import { Icon, IconName } from '../shared/icon';
import { EmptyState } from '../shared/placeholders';
import { Sheet } from '../shared/sheet';

/** A schedule being written in the dialog. `what`: `scan`, or `import:<kind>`. */
interface Draft {
    id: number | null;
    what: string;
    source: string;
    files: string;
    kind: ScheduleKind;
    hours: number;
    /** Bit 0 = Sunday. */
    days: number;
    time: string;
    enabled: boolean;
}

interface TaskChoice {
    what: string;
    icon: IconName;
    title: string;
    hint: string;
}

/**
 * A library's schedules (D-5.10 in agents/docs/features/tasks.md): each says what it runs first (a
 * folder scan, or an import with its source), then when, and whether it is on. Added and changed in a dialog in
 * numbered steps: what it runs, its source (imports), when.
 */
@Component({
    selector: 'lib-schedules-tab',
    imports: [TranslatePipe, Icon, EmptyState, Sheet],
    changeDetection: ChangeDetectionStrategy.OnPush,
    template: `
        <div class="toolbar">
            <button type="button" class="button primary" [title]="'schedules.addHint' | t" [disabled]="busy()" (click)="openNew()">
                <svg libIcon="plus"></svg>{{ 'schedules.add' | t }}
            </button>
        </div>
        <ul class="rows" [attr.aria-label]="'schedules.title' | t">
            @for (schedule of schedules(); track schedule.id) {
                <li class="row schedule-row" [class.off]="!schedule.enabled">
                    <svg [libIcon]="schedule.task === 'import' ? 'import' : 'refresh'" class="row-icon"></svg>
                    <span class="row-main">
                        <span class="row-title">{{ taskName(schedule) }}</span>
                        <span class="row-meta">
                            {{ describe(schedule) }}
                            @if (!schedule.enabled) {
                                · {{ 'schedules.off' | t }}
                            } @else if (schedule.nextRun) {
                                · {{ 'schedules.next' | t: { time: when(schedule.nextRun) } }}
                            }
                        </span>
                        @if (schedule.source) {
                            <span class="row-meta path">{{ schedule.source }}</span>
                        }
                    </span>
                    <label class="switch" [title]="(schedule.enabled ? 'schedules.turnOff' : 'schedules.turnOn') | t">
                        <input type="checkbox" role="switch" [checked]="schedule.enabled" [disabled]="busy()"
                               [attr.aria-label]="'schedules.enabledLabel' | t: { name: taskName(schedule) }" (change)="toggle(schedule)">
                        <span class="switch-track" aria-hidden="true"></span>
                    </label>
                    <button type="button" class="button" [title]="'schedules.editHint' | t" [disabled]="busy()" (click)="edit(schedule)">{{ 'common.edit' | t }}</button>
                    <button type="button" class="button danger" [title]="'schedules.deleteHint' | t" [disabled]="busy()" (click)="remove(schedule)">{{ 'common.delete' | t }}</button>
                </li>
            } @empty {
                <li class="row" libEmpty="clock">{{ 'schedules.none' | t }}</li>
            }
        </ul>
        @if (error(); as error) {
            <p class="form-error" role="alert">{{ error }}</p>
        }

        @if (draft(); as draft) {
            <lib-sheet [heading]="(draft.id === null ? 'schedules.newTitle' : 'schedules.editTitle') | t" (closed)="draftState.set(null)">
                <form id="schedule-form" class="schedule-editor" novalidate (submit)="save($event)">
                    <fieldset class="step">
                        <legend><span class="step-number" aria-hidden="true">1</span>{{ 'schedules.stepWhat' | t }}</legend>
                        <div class="choice-cards">
                            @for (choice of choices(); track choice.what) {
                                <label class="choice-card" [class.active]="draft.what === choice.what">
                                    <input type="radio" name="schedule-what" [value]="choice.what" [checked]="draft.what === choice.what" (change)="change({ what: choice.what })">
                                    <svg [libIcon]="choice.icon"></svg>
                                    <span class="choice-text">
                                        <strong>{{ choice.title }}</strong>
                                        <small>{{ choice.hint }}</small>
                                    </span>
                                </label>
                            }
                        </div>
                        @if (errors()['task']; as error) {
                            <span class="field-error">{{ error }}</span>
                        }
                    </fieldset>

                    @if (importKind(); as kind) {
                        <fieldset class="step">
                            <legend><span class="step-number" aria-hidden="true">2</span>{{ 'schedules.stepSource' | t }}</legend>
                            <div class="field">
                                <label class="field-label" for="schedule-source">{{ ('importKind.' + kind + '.source') | t }}</label>
                                <input id="schedule-source" type="text" spellcheck="false" [attr.aria-invalid]="errors()['source'] ? 'true' : null"
                                       [value]="draft.source" (input)="change({ source: valueOf($event) })">
                                @if (errors()['source']; as error) {
                                    <span class="field-error">{{ error }}</span>
                                }
                            </div>
                            <div class="field">
                                <label class="field-label" for="schedule-files">{{ 'import.files' | t }}</label>
                                <input id="schedule-files" type="text" spellcheck="false" [attr.aria-invalid]="errors()['files'] ? 'true' : null"
                                       [value]="draft.files" (input)="change({ files: valueOf($event) })">
                                <span class="field-hint">{{ 'import.filesHint' | t }}</span>
                                @if (errors()['files']; as error) {
                                    <span class="field-error">{{ error }}</span>
                                }
                            </div>
                        </fieldset>
                    }

                    <fieldset class="step">
                        <legend><span class="step-number" aria-hidden="true">{{ importKind() ? 3 : 2 }}</span>{{ 'schedules.stepWhen' | t }}</legend>
                        <div class="segmented" role="radiogroup" [attr.aria-label]="'schedules.kind' | t">
                            @for (option of kinds; track option) {
                                <button type="button" role="radio" class="button" [class.active]="draft.kind === option" [attr.aria-checked]="draft.kind === option"
                                        [title]="'schedules.' + option + '.hint' | t" (click)="change({ kind: option })">{{ 'schedules.' + option | t }}</button>
                            }
                        </div>
                        @if (draft.kind === 'interval') {
                            <div class="field narrow">
                                <label class="field-label" for="schedule-hours">{{ 'schedules.everyHours' | t }}</label>
                                <input id="schedule-hours" type="number" min="1" max="744" [value]="draft.hours" (input)="change({ hours: +valueOf($event) })">
                                @if (errors()['intervalMinutes']; as error) {
                                    <span class="field-error">{{ error }}</span>
                                }
                            </div>
                        } @else {
                            <div class="day-checks" role="group" [attr.aria-label]="'schedules.days' | t">
                                @for (day of dayNames; track $index) {
                                    <label class="check day">
                                        <input type="checkbox" [checked]="hasDay(draft, $index)" (change)="toggleDay(draft, $index)">
                                        <span>{{ day }}</span>
                                    </label>
                                }
                            </div>
                            @if (errors()['days']; as error) {
                                <span class="field-error">{{ error }}</span>
                            }
                            <div class="field narrow">
                                <label class="field-label" for="schedule-time">{{ 'schedules.time' | t }}</label>
                                <input id="schedule-time" type="time" [value]="draft.time" (input)="change({ time: valueOf($event) })">
                            </div>
                        }
                        <span class="field-hint">{{ 'schedules.skipHint' | t }}</span>
                    </fieldset>

                    <label class="check">
                        <input type="checkbox" [checked]="draft.enabled" (change)="change({ enabled: !draft.enabled })">
                        <span>{{ 'schedules.enabled' | t }}</span>
                    </label>
                    @if (formError(); as error) {
                        <p class="form-error" role="alert">{{ error }}</p>
                    }
                </form>
                <div sheetFooter class="sheet-actions">
                    <button type="button" class="button" [title]="'common.cancelHint' | t" (click)="sheet()?.close()">{{ 'common.cancel' | t }}</button>
                    <button type="submit" form="schedule-form" class="button primary" [title]="'schedules.saveHint' | t"
                            [disabled]="busy() || !ready()">{{ 'common.save' | t }}</button>
                </div>
            </lib-sheet>
        }
    `,
})
export class SchedulesTab implements OnInit {
    private readonly api = inject(Api);
    private readonly i18n = inject(I18n);
    private readonly confirm = inject(ConfirmService);
    private readonly toasts = inject(ToastService);

    readonly library = input.required<LibraryInfo>();
    /** The import kinds this account may schedule (none without the import right). */
    readonly importKinds = input<string[]>([]);

    protected readonly schedules = signal<ScheduleInfo[]>([]);
    protected readonly draftState = signal<Draft | null>(null);
    protected readonly draft = this.draftState.asReadonly();
    protected readonly busy = signal(false);
    protected readonly error = signal<string | null>(null);
    protected readonly formError = signal<string | null>(null);
    protected readonly errors = signal<Record<string, string>>({});
    protected readonly sheet = viewChild(Sheet);

    protected readonly kinds: ScheduleKind[] = ['interval', 'weekly'];
    /** Sunday first, as the schedule's day bits. */
    protected readonly dayNames = [...Array(7).keys()].map(day => new Intl.DateTimeFormat(undefined, { weekday: 'short' }).format(new Date(2026, 10, 1 + day)));
    protected readonly valueOf = valueOf;

    protected readonly choices = computed<TaskChoice[]>(() => [
        { what: 'scan', icon: 'refresh', title: this.i18n.t('schedules.task.scan'), hint: this.i18n.t('schedules.task.scan.hint') },
        ...this.importKinds().map(kind => ({
            what: `import:${kind}`, icon: 'import' as IconName,
            title: this.i18n.t('schedules.task.import', { kind: this.i18n.t(`importKind.${kind}`) }),
            hint: this.i18n.t('schedules.task.import.hint'),
        })),
    ]);
    /** The import kind of the draft, if it imports. */
    protected readonly importKind = computed(() => this.draft()?.what.startsWith('import:') ? this.draft()!.what.slice('import:'.length) : null);
    protected readonly ready = computed(() => {
        const draft = this.draft();
        return !!draft && (!this.importKind() || draft.source.trim() !== '') && (draft.kind === 'interval' ? draft.hours >= 1 : draft.days !== 0);
    });

    constructor() {
        effect(() => this.sheet()?.open());
    }

    async ngOnInit(): Promise<void> {
        await this.run(async () => this.schedules.set(await this.api.schedules(this.library().id)));
    }

    protected openNew(): void {
        this.open({ id: null, what: 'scan', source: '', files: '', kind: 'interval', hours: 24, days: 0b111_1111, time: '03:00', enabled: true });
    }

    protected edit(schedule: ScheduleInfo): void {
        const start = new Date(schedule.start);
        this.open({
            id: schedule.id,
            what: schedule.task === 'import' ? `import:${schedule.importKind}` : 'scan',
            source: schedule.source ?? '',
            files: schedule.files ?? '',
            kind: schedule.kind,
            hours: schedule.kind === 'interval' ? Math.max(1, Math.round(schedule.intervalMinutes / 60)) : 24,
            days: schedule.kind === 'weekly' ? schedule.days : 0b111_1111,
            time: `${String(start.getHours()).padStart(2, '0')}:${String(start.getMinutes()).padStart(2, '0')}`,
            enabled: schedule.enabled,
        });
    }

    protected change(part: Partial<Draft>): void {
        this.draftState.update(draft => draft && { ...draft, ...part });
    }

    /** Templates have no bitwise operators: the day bits are read and flipped here. */
    protected hasDay(draft: Draft, day: number): boolean {
        return (draft.days & (1 << day)) !== 0;
    }

    protected toggleDay(draft: Draft, day: number): void {
        this.change({ days: draft.days ^ (1 << day) });
    }

    protected async save(event: Event): Promise<void> {
        event.preventDefault();
        const draft = this.draft();
        if (!draft || !this.ready())
            return;
        await this.submit(draft.id, this.inputOf(draft), () => {
            this.toasts.show(this.i18n.t(draft.id === null ? 'schedules.added' : 'schedules.saved'));
            this.sheet()?.close();
        });
    }

    protected async toggle(schedule: ScheduleInfo): Promise<void> {
        const input: ScheduleInput = {
            task: schedule.task, importKind: schedule.importKind, source: schedule.source, files: schedule.files, kind: schedule.kind,
            start: schedule.start, intervalMinutes: schedule.intervalMinutes, days: schedule.days, enabled: !schedule.enabled,
        };
        await this.run(async () => {
            const saved = await this.api.updateSchedule(this.library().id, schedule.id, input);
            this.schedules.update(list => list.map(s => s.id === saved.id ? saved : s));
        });
    }

    protected async remove(schedule: ScheduleInfo): Promise<void> {
        const ok = await this.confirm.ask({
            title: this.i18n.t('schedules.deleteTitle'), message: this.i18n.t('schedules.deleteConfirm', { name: this.taskName(schedule), when: this.describe(schedule) }),
            action: this.i18n.t('common.delete'), danger: true,
        });
        if (!ok)
            return;
        await this.run(async () => {
            await this.api.deleteSchedule(this.library().id, schedule.id);
            this.schedules.update(list => list.filter(s => s.id !== schedule.id));
        });
    }

    protected taskName(schedule: ScheduleInfo): string {
        return schedule.task === 'import'
            ? this.i18n.t('schedules.task.import', { kind: this.i18n.t(`importKind.${schedule.importKind}`) })
            : this.i18n.t('schedules.task.scan');
    }

    protected describe(schedule: ScheduleInfo): string {
        if (schedule.kind === 'interval')
            return this.i18n.t('schedules.everyHoursText', { hours: Math.round(schedule.intervalMinutes / 6) / 10 });
        const days = this.dayNames.filter((_, day) => (schedule.days & (1 << day)) !== 0).join(', ');
        const time = new Intl.DateTimeFormat(undefined, { hour: '2-digit', minute: '2-digit' }).format(new Date(schedule.start));
        return this.i18n.t('schedules.weeklyText', { days, time });
    }

    protected when(utc: string): string {
        return new Intl.DateTimeFormat(undefined, { dateStyle: 'short', timeStyle: 'short' }).format(new Date(utc));
    }

    private open(draft: Draft): void {
        this.errors.set({});
        this.formError.set(null);
        this.draftState.set(draft);
    }

    /** What the server takes: interval schedules count from now, weekly ones keep the time of day chosen. */
    private inputOf(draft: Draft): ScheduleInput {
        const kind = this.importKind();
        const start = new Date();
        if (draft.kind === 'weekly') {
            const [hour, minute] = draft.time.split(':').map(Number);
            start.setHours(hour || 0, minute || 0, 0, 0);
        }
        return {
            task: kind ? 'import' : 'libraryScan',
            importKind: kind,
            source: kind ? draft.source.trim() : null,
            files: kind && draft.files.trim() ? draft.files.trim() : null,
            kind: draft.kind,
            start: start.toISOString(),
            intervalMinutes: draft.kind === 'interval' ? Math.max(1, draft.hours) * 60 : 0,
            days: draft.kind === 'weekly' ? draft.days : 0,
            enabled: draft.enabled,
        };
    }

    private async submit(id: number | null, input: ScheduleInput, done: () => void): Promise<void> {
        this.busy.set(true);
        this.errors.set({});
        this.formError.set(null);
        try {
            if (id === null)
                await this.api.addSchedule(this.library().id, input);
            else
                await this.api.updateSchedule(this.library().id, id, input);
            this.schedules.set(await this.api.schedules(this.library().id));
            done();
        } catch (error) {
            const byField = fieldErrors(this.i18n, error);
            if (Object.keys(byField).length)
                this.errors.set(byField);
            else
                this.formError.set(errorText(this.i18n, error));
        } finally {
            this.busy.set(false);
        }
    }

    private async run(work: () => Promise<void>): Promise<void> {
        this.busy.set(true);
        this.error.set(null);
        try {
            await work();
        } catch (error) {
            this.error.set(errorText(this.i18n, error));
        } finally {
            this.busy.set(false);
        }
    }
}
