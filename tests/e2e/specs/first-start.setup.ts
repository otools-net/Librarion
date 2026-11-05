// The first start, once per run before every other project (playwright.config.ts): a new server opens on its setup
// page, refuses a wrong setup code, then makes the server administrator with the first tenant and library and signs
// it in. Its signed-in state is what the other specs start with (`adminState`).
import fs from 'node:fs';
import path from 'node:path';
import { audit } from '../fixtures/axe';
import { expect, test } from '../fixtures/test';
import { admin, adminState, setupCode } from '../paths';

// The wrong code is refused with a 400, which the browser logs.
test.use({ allowedErrors: /status of 400/ });

test('a new server sets up its administrator from the setup page', async ({ page }) => {
    await page.goto('./');
    await expect(page).toHaveURL(/\/setup$/);
    await expect(page.getByRole('heading', { level: 1 })).toHaveText('Set up Librarion');
    // Here only: the page shows once per server.
    await audit(page, 'setup');

    const code = page.getByLabel('Setup code');
    await code.tap();
    await code.fill('not-the-setup-code');
    await page.getByLabel('Name', { exact: true }).fill(admin.userName);
    await page.getByLabel('Password', { exact: true }).fill(admin.password);
    await page.getByLabel('New password again').fill(admin.password);
    await page.getByLabel('Name of your organisation').fill(admin.tenant);
    await page.getByLabel('Name of its first library').fill(admin.library);
    const submit = page.getByRole('button', { name: 'Set up and sign in' });
    await submit.tap();
    await expect(page.getByText("This is not the server's setup code.")).toBeVisible();

    await code.fill(setupCode);
    await submit.tap();

    await expect(page.locator('lib-browse-page')).toBeVisible();
    fs.mkdirSync(path.dirname(adminState), { recursive: true });
    await page.context().storageState({ path: adminState });
});
