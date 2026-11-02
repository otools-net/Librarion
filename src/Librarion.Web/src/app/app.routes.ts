import { Routes } from '@angular/router';
import { SetupPage } from './account/setup-page';
import { SignInPage } from './account/sign-in-page';
import { adminMatcher } from './admin/admin-route';
import { BrowsePage } from './browse/browse-page';
import { NotFoundPage } from './home/not-found-page';
import { signedIn } from './core/session.service';
import { leaveWithUnsaved } from './core/unsaved-changes';
import { Shell } from './shell/shell';

export const routes: Routes = [
    { path: 'setup', component: SetupPage, title: 'Librarion' },
    { path: 'login', component: SignInPage, title: 'Librarion' },
    {
        path: '',
        component: Shell,
        canActivate: [signedIn],
        children: [
            { path: '', component: BrowsePage, title: 'Librarion' },
            // My account is a section of the administration (as SFE's); the old address still opens it.
            { path: 'account', redirectTo: 'admin/account' },
            // Each section shows only to the rights of its sub-pages (admin-sections.ts); the API checks them again.
            // Loaded when first opened: readers never need it (it is the biggest part of the client).
            {
                matcher: adminMatcher, loadComponent: () => import('./admin/admin-page').then(m => m.AdminPage),
                canDeactivate: [leaveWithUnsaved], title: 'Librarion',
            },
            { path: '**', component: NotFoundPage, title: 'Librarion' },
        ],
    },
];
