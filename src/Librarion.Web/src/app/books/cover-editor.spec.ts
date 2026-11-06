import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { ApiMock, mockApi, sessionInfo, settle, testProviders } from '../../testing/fakes';
import { ApiError } from '../core/api.service';
import { BookDetail } from '../core/models';
import { SessionService } from '../core/session.service';
import { BookPage } from './book-page';

const detail = (overrides: Partial<BookDetail> = {}): BookDetail => ({
    id: 42, libraryId: 5, title: 'Good Omens', fileName: 'omens.epub', description: null, rating: null, added: '2026-11-01T10:00:00Z', coverSource: 'book',
    tags: [], files: [], identifiers: [], mergedIntoId: null, properties: [], canDownload: false, canTag: false, canEdit: true, coverVersion: 'v1',
    cover: { template: 1, variant: 2, chosen: false }, ...overrides,
});

describe('The cover editor', () => {
    async function open(api: ApiMock, book = detail()) {
        TestBed.resetTestingModule();
        TestBed.configureTestingModule({ providers: testProviders(api) });
        api.libraries.mockResolvedValue([]);
        api.fields.mockResolvedValue([]);
        api.bookDuplicates.mockResolvedValue([]);
        api.book.mockResolvedValue(book);
        api.coverStyles.mockResolvedValue({ templates: ['classic', 'typographic'], palettes: 3 });
        TestBed.inject(SessionService).set(sessionInfo(['library.browse']));
        TestBed.inject(Router).resetConfig([{ path: 'books/:id', component: BookPage }]);
        const harness = await RouterTestingHarness.create();
        await harness.navigateByUrl('/books/42');
        await settle();
        harness.detectChanges();
        const element = harness.routeNativeElement!;
        const stable = async () => { await settle(); harness.detectChanges(); await harness.fixture.whenStable(); };
        [...element.querySelectorAll<HTMLButtonElement>('.book-actions button')].find(b => b.textContent?.trim() === 'Cover')!.click();
        await stable();
        expect(element.querySelector('lib-cover-editor dialog')?.hasAttribute('open')).toBe(true);
        return { element, stable };
    }

    it('is offered only to those who may edit the book', async () => {
        const api = mockApi();
        api.book.mockResolvedValue(detail({ canEdit: false }));
        TestBed.resetTestingModule();
        TestBed.configureTestingModule({ providers: testProviders(api) });
        api.libraries.mockResolvedValue([]);
        api.fields.mockResolvedValue([]);
        api.bookDuplicates.mockResolvedValue([]);
        TestBed.inject(SessionService).set(sessionInfo(['library.browse']));
        TestBed.inject(Router).resetConfig([{ path: 'books/:id', component: BookPage }]);
        const harness = await RouterTestingHarness.create();
        await harness.navigateByUrl('/books/42');
        await settle();
        harness.detectChanges();
        expect([...harness.routeNativeElement!.querySelectorAll('button')].some(b => b.textContent?.trim() === 'Cover')).toBe(false);
    });

    it('shows a preview of the book in every style and applies the one chosen', async () => {
        const api = mockApi();
        api.coverStyle.mockResolvedValue(detail({ coverSource: 'generated', coverVersion: 'v2', cover: { template: 1, variant: 0, chosen: true } }));
        const { element, stable } = await open(api);

        const previews = [...element.querySelectorAll<HTMLButtonElement>('.cover-style')];
        expect(previews).toHaveLength(6);
        expect(previews[3].querySelector('img')!.getAttribute('src')).toBe('api/books/42/cover/preview/1/0?v=v1');
        expect(previews[3].getAttribute('aria-label')).toBe('Typographic, colors 1');
        expect(element.querySelector('[aria-pressed="true"]')).toBeNull();

        previews[3].click();
        await stable();
        expect(api.coverStyle).toHaveBeenCalledWith(42, 1, 0);
        expect(element.querySelectorAll('.cover-style')[3].getAttribute('aria-pressed')).toBe('true');
        expect(element.querySelector('.book-head img')!.getAttribute('src')).toBe('api/books/42/cover/l?v=v2');
        expect(element.textContent).toContain('Use the book\'s cover');
    });

    it('uploads a picture, refuses one too big before sending it, shows the server\'s refusal, and goes back to the book\'s own', async () => {
        const api = mockApi();
        api.uploadCover.mockRejectedValueOnce(new ApiError(400, 'x', { cover: ['cover.invalid'] }))
            .mockResolvedValueOnce(detail({ coverSource: 'uploaded', coverVersion: 'v3', cover: { template: 1, variant: 2, chosen: true } }));
        api.useOwnCover.mockResolvedValue(detail({ coverVersion: 'v4' }));
        const { element, stable } = await open(api);
        const input = element.querySelector<HTMLInputElement>('.cover-upload input')!;
        const choose = (file: File) => {
            Object.defineProperty(input, 'files', { value: [file], configurable: true });
            input.dispatchEvent(new Event('change'));
        };

        choose(new File([new Uint8Array(5 * 1024 * 1024 + 1)], 'huge.png', { type: 'image/png' }));
        await stable();
        expect(api.uploadCover).not.toHaveBeenCalled();
        expect(element.querySelector('[role="alert"]')?.textContent).toContain('over 5 MB');

        const picture = new File([new Uint8Array(10)], 'cover.png', { type: 'image/png' });
        choose(picture);
        await stable();
        expect(element.querySelector('[role="alert"]')?.textContent).toContain('can\'t be a cover');

        choose(picture);
        await stable();
        expect(api.uploadCover).toHaveBeenLastCalledWith(42, picture);
        expect(element.textContent).toContain('An uploaded picture.');

        [...element.querySelectorAll<HTMLButtonElement>('button')].find(b => b.textContent?.trim() === 'Use the book\'s cover')!.click();
        await stable();
        expect(api.useOwnCover).toHaveBeenCalledWith(42);
        expect(element.textContent).toContain('The book\'s own cover');
    });
});
