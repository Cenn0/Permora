import { Component, inject, input, signal, effect, untracked } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterLink } from '@angular/router';
import { PortalApi } from './api';
import type { GovernanceReport } from './models';
import { problem } from './view-model';

const noticeLabels: Record<string, string> = {
 review_overdue: 'Review überfällig – Entscheidung und Entzugsstatus prüfen',
 review_due_soon: 'Review innerhalb von 48 Stunden fällig',
 review_open: 'Offener Review', expired_assignments: 'Abgelaufene Zuweisungen – Entzugsstatus prüfen',
 directory_unverifiable: 'Zuweisungen mit nicht verifizierbarem Directory-Stand',
 group_separation_conflict: 'Gruppen-Zuweisungen mit erkanntem SoD-Konflikt',
 review_schedule_due: 'Prüfplan fällig – Worker-Ausführung prüfen'
};

@Component({standalone:true,imports:[CommonModule,RouterLink],template:`
<a class="breadcrumb" [routerLink]="['/applications',id()]">← Zur Anwendung</a>
<div class="page-heading"><div><h1>Governance-Bericht & Fristen</h1><p>Offene Prüfpflichten und Hinweise für diese Anwendung.</p></div><button (click)="load(0)" [disabled]="loading()">Bericht aktualisieren</button></div>
<p class="muted">Die Ansicht wertet gespeicherte Portal- und Directory-Daten aus. Sie bestätigt keine aktuellen Rechte in Keycloak. Hinweise werden beim Abruf berechnet; es erfolgt kein externer Versand.</p>
@if(error()){<div class="alert danger" role="alert">{{error()}}</div>}
@if(loading()){<p role="status">Bericht wird geladen …</p>}
@if(data();as report){
 <section class="panel" aria-labelledby="governance-summary"><h2 id="governance-summary">Prüfstand</h2><p>Stand: {{report.asOf|date:'dd.MM.yyyy HH:mm:ss'}} · Anwendungsrevision {{report.applicationRevision}}</p>
 <dl class="governance-counts"><div><dt>Offene Reviews</dt><dd>{{report.counts.openReviews}}</dd></div><div><dt>Offene Entscheidungen</dt><dd>{{report.counts.pendingDecisions}}</dd></div><div><dt>Überfällige Entscheidungen</dt><dd>{{report.counts.overdueDecisions}}</dd></div><div><dt>Abgelaufene Zuweisungen</dt><dd>{{report.counts.expiredAssignments}}</dd></div><div><dt>Directory nicht verifizierbar</dt><dd>{{report.counts.unverifiableAssignments}}</dd></div><div><dt>Gruppen-Zuweisungen mit SoD-Konflikt</dt><dd>{{report.counts.groupSeparationRevocations}}</dd></div></dl>
 <p class="muted small">{{report.counts.assignments}} verwaltete Zuweisungen insgesamt. Null erkannte Gruppen-Konflikte belegt keine vollständige SoD-Konformität. Bestehende Tokens und externe Rechte sind gesondert zu prüfen.</p></section>
 <section class="panel" aria-labelledby="governance-notices"><h2 id="governance-notices">Hinweise ({{report.totalNotices}})</h2>
 @for(notice of report.notices;track notice.key){<article class="task-card"><div><strong>{{label(notice.code)}}</strong><p>{{severity(notice.severity)}} · Anzahl: {{notice.count}}@if(notice.dueAt){ · Fällig: {{notice.dueAt|date:'dd.MM.yyyy HH:mm'}}}</p>
 <a [routerLink]="['/applications',id()]" [queryParams]="{tab:notice.reviewId?'reviews':'requests'}">{{notice.reviewId?'Reviews öffnen':'Aufträge prüfen'}}</a></div></article>}
 @empty{<p class="empty">Keine Hinweise auf dieser Seite. Das ist keine Compliance-Freigabe.</p>}
 <nav class="inline-actions" aria-label="Berichtsseiten"><button (click)="load(offset()-25)" [disabled]="loading()||offset()===0">Vorherige Seite</button><span aria-live="polite">Seite {{offset()/25+1}}</span><button (click)="load(report.nextOffset!)" [disabled]="loading()||report.nextOffset===null">Nächste Seite</button></nav>
 <p class="muted small">Maximal 25 Hinweise je Seite. Die Summen erfassen alle offenen Reviews. Jede Seite wird neu berechnet; bei gleichzeitigen Änderungen bitte den Bericht aktualisieren.</p></section>
}
`,styles:[`.governance-counts{display:grid;grid-template-columns:repeat(auto-fit,minmax(min(100%,15rem),1fr));gap:1rem}.governance-counts div{padding:.75rem;border:1px solid var(--border,#d7dce5);border-radius:.6rem}.governance-counts dt{font-size:.9rem}.governance-counts dd{margin:.4rem 0 0;font-size:1.6rem;font-weight:700}.task-card{overflow-wrap:anywhere}`]})
export class GovernancePage {
 readonly id=input.required<string>(); private readonly api=inject(PortalApi);
 readonly data=signal<GovernanceReport|null>(null); readonly error=signal(''); readonly loading=signal(false); readonly offset=signal(0);
 private generation=0;
 constructor(){effect(()=>{this.id();untracked(()=>void this.load(0));});}
 label(code:string):string{return noticeLabels[code]??'Unbekannter Hinweis – Bericht aktualisieren';}
 severity(value:string):string{return value==='critical'?'Eskalation erforderlich':value==='warning'?'Bald prüfen':'Information';}
 async load(offset:number):Promise<void>{
  if(offset<0)return;
  const generation=++this.generation;
  this.loading.set(true);this.error.set('');this.data.set(null);
  try{const report=await this.api.governance(this.id(),offset);if(generation!==this.generation)return;this.data.set(report);this.offset.set(offset);}
  catch(e){if(generation===this.generation)this.error.set(problem(e));}
  finally{if(generation===this.generation)this.loading.set(false);}
 }
}
