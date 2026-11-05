import { TestBed } from '@angular/core/testing';
import { mockApi, settle, testProviders } from '../../testing/fakes';
import { ConfirmDialog, ConfirmService } from './confirm';

describe('ConfirmDialog', () => {
    async function render() {
        TestBed.resetTestingModule();
        TestBed.configureTestingModule({ providers: testProviders(mockApi()) });
        const fixture = TestBed.createComponent(ConfirmDialog);
        await fixture.whenStable();
        const element = fixture.nativeElement as HTMLElement;
        const service = TestBed.inject(ConfirmService);
        const stable = async () => { await settle(); await fixture.whenStable(); };
        const button = (text: string) => [...element.querySelectorAll<HTMLButtonElement>('button')].find(b => b.textContent?.trim() === text)!;
        return { fixture, element, service, stable, button };
    }

    it('asks in a dialog and answers true only when the user goes ahead', async () => {
        const { element, service, stable, button } = await render();

        const answer = service.ask({ title: 'Delete the library?', message: 'Delete Books?', action: 'Delete', danger: true });
        await stable();
        const dialog = element.querySelector('dialog')!;
        expect(dialog.hasAttribute('open')).toBe(true);
        expect(dialog.querySelector('h2')?.textContent).toBe('Delete the library?');
        expect(dialog.textContent).toContain('Delete Books?');
        expect(button('Delete').classList).toContain('danger');

        button('Delete').click();
        expect(await answer).toBe(true);
        await stable();
        expect(element.querySelector('dialog')).toBeNull();
    });

    it('answers false on Cancel and on the close button', async () => {
        const { service, stable, button, element } = await render();

        const cancelled = service.ask({ title: 'T', message: 'M', action: 'Go' });
        await stable();
        button('Cancel').click();
        expect(await cancelled).toBe(false);

        const closed = service.ask({ title: 'T', message: 'M', action: 'Go' });
        await stable();
        element.querySelector<HTMLButtonElement>('.sheet-head button')!.click();
        expect(await closed).toBe(false);
    });

    it('keeps the button off until the name is typed', async () => {
        const { element, service, stable, button } = await render();

        const answer = service.ask({ title: 'Delete the tenant?', message: 'M', action: 'Delete', danger: true, typeToConfirm: 'Home' });
        await stable();
        expect(button('Delete').disabled).toBe(true);
        const input = element.querySelector<HTMLInputElement>('dialog input')!;
        input.value = 'home';
        input.dispatchEvent(new Event('input'));
        await stable();
        expect(button('Delete').disabled).toBe(true);
        input.value = 'Home';
        input.dispatchEvent(new Event('input'));
        await stable();
        button('Delete').click();
        expect(await answer).toBe(true);
    });

    it('cancels the open question when another comes', async () => {
        const { service, stable } = await render();

        const first = service.ask({ title: 'A', message: 'A', action: 'Go' });
        const second = service.ask({ title: 'B', message: 'B', action: 'Go' });
        await stable();
        expect(await first).toBe(false);
        service.answer(true);
        expect(await second).toBe(true);
    });
});
