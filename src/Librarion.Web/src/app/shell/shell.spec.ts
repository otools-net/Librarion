import { TestBed } from '@angular/core/testing';
import { mockApi, sessionInfo, settle, testProviders } from '../../testing/fakes';
import { Router } from '@angular/router';
import { LibraryContext } from '../core/library-context';
import { SessionService } from '../core/session.service';
import { Shell } from './shell';

describe('Shell', () => {
    async function render(session = sessionInfo(), api = mockApi()) {
        TestBed.resetTestingModule();
        TestBed.configureTestingModule({ providers: testProviders(api) });
        TestBed.inject(SessionService).set(session);
        const fixture = TestBed.createComponent(Shell);
        await fixture.whenStable();
        return { fixture, element: fixture.nativeElement as HTMLElement };
    }

    it('lists the navigation, the pages not built yet marked as such', async () => {
        const { element } = await render();

        const items = [...element.querySelectorAll('.sidebar .nav-item')];
        expect(items.map(item => item.querySelector('span')?.textContent?.trim())).toEqual(['Home', 'Library', 'Authors', 'Series', 'Tags']);
        expect(items[1].querySelector('.badge')?.textContent).toBe('Soon');
        expect(items[0].tagName).toBe('A');
        expect(items.slice(1).every(item => item.getAttribute('aria-disabled') === 'true')).toBe(true);
    });

    it('shows the logo as a decorative mark beside the name, the link named for screen readers', async () => {
        const { element } = await render();

        const brand = element.querySelector<HTMLAnchorElement>('.brand')!;
        const logo = brand.querySelector('svg.logo')!;
        expect(brand.getAttribute('aria-label')).toBe('Librarion');
        expect(logo.getAttribute('aria-hidden')).toBe('true');
        expect(logo.getAttribute('viewBox')).toBe('0 0 56 56');
        expect(logo.querySelectorAll('.logo-blue, .logo-gold, .logo-detail').length).toBeGreaterThan(0);
        expect(brand.querySelector('img')).toBeNull();
    });

    it('opens the drawer from the menu button and closes it on the backdrop or Escape', async () => {
        const { fixture, element } = await render();
        const button = element.querySelector<HTMLButtonElement>('.menu-button')!;

        button.click();
        await fixture.whenStable();
        expect(element.querySelector('.sidebar')!.classList).toContain('open');
        expect(button.getAttribute('aria-expanded')).toBe('true');
        expect(button.title).toBe('Close the menu');

        element.querySelector('.shell-backdrop')!.dispatchEvent(new Event('pointerdown'));
        await fixture.whenStable();
        expect(element.querySelector('.sidebar')!.classList).not.toContain('open');
        expect(element.querySelector('.shell-backdrop')).toBeNull();

        button.click();
        await fixture.whenStable();
        document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));
        await fixture.whenStable();
        expect(element.querySelector('.sidebar')!.classList).not.toContain('open');
    });

    it('switches the theme from the account menu, saying what the button will do', async () => {
        const { fixture, element } = await render();
        element.querySelector<HTMLButtonElement>('.account-button')!.click();
        await fixture.whenStable();
        const button = element.querySelector<HTMLButtonElement>('#account-menu .theme-button')!;
        expect(document.documentElement.dataset['theme']).toBe('dark');
        expect(button.title).toBe('Switch to light colors');

        button.click();
        await fixture.whenStable();
        await settle();

        expect(document.documentElement.dataset['theme']).toBe('light');
        expect(button.title).toBe('Switch to dark colors');
    });

    it('gives every button a hint of what it does', async () => {
        const { element } = await render();

        const buttons = [...element.querySelectorAll('button')];
        expect(buttons.length).toBeGreaterThan(0);
        expect(buttons.filter(button => !button.title)).toEqual([]);
    });

    it('links the administration only for a session with an administration right', async () => {
        const plain = (await render()).element;
        expect(plain.querySelector('a[href="/admin"]')).toBeNull();

        const { element: admin, fixture } = await render(sessionInfo(['tenant.users']));
        expect(admin.querySelector('.sidebar .nav-foot a[href="/admin"]')?.textContent).toContain('Administration');
        const account = admin.querySelector<HTMLButtonElement>('.account-button')!;
        expect(account.title).toBe('Account menu (ana)');
        expect(account.textContent?.trim()).toBe('A');
        account.click();
        await fixture.whenStable();
        const menu = admin.querySelector('#account-menu')!;
        expect([...menu.querySelectorAll('.menu-item')].map(item => item.textContent?.trim()))
            .toEqual(['My account', 'Administration', 'Switch to light colors', 'Sign out']);
    });

    it('lets a server administrator switch between tenants from the top bar', async () => {
        const api = mockApi();
        api.switchTenant.mockResolvedValue(sessionInfo([], { tenant: { id: 2, slug: 'club', name: 'Club' } }));
        const session = sessionInfo(['server.tenants'], {
            user: { id: 1, userName: 'admin', displayName: null, isServerAdmin: true, local: true },
            tenants: [{ id: 1, slug: 'home', name: 'Home' }, { id: 2, slug: 'club', name: 'Club' }],
        });
        const plain = await render();
        plain.element.querySelector<HTMLButtonElement>('.account-button')!.click();
        await plain.fixture.whenStable();
        expect(plain.element.querySelector('.tenant-item')).toBeNull();

        const { element, fixture } = await render(session, api);
        element.querySelector<HTMLButtonElement>('.account-button')!.click();
        await fixture.whenStable();
        const tenants = [...element.querySelectorAll<HTMLButtonElement>('.tenant-item')];
        expect(tenants.map(item => [item.textContent?.trim(), item.getAttribute('aria-pressed')])).toEqual([['Home', 'true'], ['Club', 'false']]);
        tenants[1].click();
        await settle();

        expect(api.switchTenant).toHaveBeenCalledWith(2);
    });

    it('closes the account menu on a press outside it, and keeps it on a press inside', async () => {
        const { fixture, element } = await render();
        element.querySelector<HTMLButtonElement>('.account-button')!.click();
        await fixture.whenStable();

        element.querySelector('#account-menu .menu-head')!.dispatchEvent(new Event('pointerdown', { bubbles: true }));
        await fixture.whenStable();
        expect(element.querySelector('#account-menu')).not.toBeNull();

        element.querySelector('main')!.dispatchEvent(new Event('pointerdown', { bubbles: true }));
        await fixture.whenStable();
        expect(element.querySelector('#account-menu')).toBeNull();
    });

    it('switches the library being browsed, remembered by the browser', async () => {
        const api = mockApi();
        api.libraries.mockResolvedValue([
            { id: 5, tenantId: 1, name: 'Books', created: '' },
            { id: 6, tenantId: 1, name: 'Comics', created: '' },
        ]);
        const { fixture, element } = await render(sessionInfo(['library.browse']), api);
        await settle();
        await fixture.whenStable();

        const button = element.querySelector<HTMLButtonElement>('.switch-button')!;
        expect(button.textContent?.trim()).toBe('Books');
        button.click();
        await fixture.whenStable();
        const choices = [...element.querySelectorAll<HTMLButtonElement>('#library-menu .menu-item')];
        expect(choices.map(choice => choice.getAttribute('aria-pressed'))).toEqual(['true', 'false']);
        choices[1].click();
        await fixture.whenStable();

        expect(TestBed.inject(LibraryContext).current()?.name).toBe('Comics');
        expect(localStorage.getItem('librarion.library')).toBe('6');
        expect(element.querySelector('#library-menu')).toBeNull();
        expect(element.querySelector('.switch-button')?.textContent?.trim()).toBe('Comics');
        localStorage.removeItem('librarion.library');
    });

    it('shows one library by name, without a menu, and none without libraries', async () => {
        const api = mockApi();
        api.libraries.mockResolvedValue([{ id: 5, tenantId: 1, name: 'Books', created: '' }]);
        const { fixture, element } = await render(sessionInfo(['library.browse']), api);
        await settle();
        await fixture.whenStable();
        expect(element.querySelector('.library-name')?.textContent?.trim()).toBe('Books');
        expect(element.querySelector('.switch-button')).toBeNull();

        expect((await render()).element.querySelector('.library-name, .switch-button')).toBeNull();
    });

    it('in the administration, gives way to a back arrow: no library sidebar, switcher or search', async () => {
        const { fixture, element } = await render(sessionInfo(['tenant.users']));
        const router = TestBed.inject(Router);
        router.resetConfig([{ path: '**', children: [] }]);
        await router.navigateByUrl('/admin/users');
        await fixture.whenStable();

        expect(element.classList).toContain('admin-frame');
        expect(element.querySelector('#main-nav')).toBeNull();
        expect(element.querySelector('.search')).toBeNull();
        expect(element.querySelector('.menu-button')).toBeNull();
        expect(element.querySelector('.topbar-title')?.textContent).toBe('Administration');

        element.querySelector<HTMLButtonElement>('.back-button')!.click();
        await settle();
        await fixture.whenStable();
        expect(router.url).toBe('/');
        expect(element.querySelector('#main-nav')).not.toBeNull();
    });
});
