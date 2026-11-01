import { ApiError } from './api.service';
import { I18n } from './i18n.service';

// The server answers with codes (`userName.taken`, `tenant.lastAdministrator`, `sign-in-failed`): their texts are
// `error.<code>` in en.json (ClientI18nTests checks every code the server sends has one).

/** The text of a failed request for a page's message line. */
export function errorText(i18n: I18n, error: unknown): string {
    if (error instanceof ApiError) {
        if (error.status === 0)
            return i18n.t('error.unreachable');
        if (error.type)
            return translated(i18n, error.type, error.message);
        if (error.errors)
            return Object.values(error.errors).flat().map(code => translated(i18n, code, code)).join(' ');
        if (error.status === 403)
            return i18n.t('error.forbidden');
        if (error.status === 404)
            return i18n.t('error.notFound');
        return error.message;
    }
    return String(error);
}

/** The texts of a failed request's validation codes, by field (`userName`, `password`…). */
export function fieldErrors(i18n: I18n, error: unknown): Record<string, string> {
    if (!(error instanceof ApiError) || !error.errors)
        return {};
    return Object.fromEntries(Object.entries(error.errors).map(([field, codes]) => [field, codes.map(code => translated(i18n, code, code)).join(' ')]));
}

function translated(i18n: I18n, code: string, fallback: string): string {
    const key = `error.${code}`;
    const text = i18n.t(key);
    return text === key ? fallback : text;
}

/* Reading form controls from their events in templates: `(input)="name.set(valueOf($event))"` (SFE's shared/events). */

export function valueOf(event: Event): string {
    return (event.target as HTMLInputElement | HTMLTextAreaElement | HTMLSelectElement).value;
}

export function checkedOf(event: Event): boolean {
    return (event.target as HTMLInputElement).checked;
}
