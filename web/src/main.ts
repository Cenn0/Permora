import { provideZonelessChangeDetection } from '@angular/core';
import { bootstrapApplication } from '@angular/platform-browser';
import { provideHttpClient, withXsrfConfiguration } from '@angular/common/http';
import { provideRouter, withComponentInputBinding } from '@angular/router';
import { App } from './app/app';
bootstrapApplication(App, {providers:[provideZonelessChangeDetection(),provideHttpClient(withXsrfConfiguration({cookieName:'XSRF-TOKEN',headerName:'X-XSRF-TOKEN'})),provideRouter([
 {path:'',loadComponent:()=>import('./app/dashboard').then(m=>m.DashboardPage)},
 {path:'applications/new',loadComponent:()=>import('./app/applications').then(m=>m.RegisterPage)},
 {path:'applications/:id',loadComponent:()=>import('./app/application').then(m=>m.ApplicationPage)},
 {path:'applications',loadComponent:()=>import('./app/applications').then(m=>m.ApplicationsPage)},
 {path:'requests',loadComponent:()=>import('./app/secondary').then(m=>m.RequestsPage)},
 {path:'teams',loadComponent:()=>import('./app/secondary').then(m=>m.TeamsPage)},
 {path:'help',loadComponent:()=>import('./app/secondary').then(m=>m.HelpPage)},
 {path:'**',redirectTo:''}
],withComponentInputBinding())]}).catch(()=>{document.body.textContent='Permora konnte nicht gestartet werden. Bitte lade die Seite erneut.';});
