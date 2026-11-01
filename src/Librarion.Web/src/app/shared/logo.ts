import { ChangeDetectionStrategy, Component } from '@angular/core';

/**
 * `<svg libLogo></svg>`: the Librarion mark, a temple front whose columns are book spines ("-ion" as in Mouseion).
 * The blue follows the theme (`--accent`), the details the surface behind it; `public/favicon.svg` is its simple
 * form and `public/logo.svg` the same mark for pages outside the app. Decorative: the name goes next to it.
 */
@Component({
    selector: 'svg[libLogo]',
    template: `
        <svg:path class="logo-blue" d="M28 3 53 16H3zM4 17.5h48v3H4zM6 22h7v24H6zM24 22h8v24h-8zM43 22h7v24h-7zM4 47.5h48v3H4zM1 51.5h54v3H1z" />
        <svg:circle class="logo-gold" cx="28" cy="11.2" r="2.6" />
        <svg:path class="logo-gold" d="M15.5 22h6v24h-6zM34.5 22h6v24h-6z" />
        <svg:path class="logo-detail" d="M6 25h7v1.4H6zM6 41.6h7V43H6zM24 25h8v1.4h-8zM26 30h4v7h-4zM24 41.6h8V43h-8zM43 25h7v1.4h-7zM43 41.6h7V43h-7z" />
        <svg:path class="logo-gold-dark" d="M15.5 25h6v1.4h-6zM15.5 41.6h6V43h-6zM34.5 25h6v1.4h-6zM34.5 41.6h6V43h-6z" />
    `,
    host: { class: 'logo', viewBox: '0 0 56 56', 'aria-hidden': 'true', focusable: 'false' },
    changeDetection: ChangeDetectionStrategy.OnPush,
})
export class Logo {}
