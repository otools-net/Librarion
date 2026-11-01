import { ChangeDetectionStrategy, Component, effect, inject, input, signal, untracked } from '@angular/core';
import { Api } from '../core/api.service';
import { valueOf } from '../core/errors';
import { TranslatePipe } from '../core/i18n.service';
import { LibraryInfo, SettingLevel } from '../core/models';
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
                    <select (change)="library.set(+valueOf($event))">
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

    readonly level = input.required<SettingLevel>();
    protected readonly libraries = signal<LibraryInfo[]>([]);
    protected readonly library = signal<number | null>(null);

    protected readonly valueOf = valueOf;

    constructor() {
        effect(() => {
            if (this.level() === 'library')
                untracked(() => void this.loadLibraries());
        });
    }

    private async loadLibraries(): Promise<void> {
        const libraries = await this.api.libraries().catch(() => []);
        this.libraries.set(libraries);
        this.library.set(libraries[0]?.id ?? null);
    }
}
