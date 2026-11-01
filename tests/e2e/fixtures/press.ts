import type { Locator } from '@playwright/test';

/**
 * Presses `target` as the device does: a finger on a phone (`tap`), the mouse on the desktop project. For specs that
 * run on both (`@desktop`); phone-only specs tap directly.
 */
export async function press(target: Locator, isMobile: boolean): Promise<void> {
    if (isMobile)
        await target.tap();
    else
        await target.click(); // mouse: the desktop project has no touch screen
}
