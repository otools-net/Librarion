import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { Api } from '../core/api.service';
import { TranslatePipe } from '../core/i18n.service';
import { SystemInfo } from '../core/models';
import { Skeleton } from '../shared/placeholders';

/** System › About: the server's version, build and database. */
@Component({
    selector: 'lib-system-section',
    imports: [TranslatePipe, Skeleton],
    changeDetection: ChangeDetectionStrategy.OnPush,
    template: `
        @if (info(); as info) {
            <dl class="facts panel">
                <dt>{{ 'home.version' | t }}</dt>
                <dd class="version">{{ info.version }}</dd>
                <dt>{{ 'system.commit' | t }}</dt>
                <dd class="commit">{{ info.commit }}</dd>
                <dt>{{ 'home.database' | t }}</dt>
                <dd class="database">{{ 'database.' + info.database | t }}</dd>
            </dl>
        } @else if (failed()) {
            <p class="form-error" role="alert">{{ 'home.unreachable' | t }}</p>
        } @else {
            <lib-skeleton [rows]="3" />
        }
    `,
})
export class SystemSection {
    protected readonly info = signal<SystemInfo | null>(null);
    protected readonly failed = signal(false);

    constructor() {
        inject(Api).systemInfo().then(info => this.info.set(info), () => this.failed.set(true));
    }
}
