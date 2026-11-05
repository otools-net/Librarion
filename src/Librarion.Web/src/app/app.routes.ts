import { Routes } from '@angular/router';
import { SetupPage } from './account/setup-page';
import { SignInPage } from './account/sign-in-page';
import { adminMatcher } from './admin/admin-route';
import { BrowsePage } from './browse/browse-page';
import { NotFoundPage } from './home/not-found-page';
import { signedIn } from './core/session.service';
import { fieldValueMatcher } from './tags/tag-link';
import { leaveWithUnsaved } from './core/unsaved-changes';
import { Shell } from './shell/shell';

const tagPage = () => import('./tags/tag-page').then(m => m.TagPage);

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
            // A tag's page: /tag/<id>/<name>, a field's value /<key>/<id>/<name> (below, after the app's own routes) or
            // /fields/<key>/<id>/<name> for a key that clashes with them (tag-link.ts); the old /tags/<id> still opens.
            { path: 'tag/:id', loadComponent: tagPage, title: 'Librarion' },
            { path: 'tag/:id/:name', loadComponent: tagPage, title: 'Librarion' },
            { path: 'tags/:id', loadComponent: tagPage, title: 'Librarion' },
            { path: 'fields/:key/:id', loadComponent: tagPage, title: 'Librarion' },
            { path: 'fields/:key/:id/:name', loadComponent: tagPage, title: 'Librarion' },
            { path: 'books/:id', loadComponent: () => import('./books/book-page').then(m => m.BookPage), title: 'Librarion' },
            { path: 'search', loadComponent: () => import('./search/search-page').then(m => m.SearchPage), title: 'Librarion' },
            // My account is a section of the administration (as SFE's); the old address still opens it.
            { path: 'account', redirectTo: 'admin/account' },
            // Each section shows only to the rights of its sub-pages (admin-sections.ts); the API checks them again.
            // Loaded when first opened: readers never need it (it is the biggest part of the client).
            {
                matcher: adminMatcher, loadComponent: () => import('./admin/admin-page').then(m => m.AdminPage),
                canDeactivate: [leaveWithUnsaved], title: 'Librarion',
            },
            { matcher: fieldValueMatcher, loadComponent: tagPage, title: 'Librarion' },
            { path: '**', component: NotFoundPage, title: 'Librarion' },
        ],
    },
];
