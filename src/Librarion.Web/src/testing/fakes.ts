import { EnvironmentProviders, Provider } from '@angular/core';
import { provideRouter } from '@angular/router';
import { Mock } from 'vitest';
import { Api } from '../app/core/api.service';
import { SystemInfo } from '../app/core/models';

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

/** Lets timers, promises and change detection run (zoneless TestBed). Real timers only. */
export async function settle(ms = 0): Promise<void> {
    await new Promise(resolve => setTimeout(resolve, ms));
}

/** Sets a media query's match (see setup.ts) and tells its listeners. */
export function setMediaQuery(query: string, matches: boolean): void {
    (globalThis as unknown as { setMediaQuery(query: string, matches: boolean): void }).setMediaQuery(query, matches);
}
