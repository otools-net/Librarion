import { TestBed } from '@angular/core/testing';
import { ApiMock, mockApi, sessionInfo, settle, testProviders } from '../../testing/fakes';
import { TagDuplicateItem, TaskRunInfo } from '../core/models';
import { SessionService } from '../core/session.service';
import { TaskWatch } from '../core/task-watch';
import { ConfirmService } from '../shared/confirm';
import { TagDuplicatesSection } from './tag-duplicates-section';

async function render(api: ApiMock) {
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({ providers: testProviders(api) });
    TestBed.inject(SessionService).set(sessionInfo(['tenant.tags']));
    const fixture = TestBed.createComponent(TagDuplicatesSection);
    const stable = async () => {
        for (let i = 0; i < 3; i++) {
            await settle();
            await fixture.whenStable();
        }
    };
    await stable();
    return { element: fixture.nativeElement as HTMLElement, stable };
}

const button = (element: HTMLElement, text: string) => [...element.querySelectorAll<HTMLButtonElement>('button')].find(b => b.textContent?.trim() === text);

const kings: TagDuplicateItem = {
    id: 7, level: 'sound', books: 12,
    tags: [{ id: 1, name: 'Stephen King', books: 10 }, { id: 2, name: 'Стивен Кинг', books: 1 }, { id: 3, name: 'Steven King', books: 1 }],
};

function setUp(): ApiMock {
    const api = mockApi();
    api.managedFields.mockResolvedValue({
        fields: [{ id: 3, key: 'author', name: 'author', builtIn: true, color: null, isHidden: false, values: 3, capped: false }], plainTags: 0, plainCapped: false,
    });
    return api;
}

describe('TagDuplicatesSection', () => {
    it('lists the authors\' groups first, and merges the others into the tag kept after asking', async () => {
        const api = setUp();
        api.tagDuplicates.mockResolvedValue({ items: [kings], next: null, built: true });
        api.mergeTag.mockResolvedValueOnce({ runId: null, logId: 1, links: 1 }).mockResolvedValueOnce({ runId: 40, logId: 2, links: 1 });
        const ask = vi.spyOn(ConfirmService.prototype, 'ask').mockResolvedValue(true);
        const follow = vi.spyOn(TaskWatch.prototype, 'follow').mockImplementation(() => { });
        const { element, stable } = await render(api);

        expect(api.tagDuplicates).toHaveBeenCalledWith('author', null);
        expect(element.querySelector('.duplicate-reason')?.textContent).toBe('Sound alike');
        expect(element.querySelector('.tag-duplicate-tags')?.textContent).toContain('10 books');
        expect(button(element, 'Merge into “Stephen King”')).toBeDefined();
        element.querySelectorAll<HTMLInputElement>('input[type="radio"]')[2].click();
        await stable();
        button(element, 'Merge into “Steven King”')!.click();
        await stable();

        expect(ask).toHaveBeenCalledWith(expect.objectContaining({ message: expect.stringContaining('Stephen King, Стивен Кинг are merged into “Steven King”') }));
        expect(api.mergeTag.mock.calls).toEqual([[1, 3], [2, 3]]);
        expect(follow).toHaveBeenCalledWith(40);
        expect(element.querySelector('.duplicate-group')).toBeNull();
    });

    it('sets a group apart, and reads the list again when Find again ends', async () => {
        const api = setUp();
        api.tagDuplicates.mockResolvedValue({ items: [kings], next: null, built: false });
        api.notSameTags.mockResolvedValue(undefined);
        api.buildTagDuplicates.mockResolvedValue({ runId: 41 });
        vi.spyOn(TaskWatch.prototype, 'follow').mockImplementation(() => { });
        const { element, stable } = await render(api);

        expect(element.textContent).toContain('haven\'t been looked for yet');
        button(element, 'Not the same')!.click();
        await stable();
        expect(api.notSameTags).toHaveBeenCalledWith([1, 2, 3]);
        expect(element.querySelector('.duplicate-group')).toBeNull();

        button(element, 'Find again')!.click();
        await stable();
        const calls = api.tagDuplicates.mock.calls.length;
        TestBed.inject(TaskWatch).ended.set([{ id: 41 } as TaskRunInfo]);
        await stable();
        expect(api.tagDuplicates.mock.calls.length).toBe(calls + 1);
    });
});
