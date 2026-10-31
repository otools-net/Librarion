import { Type } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, convertToParamMap } from '@angular/router';
import { ApiMock, mockApi, sessionInfo, settingState, settle, testProviders } from '../../testing/fakes';
import { ApiError } from '../core/api.service';
import { SessionService } from '../core/session.service';
import { AccountPage } from './account-page';
import { SetupPage } from './setup-page';
import { SignInPage } from './sign-in-page';

async function render<T>(component: Type<T>, api: ApiMock, returnUrl: string | null = null) {
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
        providers: testProviders(api, { provide: ActivatedRoute, useValue: { snapshot: { queryParamMap: convertToParamMap(returnUrl ? { return: returnUrl } : {}) } } }),
    });
    const router = TestBed.inject(Router);
    const navigate = vi.spyOn(router, 'navigateByUrl').mockResolvedValue(true);
    const navigateTo = vi.spyOn(router, 'navigate').mockResolvedValue(true);
    const fixture = TestBed.createComponent(component);
    await fixture.whenStable();
    await settle();
    await fixture.whenStable();
    const element = fixture.nativeElement as HTMLElement;
    return { fixture, element, navigate, navigateTo, stable: async () => { await settle(); await fixture.whenStable(); } };
}

function type(element: HTMLElement, selector: string, value: string): void {
    const input = element.querySelector<HTMLInputElement>(selector)!;
    input.value = value;
    input.dispatchEvent(new Event('input'));
}

function submit(element: HTMLElement): void {
    element.querySelector('form')!.dispatchEvent(new Event('submit'));
}

describe('SignInPage', () => {
    it('signs in and goes back where it was asked from', async () => {
        const api = mockApi();
        api.session.mockResolvedValue(sessionInfo([], { user: null }));
        api.signIn.mockResolvedValue(sessionInfo());
        const { element, navigate, stable } = await render(SignInPage, api, '/admin/users');

        type(element, '#login-name', ' ana ');
        type(element, '#login-password', 'secret');
        submit(element);
        await stable();

        expect(api.signIn).toHaveBeenCalledWith('ana', 'secret', false);
        expect(navigate).toHaveBeenCalledWith('/admin/users', { replaceUrl: true });
        expect(TestBed.inject(SessionService).user()?.userName).toBe('ana');
    });

    it('never goes to another site after signing in', async () => {
        const api = mockApi();
        api.session.mockResolvedValue(sessionInfo([], { user: null }));
        api.signIn.mockResolvedValue(sessionInfo());
        const { element, navigate, stable } = await render(SignInPage, api, '//elsewhere/page');

        type(element, '#login-name', 'ana');
        type(element, '#login-password', 'secret');
        submit(element);
        await stable();

        expect(navigate).toHaveBeenCalledWith('/', { replaceUrl: true });
    });

    it('says why a sign-in is refused and empties the password', async () => {
        const api = mockApi();
        api.session.mockResolvedValue(sessionInfo([], { user: null }));
        api.signIn.mockRejectedValue(new ApiError(400, 'Wrong', null, 'sign-in-failed'));
        const { element, stable } = await render(SignInPage, api);

        type(element, '#login-name', 'ana');
        type(element, '#login-password', 'wrong');
        await stable();
        submit(element);
        await stable();

        expect(element.querySelector('[role="alert"]')?.textContent).toContain('Wrong name or password');
        expect(element.querySelector<HTMLInputElement>('#login-password')!.value).toBe('');
    });

    it('opens the setup page on a new server', async () => {
        const api = mockApi();
        api.session.mockResolvedValue(sessionInfo([], { user: null, setupRequired: true }));
        const { navigateTo } = await render(SignInPage, api);

        expect(navigateTo).toHaveBeenCalledWith(['/setup'], { replaceUrl: true });
    });

    it('recovers an administrator with the code the server shows, then signs in', async () => {
        const api = mockApi();
        api.session.mockResolvedValue(sessionInfo([], { user: null }));
        api.requestRecovery.mockResolvedValue({ minutes: 15 });
        api.completeRecovery.mockResolvedValue(sessionInfo());
        const { element, navigate, stable } = await render(SignInPage, api);

        element.querySelector<HTMLAnchorElement>('.account-foot a')!.click();
        await stable();
        type(element, '#recover-name', 'admin');
        await stable();
        element.querySelector<HTMLButtonElement>('button.primary')!.click();
        await stable();
        expect(element.textContent).toContain('15 minutes');

        type(element, '#recover-code', 'ABCD-EFGH-JKMN-PQRS');
        type(element, '#recover-password', 'new password');
        type(element, '#recover-confirm', 'new password');
        submit(element);
        await stable();

        expect(api.completeRecovery).toHaveBeenCalledWith('admin', 'ABCD-EFGH-JKMN-PQRS', 'new password', false);
        expect(navigate).toHaveBeenCalledWith('/', { replaceUrl: true });
    });
});

