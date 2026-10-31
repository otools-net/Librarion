import { TestBed } from '@angular/core/testing';
import { ApiMock, mockApi, settingState, settle, testProviders } from '../../testing/fakes';
import { ApiError } from '../core/api.service';
import { SettingLevel } from '../core/models';
import { SettingsPanel } from './settings-panel';

describe('SettingsPanel', () => {
    async function render(api: ApiMock, level: SettingLevel) {
        TestBed.resetTestingModule();
        TestBed.configureTestingModule({ providers: testProviders(api) });
        const fixture = TestBed.createComponent(SettingsPanel);
        fixture.componentRef.setInput('level', level);
        await fixture.whenStable();
        await settle();
        await fixture.whenStable();
        const element = fixture.nativeElement as HTMLElement;
        return { fixture, element, stable: async () => { await settle(); await fixture.whenStable(); } };
    }

    it('shows each value with where it comes from, and locks only above the user level', async () => {
        const api = mockApi();
        api.settings.mockResolvedValue([
            settingState({ value: 'dark', source: 'server', lockedAbove: true, editable: false }),
            settingState({ key: 'ui.pageSize', kind: 'integer', choices: null, min: 20, max: 200, value: 80, source: 'tenant' }),
        ]);
        const { element } = await render(api, 'tenant');

        const rows = [...element.querySelectorAll('.setting-row')];
        expect(rows[0].querySelector('label')?.textContent).toBe('Colors');
        expect(rows[0].querySelector('select')!.disabled).toBe(true);
        expect(rows[0].textContent).toContain('Locked by a level above');
        expect(rows[1].textContent).toContain('Set at this level');
        expect(element.querySelectorAll('input[type="checkbox"]').length).toBe(2);
        expect(api.settings).toHaveBeenCalledWith('tenant', undefined);
    });

    it('saves only what changed, with the locks', async () => {
        const api = mockApi();
        api.settings.mockResolvedValue([settingState(), settingState({ key: 'ui.density', choices: ['compact', 'comfortable'], value: 'compact' })]);
        api.saveSettings.mockResolvedValue([settingState({ value: 'dark', source: 'server', locked: true })]);
        const { element, stable } = await render(api, 'server');

        const select = element.querySelector<HTMLSelectElement>('select')!;
        select.value = 'dark';
        select.dispatchEvent(new Event('change'));
        const lock = element.querySelector<HTMLInputElement>('input[type="checkbox"]')!;
        lock.checked = true;
        lock.dispatchEvent(new Event('change'));
        await stable();
        element.querySelector('form')!.dispatchEvent(new Event('submit'));
        await stable();

        expect(api.saveSettings).toHaveBeenCalledWith('server', { values: { 'ui.theme': 'dark' }, lock: ['ui.theme'], unlock: [] }, undefined);
        expect(element.querySelector('[role="status"]')?.textContent).toContain('Saved');
    });

    it('shows the server\'s refusal next to its setting', async () => {
        const api = mockApi();
        api.settings.mockResolvedValue([settingState({ key: 'ui.pageSize', kind: 'integer', choices: null, min: 20, max: 200, value: 50 })]);
        api.saveSettings.mockRejectedValue(new ApiError(400, 'x', { 'ui.pageSize': ['setting.invalid'] }));
        const { element, stable } = await render(api, 'user');

        const input = element.querySelector<HTMLInputElement>('input[type="number"]')!;
        input.value = '5000';
        input.dispatchEvent(new Event('input'));
        await stable();
        element.querySelector('form')!.dispatchEvent(new Event('submit'));
        await stable();

        expect(api.saveSettings).toHaveBeenCalledWith('user', { values: { 'ui.pageSize': 5000 }, lock: [], unlock: [] }, undefined);
        expect(element.querySelector('.field-error')?.textContent).toBe('This value doesn\'t fit.');
        expect(element.querySelector('input[type="checkbox"]')).toBeNull();
    });
});
