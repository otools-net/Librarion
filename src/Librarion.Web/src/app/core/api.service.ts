import { HttpClient, HttpErrorResponse, HttpHeaders, HttpParams } from '@angular/common/http';
import { Injectable, Injector, inject } from '@angular/core';
import { Router } from '@angular/router';
import { Observable, firstValueFrom } from 'rxjs';
import {
    BookDetail, BookDuplicate, BookListItem, BookSort, FacetList, FieldInfo, FieldValue, GroupInfo, GroupInput, ImportInput, KeysetPage, LibraryInfo,
    LibraryRootInfo, ManagedFields, ManagedPage, MergeLogInfo, MissingBookPage, MissingRemoval, RightInfo, ScheduleInfo, ScheduleInput,
    SecurityEventInfo, SessionInfo, SettingChanges, SettingInfo, SettingLevel, SettingState, SetupInput, SetupState, SuggestionList, SystemInfo,
    PickedTags, TagDefinition, TagDetail, TagRef, TagInput, TagWork, TaskOverview, TaskRunInfo, TenantInfo, TenantInput, UserInfo, UserInput,
} from './models';

/** A book's cover (`s` 80 px, `m` 240, `l` 600 wide), relative to the app's root: never missing, a generated one stands in. */
export const coverUrl = (book: number, size: 's' | 'm' | 'l') => `api/books/${book}/cover/${size}`;

/** A file of a book to download (`library.download` in its library); streamed from the library, never unpacked. */
export const fileUrl = (book: number, file: number) => `api/books/${book}/files/${file}`;

/** Header every request changing something carries (the server's RequestGuard refuses it otherwise). */
export const REQUEST_HEADER = 'X-Librarion-Request';

/** Problem type of the answers while the server has no administrator. */
export const SETUP_REQUIRED = 'setup-required';

/**
 * A failed request (as SFE's): the problem's message, its validation codes by field, and its `type` (a code too:
 * `setup-required`, `sign-in-failed`, `tenant.lastAdministrator`…). Pages show them through `errorKey` (errors.ts).
 */
export class ApiError extends Error {
    constructor(readonly status: number, message: string, readonly errors: Record<string, string[]> | null = null, readonly type: string | null = null,
        readonly extensions: Record<string, unknown> = {}) {
        super(message);
    }
}

type QueryValue = string | number | boolean | null | undefined;

/** Leaves out empty values (and `false`), as the API's defaults apply then. */
function params(values: Record<string, QueryValue>): HttpParams {
    let result = new HttpParams();
    for (const [key, value] of Object.entries(values)) {
        if (value !== undefined && value !== null && value !== '' && value !== false)
            result = result.set(key, String(value));
    }
    return result;
}

/** `url` with the query of `values` (for requests with a body), nothing added when they are all empty. */
function withQuery(url: string, values: Record<string, QueryValue>): string {
    const query = params(values).toString();
    return query ? `${url}?${query}` : url;
}

/**
 * The only class that talks to the server (agents/docs/development/frontend.md). Promises; URLs stay relative, so
 * the app works under a virtual directory. A 401 outside the session's own routes opens the sign-in page, a 503
 * `setup-required` the setup page (as SFE).
 */
@Injectable({ providedIn: 'root' })
export class Api {
    private readonly http = inject(HttpClient);
    /** The router, asked for when a redirect is due (it needs the Api itself, through the pages). */
    private readonly injector = inject(Injector);

    systemInfo = () => this.get<SystemInfo>('api/system/info');

    session = () => this.get<SessionInfo>('api/session');
    signIn = (userName: string, password: string, remember: boolean) => this.post<SessionInfo>('api/session/login', { userName, password, remember });
    signOut = () => this.post<SessionInfo>('api/session/logout');
    switchTenant = (tenantId: number) => this.post<SessionInfo>('api/session/tenant', { tenantId });
    changePassword = (current: string, password: string) => this.put<void>('api/session/password', { current, password });
    requestRecovery = (userName: string) => this.post<{ minutes: number }>('api/session/recovery', { userName });
    completeRecovery = (userName: string, code: string, password: string, remember: boolean) =>
        this.post<SessionInfo>('api/session/recovery/complete', { userName, code, password, remember });

    setup = () => this.get<SetupState>('api/setup');
    completeSetup = (input: SetupInput) => this.post<SessionInfo>('api/setup', input);

    tenants = () => this.get<TenantInfo[]>('api/tenants');
    createTenant = (input: TenantInput) => this.post<TenantInfo>('api/tenants', input);
    updateTenant = (id: number, input: TenantInput) => this.put<TenantInfo>(`api/tenants/${id}`, input);
    deleteTenant = (id: number) => this.delete<void>(`api/tenants/${id}`);

