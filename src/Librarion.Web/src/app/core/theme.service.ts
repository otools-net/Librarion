import { Injectable, signal } from '@angular/core';
import { readPreference, writePreference } from './preferences';

// Light or dark colors, as `data-theme` on <html> (styles/tokens.css). This browser's pick wins; without one the
// device's mode is followed, live. src/theme-boot.ts applies the same key before the page is drawn: keep them in step.
const CHOICE = 'librarion.theme';     // light | dark; empty = follow the device
export type Theme = 'light' | 'dark';

@Injectable({ providedIn: 'root' })
export class ThemeService {
    private readonly deviceLight = matchMedia('(prefers-color-scheme: light)');
    private readonly shown = signal<Theme>('dark');

    /** The theme on screen now. */
    readonly current = this.shown.asReadonly();

    constructor() {
        this.deviceLight.addEventListener('change', () => this.apply());
        this.apply();
    }

    /** Keeps `theme` for this browser; `''` follows the device again. */
    choose(theme: Theme | ''): void {
        writePreference(CHOICE, theme);
        this.apply();
    }

    /** The other theme, kept for this browser. */
    toggle(): void {
        this.choose(this.current() === 'dark' ? 'light' : 'dark');
    }

    private apply(): void {
        const choice = readPreference(CHOICE, '');
        const theme: Theme = choice === 'light' || choice === 'dark' ? choice : this.deviceLight.matches ? 'light' : 'dark';
        this.shown.set(theme);
        document.documentElement.dataset['theme'] = theme;
    }
}
