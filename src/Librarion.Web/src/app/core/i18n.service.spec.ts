import { TestBed } from '@angular/core/testing';
import { I18n } from './i18n.service';

describe('I18n', () => {
    function create(): I18n {
        TestBed.resetTestingModule();
        return TestBed.inject(I18n);
    }

    it('gives the English text of a key', () => {
        expect(create().t('common.save')).toBe('Save');
    });

    it('shows the key itself when it has no text, so a missing one is visible', () => {
        expect(create().t('no.such.key')).toBe('no.such.key');
    });

    it('replaces {name} parts with the parameters and leaves unknown ones', () => {
        const i18n = create();

        expect(i18n.t('common.save', { unused: 1 })).toBe('Save');
        // A text with parts, through a key that doesn't exist: the key is the text.
        expect(i18n.t('{count} books by {author}', { count: 3 })).toBe('3 books by {author}');
    });

    it('falls back to English for a language it has no messages for, and sets the page language', () => {
        const i18n = create();

        i18n.use('xx-YY');

        expect(i18n.language()).toBe('en');
        expect(document.documentElement.lang).toBe('en');
    });

    it('keeps the chosen language for this browser', () => {
        create().choose('en-GB');

        expect(localStorage.getItem('librarion.language')).toBe('en-GB');
        expect(create().language()).toBe('en');
    });
});
