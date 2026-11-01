import { ChangeDetectionStrategy, Component } from '@angular/core';
import { RouterOutlet } from '@angular/router';

/** The setup and sign-in pages stand alone; every other page is inside the shell (app.routes.ts). */
@Component({
    selector: 'lib-root',
    imports: [RouterOutlet],
    template: '<router-outlet />',
    changeDetection: ChangeDetectionStrategy.OnPush,
})
export class App {}
