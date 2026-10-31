import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { Api } from '../core/api.service';
import { valueOf } from '../core/errors';
import { TranslatePipe } from '../core/i18n.service';
import { LibraryInfo, SettingLevel } from '../core/models';
import { Rights } from '../core/rights';
import { SessionService } from '../core/session.service';
import { SettingsPanel } from '../settings/settings-panel';

/** Settings by level: the levels the session may edit (server, tenant, a library), one at a time. */
@Component({
    selector: 'lib-settings-section',
    imports: [TranslatePipe, SettingsPanel],
    changeDetection: ChangeDetectionStrategy.OnPush,
    template: `
        <div class="toolbar">
            <div class="segmented" role="radiogroup" [attr.aria-label]="'settings.level' | t">
                @for (option of levels(); track option) {
                    <button type="button" role="radio" class="button" [class.active]="level() === option" [attr.aria-checked]="level() === option"
                            [title]="'settingLevel.' + option + '.hint' | t" (click)="level.set(option)">{{ 'settingLevel.' + option | t }}</button>
                }
            </div>
            @if (level() === 'library') {
                <select [attr.aria-label]="'settings.library' | t" (change)="library.set(+valueOf($event))">
                    @for (item of libraries(); track item.id) {
                        <option [value]="item.id" [selected]="library() === item.id">{{ item.name }}</option>
                    }
                </select>
            }
        </div>
        @if (level() !== 'library') {
            <lib-settings-panel [level]="level()" />
        } @else if (library(); as owner) {
            <lib-settings-panel level="library" [owner]="owner" />
        }
    `,
})
export class SettingsSection implements OnInit {
    private readonly api = inject(Api);
    private readonly sessions = inject(SessionService);

    protected readonly levels = computed<SettingLevel[]>(() => {
        this.sessions.session();
        const levels: SettingLevel[] = [];
        if (this.sessions.can(Rights.serverSettings))
            levels.push('server');
        if (this.sessions.can(Rights.tenantSettings))
            levels.push('tenant');
        if (this.sessions.canAny(Rights.librarySettings, Rights.manageTenants))
            levels.push('library');
        return levels;
    });
    protected readonly level = signal<SettingLevel>('tenant');
    protected readonly libraries = signal<LibraryInfo[]>([]);
    protected readonly library = signal<number | null>(null);

    protected readonly valueOf = valueOf;

    async ngOnInit(): Promise<void> {
        this.level.set(this.levels()[0] ?? 'tenant');
        if (this.levels().includes('library')) {
            const libraries = await this.api.libraries().catch(() => []);
            this.libraries.set(libraries);
            this.library.set(libraries[0]?.id ?? null);
        }
    }
}
