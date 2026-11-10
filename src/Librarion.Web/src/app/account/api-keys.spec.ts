import { TestBed } from '@angular/core/testing';
import { ApiMock, mockApi, sessionInfo, settle, testProviders } from '../../testing/fakes';
import { ApiError } from '../core/api.service';
import { LibraryContext } from '../core/library-context';
import { ApiKeyInfo } from '../core/models';
import { SessionService } from '../core/session.service';
import { ConfirmService } from '../shared/confirm';
import { ApiKeys } from './api-keys';

function keyInfo(overrides: Partial<ApiKeyInfo> = {}): ApiKeyInfo {
    return {
        id: 7, name: 'Phone', scopes: ['opds'], libraryIds: [], pathAllowed: false, created: '2026-11-10T10:00:00Z', expires: null, revoked: null,
        lastUsed: null, lastAddress: null, ...overrides,
    };
}

async function render(api: ApiMock, userId: number | null = null) {
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({ providers: testProviders(api) });
    TestBed.inject(SessionService).set(sessionInfo(['account.apiKeys']));
    api.libraries.mockResolvedValue([
        { id: 1, tenantId: 1, name: 'Fiction', created: '2026-10-31T10:00:00Z' },
        { id: 2, tenantId: 1, name: 'Comics', created: '2026-10-31T10:00:00Z' },
    ]);
    await TestBed.inject(LibraryContext).load();
    const fixture = TestBed.createComponent(ApiKeys);
    fixture.componentRef.setInput('userId', userId);
    const stable = async () => { await fixture.whenStable(); await settle(); await fixture.whenStable(); };
    await stable();
    return { element: fixture.nativeElement as HTMLElement, stable };
}

function type(element: HTMLElement, selector: string, value: string): void {
    const input = element.querySelector<HTMLInputElement>(selector)!;
    input.value = value;
    input.dispatchEvent(new Event('input'));
}

function tick(element: HTMLElement, text: string): void {
    const label = [...element.querySelectorAll('label.check')].find(l => l.textContent?.includes(text))!;
    label.querySelector('input')!.click();
}

function button(element: HTMLElement, text: string): HTMLButtonElement {
    return [...element.querySelectorAll<HTMLButtonElement>('button')].find(b => b.textContent?.trim() === text)!;
}

describe('ApiKeys', () => {
    afterEach(() => vi.restoreAllMocks());

    it('lists the keys with what they are for, where, when used, and ended ones without Revoke', async () => {
        const api = mockApi();
        api.apiKeys.mockResolvedValue([
            keyInfo({ scopes: ['opds', 'api:read'], libraryIds: [2], pathAllowed: true, lastUsed: '2026-11-10T11:00:00Z', lastAddress: '10.0.0.5' }),
            keyInfo({ id: 6, name: 'Old tablet', revoked: '2026-11-09T10:00:00Z' }),
            keyInfo({ id: 5, name: 'Trip', expires: '2026-01-01T00:00:00Z' }),
        ]);
        const { element } = await render(api);

        const rows = [...element.querySelectorAll('.api-key-row')];
        expect(rows).toHaveLength(3);
        expect(rows[0].textContent).toContain('Reader apps (OPDS), Reading through the API');
        expect(rows[0].textContent).toContain('Comics');
        expect(rows[0].textContent).toContain('in the address');
        expect(rows[0].textContent).toContain('10.0.0.5');
        expect(rows[0].querySelector('button')?.textContent).toContain('Revoke');
        expect(rows[1].textContent).toContain('Revoked');
        expect(rows[1].querySelector('button')).toBeNull();
        expect(rows[2].textContent).toContain('Ended');
        expect(rows[2].classList).toContain('api-key-ended');
        expect(element.textContent).toContain('/opds');
    });

    it('makes a key and shows it once, with Copy', async () => {
        const api = mockApi();
        api.apiKeys.mockResolvedValue([]);
        const made = { info: keyInfo({ name: 'Script', scopes: ['opds', 'api:read'], libraryIds: [1] }), key: 'lib_7_abcdefghijklmnopqrstuvwxyz0123456789ABCDEFG' };
        api.createApiKey.mockResolvedValue(made);
        const writeText = vi.fn().mockResolvedValue(undefined);
        Object.defineProperty(navigator, 'clipboard', { value: { writeText }, configurable: true });
        const { element, stable } = await render(api);

        button(element, 'New key').click();
        await stable();
        type(element, '#api-key-name', ' Script ');
        tick(element, 'Reading through the API');
        tick(element, 'Fiction');
        tick(element, 'Can be used in the address');
        await stable();
        element.querySelector('form')!.dispatchEvent(new Event('submit'));
        await stable();

        expect(api.createApiKey).toHaveBeenCalledWith({ name: 'Script', scopes: ['opds', 'api:read'], libraryIds: [1], pathAllowed: true, expires: null });
        expect(element.querySelector<HTMLInputElement>('#api-key-text')?.value).toBe(made.key);
        expect(element.querySelector('[role="status"]')?.textContent).toContain('shown only this once');
        button(element, 'Copy').click();
        await stable();
        expect(writeText).toHaveBeenCalledWith(made.key);

        button(element, 'Done').click();
        await stable();
        expect(element.querySelector('#api-key-text')).toBeNull();
    });

    it('says what is wrong with a key it was asked to make', async () => {
        const api = mockApi();
        api.apiKeys.mockResolvedValue([]);
        api.createApiKey.mockRejectedValue(new ApiError(400, 'Invalid', { expires: ['apiKey.expiresPast'] }));
        const { element, stable } = await render(api);

        button(element, 'New key').click();
        await stable();
        type(element, '#api-key-name', 'Phone');
        type(element, '#api-key-expires', '2020-01-01');
        await stable();
        element.querySelector('form')!.dispatchEvent(new Event('submit'));
        await stable();

        expect(element.querySelector('.field-error')?.textContent).toContain('must be in the future');
    });

    it('revokes a key after asking', async () => {
        const api = mockApi();
        api.apiKeys.mockResolvedValue([keyInfo()]);
        api.revokeApiKey.mockResolvedValue(keyInfo({ revoked: '2026-11-10T12:00:00Z' }));
        const ask = vi.spyOn(ConfirmService.prototype, 'ask').mockResolvedValue(true);
        const { element, stable } = await render(api);

        button(element, 'Revoke').click();
        await stable();

        expect(ask).toHaveBeenCalled();
        expect(api.revokeApiKey).toHaveBeenCalledWith(7);
    });

    it("shows an account's keys to its administrators: listed and revoked, never made", async () => {
        const api = mockApi();
        api.userApiKeys.mockResolvedValue([keyInfo()]);
        api.revokeUserApiKey.mockResolvedValue(keyInfo({ revoked: '2026-11-10T12:00:00Z' }));
        vi.spyOn(ConfirmService.prototype, 'ask').mockResolvedValue(true);
        const { element, stable } = await render(api, 2);

        expect(api.userApiKeys).toHaveBeenCalledWith(2);
        expect(api.apiKeys).not.toHaveBeenCalled();
        expect(button(element, 'New key')).toBeUndefined();
        button(element, 'Revoke').click();
        await stable();

        expect(api.revokeUserApiKey).toHaveBeenCalledWith(2, 7);
    });
});
