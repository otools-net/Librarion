import { TestBed } from '@angular/core/testing';
import { mockApi, settle, testProviders } from '../../testing/fakes';
import { Shell } from './shell';

describe('Shell', () => {
    async function render() {
        TestBed.resetTestingModule();
        TestBed.configureTestingModule({ providers: testProviders(mockApi()) });
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
        const button = element.querySelector<HTMLButtonElement>('.topbar .icon-button:last-child')!;
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
});
