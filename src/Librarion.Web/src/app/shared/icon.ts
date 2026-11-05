import { ChangeDetectionStrategy, Component, input } from '@angular/core';

/** Every icon of the sprite, by name (icon.spec.ts checks each has its symbol, and no symbol is left without a name). */
export const ICON_NAMES = [
    // The shell
    'menu', 'close', 'search', 'sun', 'moon', 'home', 'logout',
    // Books and their fields
    'library', 'book', 'author', 'series', 'tag', 'category', 'language', 'publisher', 'calendar', 'star', 'folder',
    // Actions
    'plus', 'minus', 'check', 'edit', 'trash', 'download', 'import', 'refresh', 'copy', 'external', 'more', 'filter', 'sort',
    'grid', 'list', 'hidden',
    // Navigation
    'back', 'chevron-left', 'chevron-right', 'chevron-down',
    // Administration
    'settings', 'sliders', 'user', 'users', 'key', 'shield', 'server', 'database', 'tenant', 'clock', 'activity',
    'missing',
    // Status
    'info', 'alert',
] as const;

export type IconName = typeof ICON_NAMES[number];

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

/**
 * The icons, drawn on a 24 × 24 grid with `currentColor` strokes, round ends and joins (`.icon` in base.css): no icon
 * font, nothing loaded from elsewhere (agents/docs/features/design.md#icons). A dot is a zero-length line.
 */
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
            <symbol id="icon-logout" viewBox="0 0 24 24"><path d="M14 4h5v16h-5M10 8l-4 4 4 4M6 12h10" /></symbol>

            <symbol id="icon-library" viewBox="0 0 24 24"><path d="M5 4h3v16H5zM10 4h3v16h-3zM15.5 5.2l2.9-.8 3.6 15-2.9.8z" /></symbol>
            <symbol id="icon-book" viewBox="0 0 24 24"><path d="M5 17.5V7a2.5 2.5 0 0 1 2.5-2.5H19V15H7.5A2.5 2.5 0 0 0 5 17.5 2.5 2.5 0 0 0 7.5 20H19v-5" /></symbol>
            <symbol id="icon-author" viewBox="0 0 24 24"><circle cx="12" cy="8" r="3.5" /><path d="M5 20c.8-3.8 3.6-5.5 7-5.5s6.2 1.7 7 5.5" /></symbol>
            <symbol id="icon-series" viewBox="0 0 24 24"><path d="M4 6h11v14H4zM8 3h12v14" /></symbol>
            <symbol id="icon-tag" viewBox="0 0 24 24"><path d="M3.5 12.5V4h8.5l8.5 8.5-8.5 8.5z" /><circle cx="8" cy="8.5" r="1.3" /></symbol>
            <symbol id="icon-category" viewBox="0 0 24 24"><path d="M12 4 3.5 8.5 12 13l8.5-4.5zM3.5 12.5 12 17l8.5-4.5M3.5 16.5 12 21l8.5-4.5" /></symbol>
            <symbol id="icon-language" viewBox="0 0 24 24"><circle cx="12" cy="12" r="8.5" /><path d="M3.5 12h17M12 3.5c2.3 2.5 3.5 5.3 3.5 8.5s-1.2 6-3.5 8.5c-2.3-2.5-3.5-5.3-3.5-8.5s1.2-6 3.5-8.5" /></symbol>
            <symbol id="icon-publisher" viewBox="0 0 24 24"><path d="M4 20h16M6 20V9l6-4 6 4v11M10 20v-5h4v5" /></symbol>
            <symbol id="icon-calendar" viewBox="0 0 24 24"><rect x="4" y="5.5" width="16" height="14.5" rx="2" /><path d="M4 10h16M8.5 3.5v4M15.5 3.5v4" /></symbol>
            <symbol id="icon-star" viewBox="0 0 24 24"><path d="m12 3.8 2.5 5.2 5.7.8-4.1 4 1 5.7-5.1-2.7-5.1 2.7 1-5.7-4.1-4 5.7-.8z" /></symbol>
            <symbol id="icon-folder" viewBox="0 0 24 24"><path d="M3.5 6.5h6l2 2h9v10.5h-17z" /></symbol>

            <symbol id="icon-plus" viewBox="0 0 24 24"><path d="M12 5v14M5 12h14" /></symbol>
            <symbol id="icon-minus" viewBox="0 0 24 24"><path d="M5 12h14" /></symbol>
            <symbol id="icon-check" viewBox="0 0 24 24"><path d="m5 12.5 4.5 4.5L19 7" /></symbol>
            <symbol id="icon-edit" viewBox="0 0 24 24"><path d="M4 20h4L19 9l-4-4L4 16zM13.5 6.5l4 4" /></symbol>
            <symbol id="icon-trash" viewBox="0 0 24 24"><path d="M4 7h16M9 7V4.5h6V7M6.5 7l1 13h9l1-13M10 11v6M14 11v6" /></symbol>
            <symbol id="icon-download" viewBox="0 0 24 24"><path d="M12 4v11M7 10l5 5 5-5M5 20h14" /></symbol>
            <symbol id="icon-import" viewBox="0 0 24 24"><path d="M4 14v6h16v-6M12 3v12M7.5 10.5 12 15l4.5-4.5" /></symbol>
            <symbol id="icon-refresh" viewBox="0 0 24 24"><path d="M20 12a8 8 0 1 1-2.3-5.7M20 4v4.5h-4.5" /></symbol>
            <symbol id="icon-copy" viewBox="0 0 24 24"><rect x="8" y="8" width="12" height="12" rx="2" /><path d="M16 8V5.5A1.5 1.5 0 0 0 14.5 4h-9A1.5 1.5 0 0 0 4 5.5v9A1.5 1.5 0 0 0 5.5 16H8" /></symbol>
            <symbol id="icon-external" viewBox="0 0 24 24"><path d="M14 4h6v6M20 4l-9 9M18 14v6H4V6h6" /></symbol>
            <symbol id="icon-more" viewBox="0 0 24 24"><path d="M6 12h.01M12 12h.01M18 12h.01" stroke-width="3" /></symbol>
            <symbol id="icon-filter" viewBox="0 0 24 24"><path d="M4 5h16l-6 7.5V19l-4-2v-4.5z" /></symbol>
            <symbol id="icon-sort" viewBox="0 0 24 24"><path d="M7 4v16M3.5 16.5 7 20l3.5-3.5M17 20V4M13.5 7.5 17 4l3.5 3.5" /></symbol>
            <symbol id="icon-grid" viewBox="0 0 24 24"><rect x="4" y="4" width="6.5" height="6.5" rx="1.5" /><rect x="13.5" y="4" width="6.5" height="6.5" rx="1.5" /><rect x="4" y="13.5" width="6.5" height="6.5" rx="1.5" /><rect x="13.5" y="13.5" width="6.5" height="6.5" rx="1.5" /></symbol>
            <symbol id="icon-list" viewBox="0 0 24 24"><path d="M9 6h11M9 12h11M9 18h11M4.5 6h.01M4.5 12h.01M4.5 18h.01" /></symbol>
            <symbol id="icon-hidden" viewBox="0 0 24 24"><path d="M2.5 12S6 6 12 6s9.5 6 9.5 6-3.5 6-9.5 6-9.5-6-9.5-6z" /><circle cx="12" cy="12" r="2.5" /><path d="M4 4l16 16" /></symbol>

            <symbol id="icon-back" viewBox="0 0 24 24"><path d="M19 12H5M11 6l-6 6 6 6" /></symbol>
            <symbol id="icon-chevron-left" viewBox="0 0 24 24"><path d="m15 6-6 6 6 6" /></symbol>
            <symbol id="icon-chevron-right" viewBox="0 0 24 24"><path d="m9 6 6 6-6 6" /></symbol>
            <symbol id="icon-chevron-down" viewBox="0 0 24 24"><path d="m6 9 6 6 6-6" /></symbol>

            <symbol id="icon-settings" viewBox="0 0 24 24"><path d="M18.70 10.34 L21.10 10.63 L21.10 13.37 L18.70 13.66 L17.91 15.56 L19.40 17.47 L17.47 19.40 L15.56 17.91 L13.66 18.70 L13.37 21.10 L10.63 21.10 L10.34 18.70 L8.44 17.91 L6.53 19.40 L4.60 17.47 L6.09 15.56 L5.30 13.66 L2.90 13.37 L2.90 10.63 L5.30 10.34 L6.09 8.44 L4.60 6.53 L6.53 4.60 L8.44 6.09 L10.34 5.30 L10.63 2.90 L13.37 2.90 L13.66 5.30 L15.56 6.09 L17.47 4.60 L19.40 6.53 L17.91 8.44z" /><circle cx="12" cy="12" r="2.8" /></symbol>
            <symbol id="icon-sliders" viewBox="0 0 24 24"><path d="M4 7h9M17 7h3M4 17h3M11 17h9M15 4.5v5M9 14.5v5" /></symbol>
            <symbol id="icon-user" viewBox="0 0 24 24"><circle cx="12" cy="12" r="9" /><circle cx="12" cy="10" r="3" /><path d="M6.5 18.5c1.2-2.3 3.1-3.3 5.5-3.3s4.3 1 5.5 3.3" /></symbol>
            <symbol id="icon-users" viewBox="0 0 24 24"><circle cx="9" cy="8.5" r="3" /><path d="M3.5 19c.6-3.2 2.7-4.8 5.5-4.8s4.9 1.6 5.5 4.8M15.5 5.5a3 3 0 0 1 0 6M17.5 14.6c1.8.6 2.8 2.1 3 4.4" /></symbol>
            <symbol id="icon-key" viewBox="0 0 24 24"><circle cx="8" cy="15" r="4" /><path d="m11 12 8.5-8.5M16 7l2.5 2.5M13.5 9.5l2 2" /></symbol>
            <symbol id="icon-shield" viewBox="0 0 24 24"><path d="m12 3 7 3v5.5c0 4.5-3 8-7 9.5-4-1.5-7-5-7-9.5V6z" /></symbol>
            <symbol id="icon-server" viewBox="0 0 24 24"><rect x="4" y="4" width="16" height="7" rx="2" /><rect x="4" y="13" width="16" height="7" rx="2" /><path d="M8 7.5h.01M8 16.5h.01" /></symbol>
            <symbol id="icon-database" viewBox="0 0 24 24"><ellipse cx="12" cy="6" rx="7" ry="2.5" /><path d="M5 6v12c0 1.4 3.1 2.5 7 2.5s7-1.1 7-2.5V6M5 12c0 1.4 3.1 2.5 7 2.5s7-1.1 7-2.5" /></symbol>
            <symbol id="icon-tenant" viewBox="0 0 24 24"><rect x="4" y="3.5" width="9" height="17" rx="1" /><path d="M13 9h6v11.5h-6M7 7.5h3M7 11h3M7 14.5h3M16 12.5h.01M16 16h.01" /></symbol>
            <symbol id="icon-clock" viewBox="0 0 24 24"><circle cx="12" cy="12" r="8.5" /><path d="M12 7.5V12l3 2" /></symbol>
            <symbol id="icon-activity" viewBox="0 0 24 24"><path d="M3 12h4l3-7 4 14 3-7h4" /></symbol>
            <symbol id="icon-missing" viewBox="0 0 24 24"><path d="M6 3.5h8l4 4v13H6zM14 3.5V8h4M10 12.5l4 4M14 12.5l-4 4" /></symbol>

            <symbol id="icon-info" viewBox="0 0 24 24"><circle cx="12" cy="12" r="9" /><path d="M12 11v5M12 7.8h.01" /></symbol>
            <symbol id="icon-alert" viewBox="0 0 24 24"><path d="M12 4 2.8 19.5h18.4zM12 10v4M12 17h.01" /></symbol>
        </svg>
    `,
    changeDetection: ChangeDetectionStrategy.OnPush,
})
export class IconSprite {}
