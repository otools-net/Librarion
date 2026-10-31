import { Routes } from '@angular/router';
import { HomePage } from './home/home-page';
import { NotFoundPage } from './home/not-found-page';

export const routes: Routes = [
    { path: '', component: HomePage, title: 'Librarion' },
    { path: '**', component: NotFoundPage, title: 'Librarion' },
];
