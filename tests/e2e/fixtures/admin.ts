import type { Locator, Page } from '@playwright/test';

/** The link of a section in the administration's column (on a phone, its list of sections), by its exact title. */
export function adminSection(page: Page, title: string): Locator {
    return page.locator('.admin-nav a').filter({ has: page.locator('.admin-nav-title').getByText(title, { exact: true }) });
}

/** A tab of a library's pages (Folders, Runs, Import, Missing books, Schedules). */
export function libraryTab(page: Page, name: string): Locator {
    return page.locator('.library-tabs').getByRole('link', { name, exact: true });
}
