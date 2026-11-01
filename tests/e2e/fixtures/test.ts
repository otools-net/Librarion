// The `test` every spec imports (ported from SFE): Playwright's, failing on what a user wouldn't see but is still a
// bug: console errors, uncaught page errors, CSP violations, server errors, and any request to another origin (the
// app is fully local, agents/docs/development/privacy.md). On a phone, the screen a test ends on is checked too
// (`layoutCheck`): no sideways scroll, nothing cut off, no tap target under 24 px.
import { test as base, expect } from '@playwright/test';
import { expectFitsScreen, expectNothingCut, expectTapTargets } from './layout';

type Options = {
    /**
     * Console errors a spec expects (a refused request it provokes...): matched against the message. One expression
     * (`/a|b/`): `test.use` takes an array as `[value, options]`.
     */
    allowedErrors: RegExp | null;
    /**
     * The screen the test ends on fits the phone, nothing cut off, its targets ≥ 24 px. Off (`false`) for a test that
     * ends on a state made on purpose, with a comment saying why.
     */
    layoutCheck: boolean;
};

export const test = base.extend<Options & { guard: void; finalLayout: void }>({
    allowedErrors: [null, { option: true }],
    layoutCheck: [true, { option: true }],
    finalLayout: [async ({ page, layoutCheck, isMobile }, use, testInfo) => {
        await use();
        // Only after a test that passed (a failure says more than its layout), on a phone, on one of the app's pages.
        if (!layoutCheck || !isMobile || testInfo.status !== testInfo.expectedStatus || testInfo.status !== 'passed'
            || page.isClosed() || !page.url().startsWith('http'))
            return;
        await test.step('the final screen fits the phone', async () => {
            await expectFitsScreen(page);
            await expectNothingCut(page);
            await expectTapTargets(page);
        });
    }, { auto: true }],
    guard: [async ({ page, allowedErrors, baseURL }, use) => {
        const problems: string[] = [];
        const allowed = (text: string) => allowedErrors?.test(text) ?? false;
        const origin = new URL(baseURL!).origin;
        page.on('console', message => {
            // A failed load names its address (the browser's message doesn't).
            const text = message.text().startsWith('Failed to load resource') ? `${message.text()} ${message.location().url}` : message.text();
            if (message.type() === 'error' && !allowed(text))
                problems.push(`console: ${text}`);
        });
        page.on('pageerror', error => {
            if (!allowed(error.message))
                problems.push(`page error: ${error.message}`);
        });
        page.on('request', request => {
            const url = request.url();
            if (/^https?:/.test(url) && new URL(url).origin !== origin)
                problems.push(`request to another origin: ${request.method()} ${url}`);
        });
        page.on('response', response => {
            if (response.status() >= 500 && !allowed(response.url()))
                problems.push(`server: ${response.status()} ${response.request().method()} ${response.url()}`);
        });
        await page.addInitScript(() => {
            document.addEventListener('securitypolicyviolation', event =>
                console.error(`CSP violation: ${event.violatedDirective} ${event.blockedURI}`));
        });
        await use();
        expect(problems, 'errors while the test ran').toEqual([]);
    }, { auto: true }],
});

export { expect };

/** Phones up to 640 px wide: the navigation and the menus are sheets from the bottom (styles/shell.css). */
export const isPhoneWidth = (page: { viewportSize(): { width: number } | null }) => page.viewportSize()!.width <= 640;
