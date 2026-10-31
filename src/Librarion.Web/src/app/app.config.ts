import { APP_BASE_HREF } from '@angular/common';
import { provideHttpClient, withFetch } from '@angular/common/http';
import { ApplicationConfig, provideBrowserGlobalErrorListeners, provideZonelessChangeDetection } from '@angular/core';
import { provideRouter } from '@angular/router';
import { routes } from './app.routes';

export const appConfig: ApplicationConfig = {
    providers: [
        provideBrowserGlobalErrorListeners(),
        provideZonelessChangeDetection(),
        provideHttpClient(withFetch()),
        provideRouter(routes),
        // index.html has <base href="./">: the app's root is where the page was served from, also under a virtual
        // directory (e.g. /librarion/).
        { provide: APP_BASE_HREF, useFactory: () => new URL(document.baseURI).pathname },
    ],
};
