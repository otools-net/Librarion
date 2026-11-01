import { TestBed } from '@angular/core/testing';
import { ToastService } from './toast.service';

describe('ToastService', () => {
    beforeEach(() => vi.useFakeTimers());
    afterEach(() => vi.useRealTimers());

    function service(): ToastService {
        TestBed.resetTestingModule();
        return TestBed.inject(ToastService);
    }

    it('removes a message after its time, an error later', () => {
        const toasts = service();
        toasts.show('Saved.');
        toasts.error('Failed.');
        expect(toasts.toasts().map(t => [t.message, t.error])).toEqual([['Saved.', false], ['Failed.', true]]);

        vi.advanceTimersByTime(ToastService.timeout);
        expect(toasts.toasts().map(t => t.message)).toEqual(['Failed.']);
        vi.advanceTimersByTime(ToastService.errorTimeout - ToastService.timeout);
        expect(toasts.toasts()).toEqual([]);
    });

    it('keeps them all while held, then gives each its time again', () => {
        const toasts = service();
        toasts.show('One');
        toasts.hold();
        toasts.show('Two');
        vi.advanceTimersByTime(ToastService.timeout * 3);
        expect(toasts.toasts().length).toBe(2);

        toasts.release();
        vi.advanceTimersByTime(ToastService.timeout);
        expect(toasts.toasts()).toEqual([]);
    });

    it('closes one on demand', () => {
        const toasts = service();
        toasts.show('One');
        toasts.show('Two');
        toasts.dismiss(toasts.toasts()[0].id);
        expect(toasts.toasts().map(t => t.message)).toEqual(['Two']);
    });
});
