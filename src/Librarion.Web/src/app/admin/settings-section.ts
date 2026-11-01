import { ChangeDetectionStrategy, Component, effect, inject, input, signal, untracked } from '@angular/core';
import { Api } from '../core/api.service';
import { I18n, TranslatePipe } from '../core/i18n.service';
import { LibraryInfo, SettingLevel } from '../core/models';
import { UnsavedChanges } from '../core/unsaved-changes';
import { ConfirmService } from '../shared/confirm';
import { SettingsPanel } from '../settings/settings-panel';

/**
 * The settings of one level, a sub-page of the administration: the server's, the tenant's, or a library's (chosen
 * above the form).
 */
@Component({
    selector: 'lib-settings-section',
    imports: [TranslatePipe, SettingsPanel],
    changeDetection: ChangeDetectionStrategy.OnPush,
    template: `
        @if (level() === 'library') {
            <div class="toolbar">
                <label class="field inline-field">
                    <span class="field-label">{{ 'settings.library' | t }}</span>
                    <select (change)="chooseLibrary($event)">
                        @for (item of libraries(); track item.id) {
                            <option [value]="item.id" [selected]="library() === item.id">{{ item.name }}</option>
                        }
                    </select>
                </label>
            </div>
            @if (library(); as owner) {
                <lib-settings-panel level="library" [owner]="owner" />
            }
        } @else {
            <lib-settings-panel [level]="level()" />
        }
    `,
})
export class SettingsSection {
    private readonly api = inject(Api);
    private readonly unsaved = inject(UnsavedChanges);
    private readonly confirm = inject(ConfirmService);
    private readonly i18n = inject(I18n);

    readonly level = input.required<SettingLevel>();
    protected readonly libraries = signal<LibraryInfo[]>([]);
    protected readonly library = signal<number | null>(null);


    constructor() {
        effect(() => {
            if (this.level() === 'library')
                untracked(() => void this.loadLibraries());
        });
    }

    /** Another library's settings: the changes made here are lost, so the user is asked first. */
    protected async chooseLibrary(event: Event): Promise<void> {
        const select = event.target as HTMLSelectElement;
        const id = +select.value;
        if (await this.unsaved.confirmLeave(this.confirm, this.i18n))
            this.library.set(id);
        else
            select.value = String(this.library());
    }

    private async loadLibraries(): Promise<void> {
        const libraries = await this.api.libraries().catch(() => []);
        this.libraries.set(libraries);
        this.library.set(libraries[0]?.id ?? null);
    }
}
