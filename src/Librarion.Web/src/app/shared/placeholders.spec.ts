import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { mockApi, testProviders } from '../../testing/fakes';
import { EmptyState, Skeleton } from './placeholders';

@Component({
    imports: [EmptyState, Skeleton],
    template: `
        <ul><li class="row" libEmpty="folder">No folders yet.</li></ul>
        <lib-skeleton shape="form" [rows]="4" />
    `,
})
class Host {}

describe('placeholders', () => {
    async function render() {
        TestBed.resetTestingModule();
        TestBed.configureTestingModule({ providers: testProviders(mockApi()) });
        const fixture = TestBed.createComponent(Host);
        await fixture.whenStable();
        return fixture.nativeElement as HTMLElement;
    }

    it('shows an empty list with its icon and text', async () => {
        const element = await render();
        const empty = element.querySelector('li.empty')!;

        expect(empty.querySelector('use')?.getAttribute('href')).toBe('#icon-folder');
        expect(empty.querySelector('.empty-text')?.textContent).toBe('No folders yet.');
    });

    it('says it is loading to screen readers, its bars hidden from them', async () => {
        const element = await render();
        const skeleton = element.querySelector('lib-skeleton')!;

        expect(skeleton.getAttribute('role')).toBe('status');
        expect(skeleton.getAttribute('aria-busy')).toBe('true');
        expect(skeleton.classList).toContain('skeleton-form');
        expect(skeleton.querySelector('.visually-hidden')?.textContent).toBe('Loading…');
        expect(skeleton.querySelectorAll('.skeleton-line[aria-hidden="true"]').length).toBe(4);
    });
});
