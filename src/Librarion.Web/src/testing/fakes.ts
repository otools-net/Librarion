import { EnvironmentProviders, Provider } from '@angular/core';
import { provideRouter } from '@angular/router';
import { Mock } from 'vitest';
import { Api } from '../app/core/api.service';
import { GroupInfo, SessionInfo, SettingState, SystemInfo, UserInfo } from '../app/core/models';

/** Every method of `Api` as a mock: unset ones return a promise that never settles (as SFE). */
export type ApiMock = { [K in keyof Api]: Api[K] extends (...args: infer A) => infer R ? Mock<(...args: A) => R> : Api[K] };

export function mockApi(): ApiMock {
    const mocks = new Map<string, Mock>();
    return new Proxy({} as ApiMock, {
        get(target, key) {
            if (Reflect.has(target, key))
                return Reflect.get(target, key);
            // Not API methods: what promises, injectors and test matchers look for on any object.
            if (typeof key !== 'string' || key === 'then' || key.startsWith('ng') || key === 'toJSON' || key.startsWith('$$') || key === 'asymmetricMatch' || key === 'nodeType')
                return undefined;
            let mock = mocks.get(key);
            if (!mock)
                mocks.set(key, mock = vi.fn(() => new Promise(() => { })));
            return mock;
        },
    });
}

/** Providers of a component test: the mocked API and an empty router. */
export function testProviders(api: ApiMock, ...more: (Provider | EnvironmentProviders)[]): (Provider | EnvironmentProviders)[] {
    return [{ provide: Api, useValue: api }, provideRouter([]), ...more];
}

export function systemInfo(overrides: Partial<SystemInfo> = {}): SystemInfo {
    return { version: '0.0.0', commit: 'abc123', database: 'sqlite', ...overrides };
}

/** A signed-in session: an account of tenant 1 ("Home") with `rights`. */
export function sessionInfo(rights: string[] = [], overrides: Partial<SessionInfo> = {}): SessionInfo {
    return {
        user: { id: 1, userName: 'ana', displayName: null, isServerAdmin: false, local: true },
        tenant: { id: 1, slug: 'home', name: 'Home' },
        tenants: [{ id: 1, slug: 'home', name: 'Home' }],
        rights,
        setupRequired: false,
        preferences: {},
        ...overrides,
    };
}

export function userInfo(overrides: Partial<UserInfo> = {}): UserInfo {
    return {
        id: 2, tenantId: 1, userName: 'bea', displayName: null, source: 'local', isServerAdmin: false, disabled: false, lockedOut: false,
        created: '2026-10-31T10:00:00Z', lastSignIn: null, groups: [], ...overrides,
    };
}

export function groupInfo(overrides: Partial<GroupInfo> = {}): GroupInfo {
    return { id: 10, tenantId: 1, name: 'Users', builtIn: 'users', externalName: null, allLibraries: true, rights: [], libraryIds: [], memberIds: [], ...overrides };
}

export function settingState(overrides: Partial<SettingState> = {}): SettingState {
    return {
        key: 'ui.theme', section: 'interface', kind: 'choice', choices: ['system', 'light', 'dark'], min: 0, max: 0,
        value: 'system', inherited: 'system', source: null, locked: false, lockedAbove: false, editable: true, ...overrides,
    };
}

/** Lets timers, promises and change detection run (zoneless TestBed). Real timers only. */
export async function settle(ms = 0): Promise<void> {
    await new Promise(resolve => setTimeout(resolve, ms));
}

/** Sets a media query's match (see setup.ts) and tells its listeners. */
export function setMediaQuery(query: string, matches: boolean): void {
    (globalThis as unknown as { setMediaQuery(query: string, matches: boolean): void }).setMediaQuery(query, matches);
}