describe('SetupPage', () => {
    it('says where the generated code is, creates the administrator and the tenant, and opens the app', async () => {
        const api = mockApi();
        api.setup.mockResolvedValue({ required: true, available: true, codeConfigured: true, codeTooShort: false, recover: false, minimumCodeLength: 12, codeGenerated: true, codeFile: 'data/setup-code.txt' });
        api.completeSetup.mockResolvedValue(sessionInfo([], { user: { id: 1, userName: 'admin', displayName: null, isServerAdmin: true, local: true } }));
        const { element, navigate, stable } = await render(SetupPage, api);

        expect(element.textContent).toContain('data/setup-code.txt');
        type(element, '#setup-code', 'abcd efgh jkmn pqrs');
        type(element, '#setup-name', 'admin');
        type(element, '#setup-password', 'long password');
        type(element, '#setup-confirm', 'long password');
        type(element, '#setup-tenant', 'Family');
        submit(element);
        await stable();

        expect(api.completeSetup).toHaveBeenCalledWith({ code: 'abcd efgh jkmn pqrs', userName: 'admin', password: 'long password', tenantName: 'Family', libraryName: null });
        expect(navigate).toHaveBeenCalledWith('/', { replaceUrl: true });
    });

    it('shows the server\'s refusals next to their fields, and refuses passwords that differ', async () => {
        const api = mockApi();
        api.setup.mockResolvedValue({ required: true, available: true, codeConfigured: true, codeTooShort: false, recover: false, minimumCodeLength: 12, codeGenerated: false, codeFile: null });
        api.completeSetup.mockRejectedValue(new ApiError(400, 'x', { code: ['setup.wrongCode'] }));
        const { element, stable } = await render(SetupPage, api);

        type(element, '#setup-code', 'nope');
        type(element, '#setup-name', 'admin');
        type(element, '#setup-password', 'long password');
        type(element, '#setup-confirm', 'other');
        await stable();
        expect(element.textContent).toContain('The two passwords differ');
        expect(element.querySelector<HTMLButtonElement>('button[type="submit"]')!.disabled).toBe(true);

        type(element, '#setup-confirm', 'long password');
        submit(element);
        await stable();
        expect(element.querySelector('.field-error')?.textContent).toContain('not the server\'s setup code');
    });

    it('sends to the sign-in page when the server is set up already', async () => {
        const api = mockApi();
        api.setup.mockResolvedValue({ required: false, available: false, codeConfigured: false, codeTooShort: false, recover: false, minimumCodeLength: 12, codeGenerated: false, codeFile: null });
        const { element } = await render(SetupPage, api);

        expect(element.textContent).toContain('set up already');
        expect(element.querySelector('form')).toBeNull();
    });
});

describe('AccountPage', () => {
    it('changes the password of a local account and says the other devices are signed out', async () => {
        const api = mockApi();
        api.changePassword.mockResolvedValue(undefined);
        api.settings.mockResolvedValue([settingState()]);
        TestBed.resetTestingModule();
        const { element, stable } = await renderAccount(api, ['account.password', 'account.preferences']);

        type(element, '#current-password', 'old');
        type(element, '#new-password', 'a new one');
        type(element, '#confirm-password', 'a new one');
        element.querySelector('section form')!.dispatchEvent(new Event('submit'));
        await stable();

        expect(api.changePassword).toHaveBeenCalledWith('old', 'a new one');
        expect(element.querySelector('[role="status"]')?.textContent).toContain('other devices are signed out');
        expect(element.querySelector('lib-settings-panel')).not.toBeNull();
    });

    it('shows no password form without the right, and signs out', async () => {
        const api = mockApi();
        api.signOut.mockResolvedValue(sessionInfo([], { user: null }));
        const { element, navigateTo, stable } = await renderAccount(api, []);

        expect(element.querySelector('#current-password')).toBeNull();
        element.querySelector<HTMLButtonElement>('.form-actions .button')!.click();
        await stable();

        expect(api.signOut).toHaveBeenCalled();
        expect(navigateTo).toHaveBeenCalledWith(['/login']);
    });

    async function renderAccount(api: ApiMock, rights: string[]) {
        TestBed.resetTestingModule();
        TestBed.configureTestingModule({ providers: testProviders(api) });
        TestBed.inject(SessionService).set(sessionInfo(rights));
        const router = TestBed.inject(Router);
        const navigateTo = vi.spyOn(router, 'navigate').mockResolvedValue(true);
        const fixture = TestBed.createComponent(AccountPage);
        await fixture.whenStable();
        const element = fixture.nativeElement as HTMLElement;
        return { element, navigateTo, stable: async () => { await settle(); await fixture.whenStable(); } };
    }
});
