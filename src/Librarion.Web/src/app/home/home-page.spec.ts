import { TestBed } from '@angular/core/testing';
import { mockApi, settle, systemInfo, testProviders } from '../../testing/fakes';
import { HomePage } from './home-page';

describe('HomePage', () => {
    async function render(api = mockApi()) {
        TestBed.resetTestingModule();
        TestBed.configureTestingModule({ providers: testProviders(api) });
        const fixture = TestBed.createComponent(HomePage);
        await fixture.whenStable();
        return { fixture, element: fixture.nativeElement as HTMLElement };
    }

    it('says it is loading until the server answers', async () => {
        const { element } = await render();

        expect(element.querySelector('[aria-busy="true"]')?.textContent).toContain('Loading');
    });

    it('shows the version and the database the server runs on', async () => {
        const api = mockApi();
        api.systemInfo.mockResolvedValue(systemInfo({ version: '0.3.1', database: 'postgres' }));

        const { fixture, element } = await render(api);
        await settle();
        await fixture.whenStable();

        expect(element.querySelector('.version')?.textContent).toBe('0.3.1');
        expect(element.querySelector('.database')?.textContent).toBe('PostgreSQL');
    });

    it('says when the server can\'t be reached', async () => {
        const api = mockApi();
        api.systemInfo.mockRejectedValue(new Error('offline'));

        const { fixture, element } = await render(api);
        await settle();
        await fixture.whenStable();

        expect(element.querySelector('[role="alert"]')?.textContent).toContain('can\'t be reached');
    });
});
