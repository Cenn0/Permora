import { Component, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterLink } from '@angular/router';
import { PortalApi } from './api';
import { Icon } from './icon';
import type { Dashboard } from './models';
import { applicationColor, barWidth, label, problem, statusClass } from './view-model';

@Component({selector:'p-dashboard',standalone:true,imports:[CommonModule,RouterLink,Icon],templateUrl:'./dashboard.html'})
export class DashboardPage {
 readonly api = inject(PortalApi); readonly data = signal<Dashboard | null>(null); readonly error = signal(''); readonly loading = signal(true);
 readonly label = label; readonly tone = statusClass; readonly color = applicationColor;
 constructor() { void this.reload(); }
 async reload(): Promise<void> { this.loading.set(true); this.error.set(''); try { this.data.set(await this.api.dashboard()); } catch (e) { this.error.set(problem(e)); } finally { this.loading.set(false); } }
 width(value: number): number { return barWidth(value, this.data()?.applications.map(a => a.identityCount) ?? []); }
 appName(id: string): string { return this.data()?.applications.find(a => a.id === id)?.name ?? 'Anwendung'; }
}
