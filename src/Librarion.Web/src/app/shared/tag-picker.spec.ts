import { TestBed } from '@angular/core/testing';
import { ApiMock, mockApi, settle, testProviders } from '../../testing/fakes';
import { PickedTag } from '../core/models';
import { TagPicker } from './tag-picker';

const picked = (id: number, name: string, more: Partial<PickedTag> = {}): PickedTag =>
    ({ id, name, color: null, field: null, shorthand: null, alias: null, parent: null, books: 3, ...more });

describe('TagPicker', () => {
    async function render(api: ApiMock, inputs: Record<string, unknown> = {}) {
        TestBed.resetTestingModule();
        TestBed.configureTestingModule({ providers: testProviders(api) });
        const fixture = TestBed.createComponent(TagPicker);
        fixture.componentRef.setInput('label', 'Add a tag');
        for (const [name, value] of Object.entries(inputs))
            fixture.componentRef.setInput(name, value);
        const chosen: PickedTag[] = [];
        const created: string[] = [];
        fixture.componentInstance.picked.subscribe(tag => chosen.push(tag));
        fixture.componentInstance.create.subscribe(name => created.push(name));
        await fixture.whenStable();
        const element = fixture.nativeElement as HTMLElement;
        const box = element.querySelector('input')!;
        const type = async (text: string) => {
            box.focus();
            box.value = text;
            box.dispatchEvent(new Event('input'));
            await settle(200);
            await fixture.whenStable();
        };
        const press = async (key: string) => {
            const event = new KeyboardEvent('keydown', { key, cancelable: true, bubbles: true });
            box.dispatchEvent(event);
            await settle();
            await fixture.whenStable();
            return event;
        };
        const options = () => [...element.querySelectorAll<HTMLElement>('[role="option"]')];
        return { fixture, element, box, type, press, options, chosen, created };
    }

    it('lists what the server found for the field, each with its color, other name, parent, shorthand and books', async () => {
        const api = mockApi();
        api.pickTags.mockResolvedValue({
            items: [picked(1, 'Speculative fiction', { color: '#aa0000', shorthand: 'SF', alias: 'Weird tales', books: 1234 }), picked(2, 'Freddy', { parent: 'FNAF' })],
            exact: false,
        });
        const { type, options, box } = await render(api, { field: 'category' });

        await type('we');

        expect(api.pickTags).toHaveBeenLastCalledWith('category', 'we', 10);
        const [first, second] = options();
        expect(first.querySelector('.tag-dot')?.getAttribute('style')).toContain('background: rgb(170, 0, 0)');
        expect(first.textContent).toContain('Also called Weird tales');
        expect(first.querySelector('.tag-picker-shorthand')?.textContent).toBe('SF');
        expect(first.querySelector('.tag-picker-books')?.textContent).toBe('1,234 books');
        expect(second.querySelector('.tag-picker-parent')?.textContent).toBe('(FNAF)');
        expect(box.getAttribute('aria-expanded')).toBe('true');
        expect(box.getAttribute('aria-activedescendant')).toBe(first.id);
        expect(first.getAttribute('aria-selected')).toBe('true');
    });

    it('chooses with the arrows and Enter, or a tap, then empties the box', async () => {
        const api = mockApi();
        api.pickTags.mockResolvedValue({ items: [picked(1, 'Fantasy'), picked(2, 'Fantastic')], exact: false });
        const { type, press, options, box, chosen } = await render(api);

        await type('fant');
        await press('ArrowDown');
        expect(options()[1].getAttribute('aria-selected')).toBe('true');
        await press('ArrowDown');
        expect(options()[0].getAttribute('aria-selected')).toBe('true');
        await press('ArrowUp');
        const enter = await press('Enter');

        expect(enter.defaultPrevented).toBe(true);
        expect(chosen.map(t => t.id)).toEqual([2]);
        expect(box.value).toBe('');
        expect(options()).toHaveLength(0);

        await type('fant');
        options()[0].click();
        expect(chosen.map(t => t.id)).toEqual([2, 1]);
    });

    it('leaves out the excluded tags and offers Create only when no tag has the typed name', async () => {
        const api = mockApi();
        api.pickTags.mockResolvedValueOnce({ items: [picked(1, 'Fantasy'), picked(2, 'Fantasy noir')], exact: false })
            .mockResolvedValueOnce({ items: [picked(1, 'Fantasy')], exact: true });
        const { type, options, created } = await render(api, { exclude: [1], allowCreate: true });

        await type('Fantasy n');
        expect(options().map(o => o.textContent?.trim())).toEqual([expect.stringContaining('Fantasy noir'), 'Create “Fantasy n”']);
        options()[1].click();
        expect(created).toEqual(['Fantasy n']);

        await type('fantasy');
        expect(options()).toHaveLength(0);
    });

    it('closes its list on Escape without closing the dialog around it, and lists the first tags on an arrow in an empty box', async () => {
        const api = mockApi();
        api.pickTags.mockResolvedValue({ items: [picked(1, 'Alpha')], exact: false });
        const { type, press, options } = await render(api);

        await type('al');
        const escape = await press('Escape');
        expect(escape.defaultPrevented).toBe(true);
        expect(options()).toHaveLength(0);
        await type('');
        await press('ArrowDown');
        await settle();
        expect(api.pickTags).toHaveBeenLastCalledWith(null, '', 10);
        expect(options()).toHaveLength(1);
    });

    it('drops an answer overtaken by a later one, and says when the lookup failed', async () => {
        const api = mockApi();
        let slow: (value: { items: PickedTag[]; exact: boolean }) => void = () => { };
        api.pickTags.mockReturnValueOnce(new Promise(resolve => slow = resolve))
            .mockResolvedValueOnce({ items: [picked(2, 'Beta')], exact: false })
            .mockRejectedValueOnce(new Error('offline'));
        const { type, options, element, fixture } = await render(api);

        await type('a');
        await type('be');
        slow({ items: [picked(1, 'Alpha')], exact: false });
        await settle();
        await fixture.whenStable();
        expect(options().map(o => o.textContent)).toEqual([expect.stringContaining('Beta')]);

        await type('bet');
        expect(element.querySelector('[role="status"]')?.textContent).toContain("Couldn't look up tags");
    });
});
