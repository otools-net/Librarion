import { ChangeDetectionStrategy, Component, computed, effect, inject, signal, untracked } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { Api } from '../core/api.service';
import { errorText } from '../core/errors';
import { I18n, TranslatePipe } from '../core/i18n.service';
import { LibraryContext } from '../core/library-context';
import { FolderDetail, FolderItem } from '../core/models';
import { BookList } from '../browse/book-list';
import { Icon } from '../shared/icon';
import { EmptyState, Skeleton } from '../shared/placeholders';

/** Folders asked for at a time. */
const PAGE = 100;

/** The address of a folder: its id finds it, its name is only for reading (as a tag's, tag-link.ts). */
export function folderLink(folder: { id: number; name: string }): (string | number)[] {
    return ['/folders', folder.id, folder.name.slice(0, 100)];
}

/**
 * The folder view (stage 6 chunk 8b, `library.folders`; agents/docs/features/folders.md): `/folders` is the library's
 * top (its only root's folders, or its roots), `/folders/<id>/<name>` a folder: the folders above it as links, its
 * folders and ZIPs by name with their stored counts (more by key), then its own books (`BookList` with the folder:
 * sorts, selection and Tag all as other lists). A folder of another library switches the library.
 */
@Component({
    selector: 'lib-folder-page',
    imports: [TranslatePipe, Icon, EmptyState, Skeleton, RouterLink, BookList],
    changeDetection: ChangeDetectionStrategy.OnPush,
    host: { class: 'folder-page' },
    template: `
        @if (library(); as library) {
            <header class="folder-head">
                <nav class="folder-crumbs" [attr.aria-label]="'folders.path' | t">
                    <ol>
                        <li><a routerLink="/folders">{{ library.name }}</a></li>
                        @for (crumb of detail()?.path ?? []; track crumb.id) {
                            <li><a [routerLink]="link(crumb)">{{ crumb.name }}</a></li>
                        }
                    </ol>
                </nav>
                <h1>
                    <svg [libIcon]="folder()?.zip ? 'zip' : 'folder'"></svg>
                    {{ id() ? (folder()?.name ?? '') : ('folders.title' | t) }}
                </h1>
                @if (folder(); as folder) {
                    <p class="browse-count">{{ summary(folder) }}</p>
                }
            </header>

            @if (children().length) {
                <ul class="values folders" [attr.aria-label]="'folders.list' | t">
                    @for (child of children(); track child.id) {
                        <li class="value folder-row">
                            <a [routerLink]="link(child)" [title]="child.name">
                                <svg [libIcon]="child.zip ? 'zip' : 'folder'"></svg>
                                <span class="value-name">{{ child.name }}</span>
                                <span class="value-count" [attr.aria-label]="rowCount(child)">{{ i18n.number(child.totalFiles) }}</span>
                            </a>
                        </li>
                    }
                </ul>
                @if (next()) {
                    <p class="folders-more">
                        <button type="button" class="button" [disabled]="loading()" (click)="more()">{{ 'folders.more' | t }}</button>
                    </p>
                }
            } @else if (error(); as error) {
                <p class="form-error" role="alert">{{ error }} <button type="button" class="button" (click)="load()">{{ 'common.retry' | t }}</button></p>
            } @else if (missing()) {
                <p class="empty large" libEmpty="folder">
                    {{ 'folders.missing' | t }}
                    <a routerLink="/folders">{{ 'folders.backToTop' | t }}</a>
                </p>
            } @else if (loading()) {
                <lib-skeleton [rows]="6" label="folders.loading" />
            } @else if (!folder()?.files) {
                <p class="empty large" libEmpty="folder">{{ (id() ? 'folders.empty' : 'folders.none') | t }}</p>
            }

            @if (folder(); as folder) {
                @if (folder.files) {
                    <lib-book-list class="folder-books" [library]="library.id" [folder]="folder.id" [label]="'folders.books' | t: { name: folder.name }"
                                   [setSize]="folder.files" emptyText="folders.noBooks">
                        <h2>{{ 'folders.booksHere' | t }}</h2>
                    </lib-book-list>
                }
            }
        } @else if (librariesLoaded()) {
            <h1>{{ 'folders.title' | t }}</h1>
            <p class="empty large" libEmpty="library">{{ 'browse.noLibrary' | t }}</p>
        } @else {
            <lib-skeleton [rows]="6" label="folders.loading" />
        }
    `,
})
export class FolderPage {
    private readonly api = inject(Api);
    protected readonly i18n = inject(I18n);
    private readonly route = inject(ActivatedRoute);
    private readonly router = inject(Router);
    private readonly libraries = inject(LibraryContext);

