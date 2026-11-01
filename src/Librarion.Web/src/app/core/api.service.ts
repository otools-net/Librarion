import { HttpClient, HttpErrorResponse, HttpHeaders, HttpParams } from '@angular/common/http';
import { Injectable, Injector, inject } from '@angular/core';
import { Router } from '@angular/router';
import { Observable, firstValueFrom } from 'rxjs';
import {
    GroupInfo, GroupInput, LibraryInfo, LibraryRootInfo, MissingBookPage, MissingRemoval, ScheduleInfo, ScheduleInput, TaskRunInfo, RightInfo, SecurityEventInfo, SessionInfo, SettingChanges, SettingLevel, SettingState, SetupInput,
    SetupState, SystemInfo, TenantInfo, TenantInput, UserInfo, UserInput,
} from './models';

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
    missingBooks = (library: number, after?: string) => this.get<MissingBookPage>(`api/libraries/${library}/missing`, { after, limit: 20 });
    removeMissingBooks = (library: number, removal: MissingRemoval) => this.post<{ runId: number }>(`api/libraries/${library}/missing/remove`, removal);
    task = (run: number) => this.get<TaskRunInfo>(`api/tasks/${run}`);
    cancelTask = (run: number) => this.post<void>(`api/tasks/${run}/cancel`);
    schedules = (library: number) => this.get<ScheduleInfo[]>(`api/libraries/${library}/schedules`);
    addSchedule = (library: number, input: ScheduleInput) => this.post<ScheduleInfo>(`api/libraries/${library}/schedules`, input);
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

    /** The settings of a level; `owner`: the library (or another tenant); the user level is one's own preferences. */
    settings = (level: SettingLevel, owner?: number) =>
        level === 'user' ? this.get<SettingState[]>('api/me/preferences') : this.get<SettingState[]>('api/settings', { level, owner });
    saveSettings = (level: SettingLevel, changes: SettingChanges, owner?: number) =>
        level === 'user'
            ? this.patch<SettingState[]>('api/me/preferences', changes)
            : this.patch<SettingState[]>(withQuery('api/settings', { level, owner }), changes);

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
