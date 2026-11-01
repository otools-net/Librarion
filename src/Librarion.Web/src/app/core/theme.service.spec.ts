import { TestBed } from '@angular/core/testing';
import { setMediaQuery } from '../../testing/fakes';
import { ThemeService } from './theme.service';

describe('ThemeService', () => {
    function create(): ThemeService {
        TestBed.resetTestingModule();
        return TestBed.inject(ThemeService);
    }

    it('follows the device without a choice, live', () => {
        setMediaQuery('(prefers-color-scheme: light)', true);
        const theme = create();
        expect(theme.current()).toBe('light');
        expect(document.documentElement.dataset['theme']).toBe('light');

        setMediaQuery('(prefers-color-scheme: light)', false);

        expect(theme.current()).toBe('dark');
        expect(document.documentElement.dataset['theme']).toBe('dark');
    });

    it('keeps a choice for this browser over the device, under the key theme-boot.ts reads', () => {
        setMediaQuery('(prefers-color-scheme: light)', true);
        const theme = create();

        theme.toggle();

        expect(theme.current()).toBe('dark');
        expect(localStorage.getItem('librarion.theme')).toBe('dark');
        expect(create().current()).toBe('dark');
    });

    it('follows the device again when the choice is cleared', () => {
        const theme = create();
        theme.choose('light');

        theme.choose('');

        expect(theme.current()).toBe('dark');
    });
});
