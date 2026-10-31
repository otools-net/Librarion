import { ChangeDetectionStrategy, Component } from '@angular/core';
import { Shell } from './shell/shell';

@Component({
    selector: 'lib-root',
    imports: [Shell],
    template: '<lib-shell />',
    changeDetection: ChangeDetectionStrategy.OnPush,
})
export class App {}