    libraries = () => this.get<LibraryInfo[]>('api/libraries');
    createLibrary = (name: string) => this.post<LibraryInfo>('api/libraries', { name });
    renameLibrary = (id: number, name: string) => this.put<LibraryInfo>(`api/libraries/${id}`, { name });
    deleteLibrary = (id: number) => this.delete<void>(`api/libraries/${id}`);
    libraryRoots = (library: number) => this.get<LibraryRootInfo[]>(`api/libraries/${library}/roots`);
    addLibraryRoot = (library: number, path: string) => this.post<LibraryRootInfo>(`api/libraries/${library}/roots`, { path });
    removeLibraryRoot = (library: number, root: number) => this.delete<{ runId: number }>(`api/libraries/${library}/roots/${root}`);
    scanLibrary = (library: number) => this.post<{ runId: number; started: boolean }>(`api/libraries/${library}/scan`);
    /** Newest first; `before`: the last id of the page before. */
    libraryTasks = (library: number, before?: number) => this.get<TaskRunInfo[]>(`api/libraries/${library}/tasks`, { before, limit: 10 });
    /** The runs going on in the libraries the session may scan (the progress panel). */
    runningTasks = () => this.get<TaskRunInfo[]>('api/tasks/running');
    taskOverview = () => this.get<TaskOverview>('api/tasks/overview');
    /** Every library's runs, newest first; `before`: the last id of the page before. */
    taskHistory = (before?: number) => this.get<TaskRunInfo[]>('api/tasks', { before, limit: 20 });
    missingBooks = (library: number, after?: string) => this.get<MissingBookPage>(`api/libraries/${library}/missing`, { after, limit: 20 });
    removeMissingBooks = (library: number, removal: MissingRemoval) => this.post<{ runId: number }>(`api/libraries/${library}/missing/remove`, removal);
    importKinds = (library: number) => this.get<string[]>(`api/libraries/${library}/import/kinds`);
    importIntoLibrary = (library: number, input: ImportInput) => this.post<{ runId: number }>(`api/libraries/${library}/import`, input);
    task = (run: number) => this.get<TaskRunInfo>(`api/tasks/${run}`);
    cancelTask = (run: number) => this.post<void>(`api/tasks/${run}/cancel`);
    schedules = (library: number) => this.get<ScheduleInfo[]>(`api/libraries/${library}/schedules`);
    addSchedule = (library: number, input: ScheduleInput) => this.post<ScheduleInfo>(`api/libraries/${library}/schedules`, input);
    updateSchedule = (library: number, schedule: number, input: ScheduleInput) => this.put<ScheduleInfo>(`api/libraries/${library}/schedules/${schedule}`, input);
    deleteSchedule = (library: number, schedule: number) => this.delete<void>(`api/libraries/${library}/schedules/${schedule}`);

    /** `admins`: the server administrators instead of the tenant's accounts; `after`: the last name of the page before. */
    users = (query: { admins?: boolean; search?: string; after?: string } = {}) => this.get<UserInfo[]>('api/users', query);
    createUser = (input: UserInput) => this.post<UserInfo>('api/users', input);
    updateUser = (id: number, input: UserInput) => this.put<UserInfo>(`api/users/${id}`, input);
    deleteUser = (id: number) => this.delete<void>(`api/users/${id}`);

    rights = () => this.get<RightInfo[]>('api/rights');
    groups = () => this.get<GroupInfo[]>('api/groups');
    createGroup = (input: GroupInput) => this.post<GroupInfo>('api/groups', input);
    updateGroup = (id: number, input: GroupInput) => this.put<GroupInfo>(`api/groups/${id}`, input);
    deleteGroup = (id: number) => this.delete<void>(`api/groups/${id}`);

    /** The books of a library the session may browse, a page at a time by key (`after`: the `next` of the page before). */
    books = (query: { library: number; sort: BookSort; tag?: number | null; q?: string | null; after?: string | null; limit?: number }) =>
        this.get<KeysetPage<BookListItem>>('api/books', {
            library: query.library, sort: query.sort, tag: query.tag ?? undefined, q: query.q || undefined, after: query.after ?? undefined, limit: query.limit,
        });
    /** What to offer for the last term of a search being typed, the most used in `library` first. */
    suggestions = (q: string, library?: number) => this.get<SuggestionList>('api/search/suggestions', { q, library });
    /** The most frequent values of each field among the books of `library` matching `q` (all of them without), with counts. */
    facets = (library: number, q?: string | null) => this.get<FacetList>('api/search/facets', { library, q: q || undefined });
    book = (id: number) => this.get<BookDetail>(`api/books/${id}`);
    /** Other books the session sees that may be this one, strongest reason first (at most 20). */
    bookDuplicates = (id: number) => this.get<BookDuplicate[]>(`api/books/${id}/duplicates`);

    fields = () => this.get<FieldInfo[]>('api/fields');
    /**
     * A field's values (`field`: its key) or the plain tags (`field` null) by sort name, with the library's counts and
     * only those with books there; a page may be short while `next` says more follow. `from`: start there (a jump).
     */
    tagValues = (field: string | null, query: { library?: number; from?: string | null; after?: string | null; limit?: number }) =>
        this.get<KeysetPage<FieldValue>>(field ? `api/fields/${encodeURIComponent(field)}/values` : 'api/tags', {
            library: query.library, from: query.from || undefined, after: query.after ?? undefined, limit: query.limit,
        });
    tag = (id: number, library?: number) => this.get<TagDetail>(`api/tags/${id}`, { library });
    /** The tag a gone tag was merged into (its old address); 404 when it wasn't merged. */
    movedTag = (id: number, library?: number) => this.get<TagRef>(`api/tags/${id}/moved`, { library });

