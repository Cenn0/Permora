import { Component, computed, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { PortalApi } from './api';
import { Icon } from './icon';
import type { AppSummary, Team } from './models';
import { applicationColor, label, problem, statusClass } from './view-model';

@Component({standalone:true,imports:[CommonModule,FormsModule,RouterLink,Icon],template:`
<div class="page-heading"><div><span class="eyebrow">DEIN VERANTWORTUNGSBEREICH</span><h1>Meine Anwendungen</h1><p>Nur Anwendungen, für die du ein Verwaltungsmandat besitzt.</p></div><a class="button primary" routerLink="/applications/new"><p-icon name="plus" /> Neue Anwendung</a></div>
@if(error()){<div class="alert danger" role="alert">{{error()}} <button (click)="load()">Erneut laden</button></div>}
<div class="toolbar"><label class="search-field"><p-icon name="search" /><input aria-label="Eigene Anwendungen durchsuchen" [ngModel]="query()" (ngModelChange)="query.set($event)" placeholder="Anwendungen durchsuchen …" /></label><span class="muted">{{filtered().length}} Anwendungen</span></div>
@if(loading()){<p role="status">Wird geladen …</p>}
<div class="application-cards wide">@for(app of filtered();track app.id;let i=$index){<a [routerLink]="['/applications',app.id]" [class]="'application-card '+color(i)"><span [class]="'icon-tile '+color(i)"><p-icon name="apps" /></span><h2>{{app.name}}</h2><p>{{app.description}}</p><span [class]="'badge '+tone(app.status)">{{label(app.status)}}</span><small>{{label(app.environment)}} · {{app.roleCount}} Rollen · {{app.permissionCount}} Rechte</small></a>}@empty{<div class="empty panel">Keine passenden Anwendungen.</div>}</div>`})
export class ApplicationsPage {
 private readonly api=inject(PortalApi); readonly apps=signal<AppSummary[]>([]); readonly query=signal(''); readonly error=signal(''); readonly loading=signal(true);
 readonly filtered=computed(()=>this.apps().filter(a=>(a.name+' '+a.slug).toLowerCase().includes(this.query().trim().toLowerCase())));
 readonly label=label; readonly tone=statusClass; readonly color=applicationColor;
 constructor(){void this.load();} async load():Promise<void>{this.loading.set(true);try{this.apps.set(await this.api.applications());this.error.set('');}catch(e){this.error.set(problem(e));}finally{this.loading.set(false);}}
}
@Component({standalone:true,imports:[FormsModule,RouterLink,Icon],template:`
<a class="breadcrumb" routerLink="/applications">← Meine Anwendungen</a><div class="page-heading"><div><h1>Neue Anwendung</h1><p>Ein eigener Verwaltungsbereich für dein Team.</p></div></div>
@if(error()){<div class="alert danger" role="alert">{{error()}}</div>}
<form class="panel form-panel" (ngSubmit)="save()"><div class="alert neutral">Nach der Registrierung prüft das IAM-Team die Keycloak-Anbindung. Erst danach können Rechte verwaltet werden.</div>
<label>Name<input name="name" [(ngModel)]="name" required minlength="2" maxlength="120" /></label>
<label>Technischer Schlüssel<input name="slug" [(ngModel)]="slug" required pattern="[a-z][a-z0-9]*(?:[.-][a-z0-9]+)*" minlength="2" maxlength="80" placeholder="production-planning" /><small>Unveränderlicher Schlüssel. Keine zentralen oder fremden Namensräume.</small></label>
<label>Beschreibung<textarea name="description" [(ngModel)]="description" maxlength="2000"></textarea></label>
<div class="two-columns"><label>Verantwortliches Team<select name="team" [(ngModel)]="teamId" required><option value="">Bitte auswählen</option>@for(team of teams();track team.id){<option [value]="team.id">{{team.name}}</option>}</select></label><label>Realm<select name="realm" [(ngModel)]="realmKey" required><option value="">Bitte auswählen</option>@for(realm of realms();track realm){<option [value]="realm">{{realm}}</option>}</select></label></div>
<label>Umgebung<select name="environment" [(ngModel)]="environment"><option value="Development">Entwicklung</option><option value="Test">Test</option><option value="Production">Produktion</option></select></label>
<div class="form-actions"><a class="button" routerLink="/applications">Abbrechen</a><button class="primary" type="submit" [disabled]="busy() || !teamId || !realmKey">{{busy()?'Registrieren …':'Anwendung registrieren'}}</button></div></form>`})
export class RegisterPage {
 private readonly api=inject(PortalApi); private readonly router=inject(Router); readonly teams=signal<Team[]>([]); readonly realms=signal<string[]>([]); readonly busy=signal(false); readonly error=signal('');
 name='';slug='';description='';teamId='';realmKey='';environment='Development';
 constructor(){void this.load();} async load():Promise<void>{try{const [teams,realms]=await Promise.all([this.api.teams(),this.api.realms()]);const session=this.api.session();this.teams.set(teams.filter(t=>session?.administrator||t.members.some(m=>m.subjectKey===session?.subjectKey&&m.role==='Owner')));this.realms.set(realms);}catch(e){this.error.set(problem(e));}}
 async save():Promise<void>{if(this.busy())return;this.busy.set(true);this.error.set('');try{const app=await this.api.post<{id:string}>('/api/v1/applications',{teamId:this.teamId,realmKey:this.realmKey,slug:this.slug,name:this.name,description:this.description,environment:this.environment});await this.router.navigate(['/applications',app.id]);}catch(e){this.error.set(problem(e));}finally{this.busy.set(false);}}
}
