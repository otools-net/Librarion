import { TestBed } from '@angular/core/testing';
import { ActivatedRouteSnapshot, Router, RouterStateSnapshot, UrlTree } from '@angular/router';
import { mockApi, sessionInfo, testProviders } from '../../testing/fakes';
import { ApiError } from './api.service';
import { errorText, fieldErrors } from './errors';
import { I18n } from './i18n.service';
import { SessionService, needsRight, signedIn } from './session.service';

describe('SessionService', () => {
    function setup(api = mockApi()) {
        TestBed.resetTestingModule();
        TestBed.configureTestingModule({ providers: testProviders(api) });
        return { api, sessions: TestBed.inject(SessionService) };
    }

    async function guard(fn: typeof signedIn, url = '/admin'): Promise<boolean | string> {
        const result = await TestBed.runInInjectionContext(() => fn({} as ActivatedRouteSnapshot, { url } as RouterStateSnapshot));
        return result instanceof UrlTree ? TestBed.inject(Router).serializeUrl(result) : result as boolean;
    }

    it('asks the server once and answers what the session may do', async () => {
        const { api, sessions } = setup();
        api.session.mockResolvedValue(sessionInfo(['tenant.users']));

        await Promise.all([sessions.load(), sessions.load()]);

        expect(api.session).toHaveBeenCalledTimes(1);
        expect(sessions.can('tenant.users')).toBe(true);
        expect(sessions.can('server.tenants')).toBe(false);
        expect(sessions.canAny('server.tenants', 'tenant.users')).toBe(true);
    });

    it('applies the account\'s theme preference', async () => {
        const { sessions } = setup();

        sessions.set(sessionInfo([], { preferences: { 'ui.theme': 'light' } }));

        expect(document.documentElement.dataset['theme']).toBe('light');
        localStorage.clear();
    });

    it('opens the setup page on a new server, the sign-in page without an account, the page with one', async () => {
        const { api } = setup();
        api.session.mockResolvedValue(sessionInfo([], { setupRequired: true, user: null }));
        expect(await guard(signedIn)).toBe('/setup');

        setup(api).sessions.clear();
        api.session.mockResolvedValue(sessionInfo([], { user: null }));
        expect(await guard(signedIn)).toBe('/login?return=%2Fadmin');

        setup(api).sessions.clear();
        api.session.mockResolvedValue(sessionInfo());
        expect(await guard(signedIn)).toBe(true);
    });

    it('sends a page needing a right home without it', async () => {
        const { sessions } = setup();
        sessions.set(sessionInfo(['library.browse']));

        expect(await guard(needsRight('tenant.users'))).toBe('/');
        expect(await guard(needsRight('library.browse'))).toBe(true);
    });
});

describe('errors', () => {
    it('translates the server\'s codes: problem types, validation codes, statuses', () => {
        TestBed.resetTestingModule();
        const i18n = TestBed.inject(I18n);

        expect(errorText(i18n, new ApiError(400, 'x', null, 'sign-in-failed'))).toBe('Wrong name or password.');
        expect(errorText(i18n, new ApiError(409, 'x', null, 'tenant.lastAdministrator'))).toContain('no administrator left');
        expect(errorText(i18n, new ApiError(400, 'x', { userName: ['userName.taken'] }))).toContain('Another account has this name');
        expect(errorText(i18n, new ApiError(0, 'offline'))).toBe('The server can\'t be reached.');
        expect(errorText(i18n, new ApiError(403, 'x'))).toBe('This account may not do that.');
        expect(errorText(i18n, new ApiError(400, 'x', null, 'some-new-type'))).toBe('x');
        expect(fieldErrors(i18n, new ApiError(400, 'x', { password: ['password.tooShort'] }))).toEqual({ password: 'The password is too short.' });
    });
});