    /** The tag manager (`tenant.tags`): the fields with their value counts; a field's tags (`field` null: plain tags) by key, or found by `q`. */
    /** The tag picker's suggestions: tags of `field` (null: plain tags) found by what is typed. */
    pickTags = (field: string | null, q: string, limit = 10) => this.get<PickedTags>('api/tags/pick', { field, q, limit });
    managedFields = () => this.get<ManagedFields>('api/tags/manage/fields');
    managedTags = (field: string | null, query: { q?: string | null; after?: string | null; limit?: number } = {}) =>
        this.get<ManagedPage>('api/tags/manage', { field: field ?? undefined, q: query.q || undefined, after: query.after ?? undefined, limit: query.limit });
    tagDefinition = (id: number) => this.get<TagDefinition>(`api/tags/${id}/definition`);
    createTag = (input: TagInput) => this.post<TagDefinition>('api/tags', input);
    updateTag = (id: number, input: TagInput) => this.put<TagDefinition>(`api/tags/${id}`, input);
    /** Done, or a run when it touches many books (`runId`). */
    deleteTag = (id: number) => this.delete<TagWork>(`api/tags/${id}`);
    mergeTag = (id: number, targetId: number) => this.post<TagWork>(`api/tags/${id}/merge`, { targetId });
    mergeField = (key: string, target: string) => this.post<TagWork>(`api/fields/${encodeURIComponent(key)}/merge`, { target });
    merges = (before?: number) => this.get<MergeLogInfo[]>('api/merges', { before });
    undoMerge = (id: number) => this.post<TagWork>(`api/merges/${id}/undo`);

    /** The settings of a level; `owner`: the library (or another tenant); the user level is one's own preferences. */
    settings = (level: SettingLevel, owner?: number) =>
        level === 'user' ? this.get<SettingState[]>('api/me/preferences') : this.get<SettingState[]>('api/settings', { level, owner });
    saveSettings = (level: SettingLevel, changes: SettingChanges, owner?: number) =>
        level === 'user'
            ? this.patch<SettingState[]>('api/me/preferences', changes)
            : this.patch<SettingState[]>(withQuery('api/settings', { level, owner }), changes);
    /** Every setting key, with the narrowest level that may set it (the administration's search). */
    settingCatalog = () => this.get<SettingInfo[]>('api/settings/catalog');

    /** Newest first; `before`: the last id of the page before; `all`: every tenant's (server administrators). */
    securityEvents = (query: { before?: number; kind?: string; user?: string; all?: boolean } = {}) =>
        this.get<SecurityEventInfo[]>('api/security-events', query);

    private get<T>(url: string, query: Record<string, QueryValue> = {}): Promise<T> {
        return this.send(url, this.http.get<T>(url, { params: params(query) }));
    }

    private post<T>(url: string, body: unknown = null): Promise<T> {
        return this.send(url, this.http.post<T>(url, body, { headers: this.changing() }));
    }

    private put<T>(url: string, body: unknown): Promise<T> {
        return this.send(url, this.http.put<T>(url, body, { headers: this.changing() }));
    }

    private patch<T>(url: string, body: unknown): Promise<T> {
        return this.send(url, this.http.patch<T>(url, body, { headers: this.changing() }));
    }

    private delete<T>(url: string): Promise<T> {
        return this.send(url, this.http.delete<T>(url, { headers: this.changing() }));
    }

    private changing(): HttpHeaders {
        return new HttpHeaders({ [REQUEST_HEADER]: '1' });
    }

    private async send<T>(url: string, request: Observable<T>): Promise<T> {
        try {
            return await firstValueFrom(request);
        } catch (error) {
            throw this.failure(url, error);
        }
    }

    private failure(url: string, error: unknown): ApiError {
        if (!(error instanceof HttpErrorResponse))
            return new ApiError(0, String(error));
        const body = (error.error ?? {}) as { title?: string; detail?: string; type?: string; errors?: Record<string, string[]>; [key: string]: unknown };
        const type = typeof body.type === 'string' && !body.type.startsWith('http') ? body.type : null;
        const { title, detail, errors, ...extensions } = body;
        const failure = new ApiError(error.status, detail ?? title ?? error.message, errors ?? null, type, extensions);

        // The session's and setup's own routes handle their answers on their pages.
        const ownPage = url.startsWith('api/session') || url.startsWith('api/setup');
        if (!ownPage) {
            const router = this.injector.get(Router);
            if (error.status === 503 && type === SETUP_REQUIRED)
                void router.navigate(['/setup']);
            else if (error.status === 401)
                void router.navigate(['/login'], { queryParams: { return: router.url } });
        }
        return failure;
    }
}
