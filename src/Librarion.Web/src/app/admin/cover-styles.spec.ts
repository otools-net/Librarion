import { TestBed } from '@angular/core/testing';
import { ApiMock, mockApi, sessionInfo, settle, testProviders } from '../../testing/fakes';
import { ApiError } from '../core/api.service';
import { CoverTemplates } from '../core/models';
import { SessionService } from '../core/session.service';
import { TaskWatch } from '../core/task-watch';
import { ToastService } from '../core/toast.service';
import { ConfirmService } from '../shared/confirm';
import { CoverStylesSection } from './cover-styles-section';

const templates = (...on: boolean[]): CoverTemplates => ({
    templates: ['classic', 'typographic', 'geometric'].map((name, index) => ({ index, name, on: on[index] })),
    palettes: 2,
});

async function render(api: ApiMock) {
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({ providers: testProviders(api) });
    TestBed.inject(SessionService).set(sessionInfo(['tenant.coverStyles']));
    const fixture = TestBed.createComponent(CoverStylesSection);
    await fixture.whenStable();
    await settle();
    await fixture.whenStable();
    const element = fixture.nativeElement as HTMLElement;
    return { element, stable: async () => { await settle(); await fixture.whenStable(); } };
}

const switches = (element: HTMLElement) => [...element.querySelectorAll<HTMLInputElement>('input[role="switch"]')];
const button = (element: HTMLElement, text: string) => [...element.querySelectorAll<HTMLButtonElement>('button')].find(b => b.textContent?.trim() === text);

describe('CoverStylesSection', () => {
    it('shows each template with a sample in every palette and its switch', async () => {
        const api = mockApi();
        api.coverTemplates.mockResolvedValue(templates(true, true, false));
        const { element } = await render(api);

        const panels = [...element.querySelectorAll('.cover-template')];
        expect(panels.map(p => p.querySelector('h2')!.textContent!.trim())).toEqual(['Classic', 'Typographic', 'Geometric']);
        const samples = panels[1].querySelectorAll('img');
        expect(samples).toHaveLength(2);
        expect(samples[1].getAttribute('src')).toBe('api/covers/templates/1/preview/1?title=The%20Title%20of%20a%20Book&author=An%20Author');
        expect(switches(element).map(s => s.checked)).toEqual([true, true, false]);
        expect(switches(element)[0].getAttribute('aria-label')).toBe('Classic for new books');
        expect(panels[2].classList).toContain('off');
        expect(panels.filter(p => p.querySelector('.cover-template-off'))).toEqual([panels[2]]);
    });

    it('turns a template off, keeps the last one on, and shows a refusal', async () => {
        const api = mockApi();
        api.coverTemplates.mockResolvedValue(templates(true, true, false));
        api.setCoverTemplate.mockResolvedValueOnce(templates(false, true, false))
            .mockRejectedValueOnce(new ApiError(400, 'x', { template: ['coverStyle.lastOn'] }));
        const { element, stable } = await render(api);

        switches(element)[0].click();
        await stable();
        expect(api.setCoverTemplate).toHaveBeenCalledWith(0, false);
        expect(switches(element).map(s => s.checked)).toEqual([false, true, false]);
        expect(switches(element)[1].disabled).toBe(true);

        // Another session turned it off meanwhile: the server refuses, the switch goes back.
        switches(element)[0].click();
        await stable();
        expect(api.setCoverTemplate).toHaveBeenLastCalledWith(0, true);
        expect(switches(element)[0].checked).toBe(false);
        expect(element.querySelector('[role="alert"]')?.textContent).toContain('One cover style stays on.');
    });

    it('gives the books of a template turned off another, as a run, after asking', async () => {
        const api = mockApi();
        api.coverTemplates.mockResolvedValue(templates(true, true, false));
        api.reassignCoverTemplate.mockResolvedValue({ runId: 51 });
        const ask = vi.spyOn(ConfirmService.prototype, 'ask').mockResolvedValueOnce(false).mockResolvedValueOnce(true);
        const follow = vi.spyOn(TaskWatch.prototype, 'follow').mockImplementation(() => { });
        const toast = vi.spyOn(ToastService.prototype, 'show');
        const { element, stable } = await render(api);

        button(element, 'Give its books another style')!.click();
        await stable();
        expect(api.reassignCoverTemplate).not.toHaveBeenCalled();
        button(element, 'Give its books another style')!.click();
        await stable();

        expect(ask).toHaveBeenLastCalledWith(expect.objectContaining({ title: 'Give the books of Geometric another style?' }));
        expect(api.reassignCoverTemplate).toHaveBeenCalledWith(2);
        expect(follow).toHaveBeenCalledWith(51);
        expect(toast).toHaveBeenCalledWith('Giving the books of Geometric another style…');
    });
});
