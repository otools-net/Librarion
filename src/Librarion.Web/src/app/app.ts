import { ChangeDetectionStrategy, Component } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { ConfirmDialog } from './shared/confirm';
import { IconSprite } from './shared/icon';
import { Toasts } from './shared/toasts';

/**
 * The setup and sign-in pages stand alone; every other page is inside the shell (app.routes.ts). The icon sprite, the
 * confirmation dialog and the toasts are here once, for every page.
 */
@Component({
    selector: 'lib-root',
    imports: [RouterOutlet, IconSprite, ConfirmDialog, Toasts],
    template: '<lib-icon-sprite /><router-outlet /><lib-confirm-dialog /><lib-toasts />',
    changeDetection: ChangeDetectionStrategy.OnPush,
})
export class App {}
