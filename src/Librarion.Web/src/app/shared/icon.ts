import { ChangeDetectionStrategy, Component, input } from '@angular/core';

export type IconName = 'menu' | 'close' | 'search' | 'sun' | 'moon' | 'home' | 'library' | 'author' | 'series' | 'tag' | 'settings' | 'user';

/** `<svg libIcon="menu"></svg>`: a symbol of the bundled sprite (`IconSprite`, once in the shell). Decorative. */
@Component({
    selector: 'svg[libIcon]',
    template: '<svg:use [attr.href]="\'#icon-\' + libIcon()" />',
    host: { class: 'icon', 'aria-hidden': 'true', focusable: 'false' },
    changeDetection: ChangeDetectionStrategy.OnPush,
})
export class Icon {
    readonly libIcon = input.required<IconName>();
}

/** The icons, drawn on a 24 × 24 grid with `currentColor` strokes (no icon font, nothing loaded from elsewhere). */
@Component({
    selector: 'lib-icon-sprite',
    template: `
        <svg xmlns="http://www.w3.org/2000/svg" class="icon-sprite" aria-hidden="true">
            <symbol id="icon-menu" viewBox="0 0 24 24"><path d="M4 6h16M4 12h16M4 18h16" /></symbol>
            <symbol id="icon-close" viewBox="0 0 24 24"><path d="M6 6l12 12M18 6 6 18" /></symbol>
            <symbol id="icon-search" viewBox="0 0 24 24"><circle cx="11" cy="11" r="6.5" /><path d="m16 16 4.5 4.5" /></symbol>
            <symbol id="icon-sun" viewBox="0 0 24 24"><circle cx="12" cy="12" r="4" /><path d="M12 2.5v2.5M12 19v2.5M2.5 12H5M19 12h2.5M5.3 5.3l1.8 1.8M16.9 16.9l1.8 1.8M5.3 18.7l1.8-1.8M16.9 7.1l1.8-1.8" /></symbol>
            <symbol id="icon-moon" viewBox="0 0 24 24"><path d="M20 14.5A8 8 0 0 1 9.5 4a8 8 0 1 0 10.5 10.5z" /></symbol>
            <symbol id="icon-home" viewBox="0 0 24 24"><path d="M4 11 12 4l8 7M6 9.5V20h12V9.5" /></symbol>
            <symbol id="icon-library" viewBox="0 0 24 24"><path d="M5 4h3v16H5zM10 4h3v16h-3zM15.5 5.2l2.9-.8 3.6 15-2.9.8z" /></symbol>
            <symbol id="icon-author" viewBox="0 0 24 24"><circle cx="12" cy="8" r="3.5" /><path d="M5 20c.8-3.8 3.6-5.5 7-5.5s6.2 1.7 7 5.5" /></symbol>
            <symbol id="icon-series" viewBox="0 0 24 24"><path d="M4 6h11v14H4zM8 3h12v14" /></symbol>
            <symbol id="icon-settings" viewBox="0 0 24 24"><circle cx="12" cy="12" r="3" /><path d="M12 2.8v2.4M12 18.8v2.4M2.8 12h2.4M18.8 12h2.4M5.5 5.5l1.7 1.7M16.8 16.8l1.7 1.7M5.5 18.5l1.7-1.7M16.8 7.2l1.7-1.7" /></symbol>
            <symbol id="icon-user" viewBox="0 0 24 24"><circle cx="12" cy="12" r="9" /><circle cx="12" cy="10" r="3" /><path d="M6.5 18.5c1.2-2.3 3.1-3.3 5.5-3.3s4.3 1 5.5 3.3" /></symbol>
            <symbol id="icon-tag" viewBox="0 0 24 24"><path d="M3.5 12.5V4h8.5l8.5 8.5-8.5 8.5z" /><circle cx="8" cy="8.5" r="1.3" /></symbol>
        </svg>
    `,
    changeDetection: ChangeDetectionStrategy.OnPush,
})
export class IconSprite {}