    private readonly params = toSignal(this.route.paramMap, { initialValue: this.route.snapshot.paramMap });
    /** The folder asked for; 0 at the library's top. */
    protected readonly id = computed(() => Number(this.params().get('id') ?? 0));
    protected readonly library = this.libraries.current;
    protected readonly librariesLoaded = signal(false);
    protected readonly detail = signal<FolderDetail | null>(null);
    /** The folder shown: the one asked for, or at the top a library's only root. */
    protected readonly folder = computed(() => this.detail()?.folder ?? null);
    protected readonly children = signal<FolderItem[]>([]);
    protected readonly next = signal<string | null>(null);
    protected readonly loading = signal(false);
    protected readonly missing = signal(false);
    protected readonly error = signal<string | null>(null);
    private requested = 0;

    constructor() {
        void this.libraries.load().finally(() => this.librariesLoaded.set(true));
        effect(() => {
            this.id();
            this.library()?.id;
            untracked(() => void this.load());
        });
    }

    protected link(folder: { id: number; name: string }): (string | number)[] {
        return folderLink(folder);
    }

    protected summary(folder: FolderItem): string {
        const parts = [];
        if (folder.folders)
            parts.push(this.i18n.t(folder.folders === 1 ? 'folders.count.folder' : 'folders.count.folders', { count: this.i18n.number(folder.folders) }));
        parts.push(this.i18n.t(folder.totalFiles === 1 ? 'folders.count.file' : 'folders.count.files', { count: this.i18n.number(folder.totalFiles) }));
        if (folder.folders && folder.files)
            parts.push(this.i18n.t('folders.count.here', { count: this.i18n.number(folder.files) }));
        return parts.join(' · ');
    }

    protected rowCount(folder: FolderItem): string {
        return this.i18n.t(folder.totalFiles === 1 ? 'folders.count.file' : 'folders.count.files', { count: this.i18n.number(folder.totalFiles) });
    }

    protected async load(): Promise<void> {
        const library = this.library();
        if (!library)
            return;
        const asked = ++this.requested;
        const id = this.id();
        this.loading.set(true);
        this.missing.set(false);
        this.error.set(null);
        this.children.set([]);
        this.next.set(null);
        if (this.detail()?.folder.id !== id)
            this.detail.set(null);
        try {
            let shown = id;
            if (!id) {
                // The top: a library's only root is the library itself; several are listed.
                const roots = await this.api.libraryFolders(library.id);
                if (asked !== this.requested)
                    return;
                if (roots.length !== 1) {
                    this.detail.set(null);
                    this.children.set(roots);
                    return;
                }
                shown = roots[0].id;
            }
            const [detail, page] = await Promise.all([this.api.folder(shown), this.api.folders(shown, null, PAGE)]);
            if (asked !== this.requested)
                return;
            if (detail.libraryId !== library.id) {
                // A folder of another library (a link, a bookmark): that library becomes the one browsed.
                this.libraries.choose(detail.libraryId);
                return;
            }
            this.detail.set(detail);
            this.children.set(page.items);
            this.next.set(page.next);
            if (id)
                await this.readable(detail.folder);
        } catch (error) {
            if (asked !== this.requested)
                return;
            if ((error as { status?: number }).status === 404)
                this.missing.set(true);
            else
                this.error.set(errorText(this.i18n, error));
        } finally {
            if (asked === this.requested)
                this.loading.set(false);
        }
    }

    protected async more(): Promise<void> {
        const folder = this.folder();
        const after = this.next();
        if (!folder || !after || this.loading())
            return;
        const asked = this.requested;
        this.loading.set(true);
        try {
            const page = await this.api.folders(folder.id, after, PAGE);
            if (asked !== this.requested)
                return;
            this.children.update(items => [...items, ...page.items]);
            this.next.set(page.next);
        } catch (error) {
            if (asked === this.requested)
                this.error.set(errorText(this.i18n, error));
        } finally {
            if (asked === this.requested)
                this.loading.set(false);
        }
    }

    /** The address with the folder's current name, put in place of this one (no history entry) when it differs. */
    private async readable(folder: FolderItem): Promise<void> {
        const tree = this.router.createUrlTree(folderLink(folder));
        if (this.router.serializeUrl(tree) !== this.router.url)
            await this.router.navigateByUrl(tree, { replaceUrl: true });
    }
}
