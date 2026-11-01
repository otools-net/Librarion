import { Injectable, Pipe, PipeTransform, inject, signal } from '@angular/core';
import en from '../../i18n/en.json';
import { readPreference, writePreference } from './preferences';

// Every text of the UI is a key of src/i18n/<language>.json (agents/docs/development/frontend.md, "Languages").
// English is bundled and is the fallback of every key; other languages are added as files (loaded at run time when
// there are some). A backend test (ClientI18nTests) fails on a literal text in a template and on a key missing here.
export type Messages = Record<string, string>;
export type MessageKey = keyof typeof en;

const LANGUAGE = 'librarion.language';
const bundled: Record<string, Messages> = { en };

@Injectable({ providedIn: 'root' })
export class I18n {
    private readonly messages = signal<Messages>(en);
    private readonly code = signal('en');

    /** The UI language (a BCP 47 code: `en`, `es`…). */
    readonly language = this.code.asReadonly();

    constructor() {
        this.use(readPreference(LANGUAGE) ?? navigator.language);
    }

    /** Switches to `language` when its messages are known, else to English; the choice is kept in this browser. */
    use(language: string): void {
        const code = language.toLowerCase().split('-')[0];
        const messages = bundled[code] ?? en;
        this.code.set(bundled[code] ? code : 'en');
        this.messages.set(messages);
        document.documentElement.lang = this.code();
    }

    /** Keeps `language` for this browser and switches to it. */
    choose(language: string): void {
        writePreference(LANGUAGE, language);
        this.use(language);
    }

    /** The text of `key`, its `{name}` parts replaced by `params`. Reads a signal: templates follow a language change. */
    t(key: MessageKey | string, params?: Record<string, string | number>): string {
        const text = this.messages()[key] ?? (en as Messages)[key] ?? key;
        return params ? text.replace(/\{(\w+)\}/g, (part, name: string) => name in params ? String(params[name]) : part) : text;
    }
}

/** `{{ 'home.title' | t }}`, `{{ 'x.count' | t: { count } }}`. Not pure: it follows the language signal. */
@Pipe({ name: 't', pure: false })
export class TranslatePipe implements PipeTransform {
    private readonly i18n = inject(I18n);

    transform(key: MessageKey | string, params?: Record<string, string | number>): string {
        return this.i18n.t(key, params);
    }
}
