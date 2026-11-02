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
            // Browsing by value (fields, tags) is loaded when first opened.
            { path: 'fields', loadComponent: () => import('./tags/fields-page').then(m => m.FieldsPage), title: 'Librarion' },
            { path: 'fields/:key', loadComponent: () => import('./tags/values-page').then(m => m.ValuesPage), title: 'Librarion' },
            { path: 'tags', loadComponent: () => import('./tags/values-page').then(m => m.ValuesPage), title: 'Librarion' },
            { path: 'tags/:id', loadComponent: () => import('./tags/tag-page').then(m => m.TagPage), title: 'Librarion' },
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
