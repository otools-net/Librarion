import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { Api } from '../core/api.service';
import { errorText, valueOf } from '../core/errors';
import { I18n, TranslatePipe } from '../core/i18n.service';
import { LibraryInfo } from '../core/models';

/** Libraries of the tenant: added, renamed in place, deleted. Their folders and scans come in stage 3. */
@Component({
    selector: 'lib-libraries-section',
    imports: [TranslatePipe],
    changeDetection: ChangeDetectionStrategy.OnPush,
    template: `
        <form class="toolbar" novalidate (submit)="create($event)">
            <input type="text" class="toolbar-search" [placeholder]="'libraries.newName' | t" [attr.aria-label]="'libraries.newName' | t"
                   [value]="name()" (input)="name.set(valueOf($event))">
            <button type="submit" class="button primary" [title]="'libraries.addHint' | t" [disabled]="busy() || !name().trim()">{{ 'libraries.add' | t }}</button>
        </form>
        @if (error(); as error) {
            <p class="form-error" role="alert">{{ error }}</p>
        }
        <ul class="rows" [attr.aria-label]="'admin.libraries' | t">
            @for (library of libraries(); track library.id) {
                <li class="row">
                    @if (renaming() === library.id) {
                        <form class="row-main row-form" novalidate (submit)="rename($event, library)">
                            <input type="text" [attr.aria-label]="'libraries.rename' | t" [value]="library.name" (input)="newName.set(valueOf($event))">
                            <button type="submit" class="button primary" [title]="'common.saveHint' | t" [disabled]="busy()">{{ 'common.save' | t }}</button>
                            <button type="button" class="button" [title]="'common.cancelHint' | t" (click)="renaming.set(null)">{{ 'common.cancel' | t }}</button>
                        </form>
                    } @else {
                        <span class="row-main"><span class="row-title">{{ library.name }}</span></span>
                        <button type="button" class="button" [title]="'libraries.renameHint' | t: { name: library.name }" (click)="startRename(library)">{{ 'libraries.rename' | t }}</button>
                        <button type="button" class="button danger" [title]="'libraries.deleteHint' | t: { name: library.name }" [disabled]="busy()" (click)="remove(library)">{{ 'common.delete' | t }}</button>
                    }
                </li>
            } @empty {
                <li class="row muted">{{ 'libraries.none' | t }}</li>
            }
        </ul>
    `,
})
export class LibrariesSection implements OnInit {
    private readonly api = inject(Api);
    private readonly i18n = inject(I18n);

    protected readonly libraries = signal<LibraryInfo[]>([]);
    protected readonly name = signal('');
    protected readonly renaming = signal<number | null>(null);
    protected readonly newName = signal('');
    protected readonly busy = signal(false);
    protected readonly error = signal<string | null>(null);

    protected readonly valueOf = valueOf;

    async ngOnInit(): Promise<void> {
        await this.run(async () => this.libraries.set(await this.api.libraries()));
    }

    protected async create(event: Event): Promise<void> {
        event.preventDefault();
        await this.run(async () => {
            await this.api.createLibrary(this.name().trim());
            this.name.set('');
            this.libraries.set(await this.api.libraries());
        });
    }

    protected startRename(library: LibraryInfo): void {
        this.newName.set(library.name);
        this.renaming.set(library.id);
    }

    protected async rename(event: Event, library: LibraryInfo): Promise<void> {
        event.preventDefault();
        await this.run(async () => {
            await this.api.renameLibrary(library.id, this.newName().trim());
            this.renaming.set(null);
            this.libraries.set(await this.api.libraries());
        });
    }

    protected async remove(library: LibraryInfo): Promise<void> {
        if (!confirm(this.i18n.t('libraries.deleteConfirm', { name: library.name })))
            return;
        await this.run(async () => {
            await this.api.deleteLibrary(library.id);
            this.libraries.set(await this.api.libraries());
        });
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
