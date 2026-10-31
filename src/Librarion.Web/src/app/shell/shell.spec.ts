import { TestBed } from '@angular/core/testing';
import { mockApi, sessionInfo, settle, testProviders } from '../../testing/fakes';
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
        expect(items.map(item => item.textContent?.trim())).toEqual(['Home', 'Library', 'Authors', 'Series', 'Tags']);
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

        element.querySelector('.drawer-backdrop')!.dispatchEvent(new Event('pointerdown'));
        await fixture.whenStable();
        expect(element.querySelector('.sidebar')!.classList).not.toContain('open');
        expect(element.querySelector('.drawer-backdrop')).toBeNull();

        button.click();
        await fixture.whenStable();
        document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));
        await fixture.whenStable();
        expect(element.querySelector('.sidebar')!.classList).not.toContain('open');
    });

    it('switches the theme from the top bar, saying what the button will do', async () => {
        const { fixture, element } = await render();
        const button = element.querySelector<HTMLButtonElement>('.topbar .theme-button')!;
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

        const admin = (await render(sessionInfo(['tenant.users']))).element;
        expect(admin.querySelector('.sidebar a[href="/admin"]')?.textContent).toContain('Administration');
        expect(admin.querySelector<HTMLAnchorElement>('.account-button')!.title).toBe('My account (ana)');
    });

    it('lets a server administrator switch between tenants from the top bar', async () => {
        const api = mockApi();
        api.switchTenant.mockResolvedValue(sessionInfo([], { tenant: { id: 2, slug: 'club', name: 'Club' } }));
        const session = sessionInfo(['server.tenants'], {
            user: { id: 1, userName: 'admin', displayName: null, isServerAdmin: true, local: true },
            tenants: [{ id: 1, slug: 'home', name: 'Home' }, { id: 2, slug: 'club', name: 'Club' }],
        });
        expect((await render()).element.querySelector('.tenant-switch')).toBeNull();

        const { element } = await render(session, api);
        const select = element.querySelector<HTMLSelectElement>('.tenant-switch')!;
        select.value = '2';
        select.dispatchEvent(new Event('change'));
        await settle();

        expect(api.switchTenant).toHaveBeenCalledWith(2);
    });
});
