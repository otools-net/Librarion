import { ChangeDetectionStrategy, Component } from '@angular/core';
import { RouterLink } from '@angular/router';
import { TranslatePipe } from '../core/i18n.service';

@Component({
    selector: 'lib-not-found-page',
    imports: [RouterLink, TranslatePipe],
    template: `
        <h1>{{ 'notFound.title' | t }}</h1>
        <p><a routerLink="/">{{ 'notFound.back' | t }}</a></p>
    `,
    changeDetection: ChangeDetectionStrategy.OnPush,
})
export class NotFoundPage {}
