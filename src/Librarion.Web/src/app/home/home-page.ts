import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { Api } from '../core/api.service';
import { TranslatePipe } from '../core/i18n.service';
import { SystemInfo } from '../core/models';
import { Skeleton } from '../shared/placeholders';

/** The start page: what is running. The library's own home comes with the catalog (stage 5). */
@Component({
    selector: 'lib-home-page',
    imports: [TranslatePipe, Skeleton],
    template: `
        <h1>{{ 'home.title' | t }}</h1>
        <p class="lead">{{ 'home.intro' | t }}</p>
        @if (info(); as info) {
            <dl class="facts">
                <dt>{{ 'home.version' | t }}</dt>
                <dd class="version">{{ info.version }}</dd>
                <dt>{{ 'home.database' | t }}</dt>
                <dd class="database">{{ 'database.' + info.database | t }}</dd>
            </dl>
        } @else if (failed()) {
            <p class="error" role="alert">{{ 'home.unreachable' | t }}</p>
        } @else {
            <lib-skeleton label="home.loading" [rows]="2" />
        }
    `,
    host: { class: 'home-page' },
    changeDetection: ChangeDetectionStrategy.OnPush,
})
export class HomePage {
    protected readonly info = signal<SystemInfo | null>(null);
    protected readonly failed = signal(false);

    constructor() {
        inject(Api).systemInfo().then(info => this.info.set(info), () => this.failed.set(true));
    }
}
