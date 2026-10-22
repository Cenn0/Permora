import { Injectable, inject, signal } from '@angular/core';
import { HttpClient, HttpHeaders } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import type { AppDetail, AppSummary, AuditPage, Change, Dashboard, Operation, Principal, Review, ReviewSchedule, ReviewScheduleInput, Session, Team } from './models';

@Injectable({ providedIn: 'root' })
export class PortalApi {
 private readonly http = inject(HttpClient);
 readonly session = signal<Session | null>(null);
 async loadSession(): Promise<void> { this.session.set(await this.get<Session>('/bff/session')); }
 get<T>(path: string): Promise<T> { return firstValueFrom(this.http.get<T>(path)); }
 async post<T>(path: string, body: unknown, key?: string): Promise<T> {
  try {
   return await firstValueFrom(this.http.post<T>(path, body, { headers: key ? new HttpHeaders({ 'Idempotency-Key': key }) : undefined }));
  } catch (error) { this.handleWriteFailure(error); throw error; }
 }
 async put<T>(path: string, body: unknown): Promise<T> {
  try { return await firstValueFrom(this.http.put<T>(path, body)); }
  catch (error) { this.handleWriteFailure(error); throw error; }
 }
 private handleWriteFailure(error: unknown): void {
  const response = error as { status?: number; error?: { title?: string } };
  if (response?.status === 403 && response?.error?.title === 'step_up_required')
   this.session.update(s => s ? { ...s, strongAuthentication: false } : s);
 }
 dashboard(): Promise<Dashboard> { return this.get('/api/v1/dashboard'); }
 applications(): Promise<AppSummary[]> { return this.get('/api/v1/applications'); }
 application(id: string): Promise<AppDetail> { return this.get(`/api/v1/applications/${encodeURIComponent(id)}`); }
 teams(): Promise<Team[]> { return this.get('/api/v1/teams'); }
 realms(): Promise<string[]> { return this.get('/api/v1/realms'); }
 changes(id: string, change: Change, revision: number, key: string): Promise<Operation> {
  return this.post(`/api/v1/applications/${id}/changes`, { change, expectedRevision: revision }, key);
 }
 identities(id: string, query: string): Promise<Principal[]> { return this.get(`/api/v1/applications/${id}/identities?q=${encodeURIComponent(query)}`); }
 operations(id: string): Promise<Operation[]> { return this.get(`/api/v1/applications/${id}/operations`); }
 audit(id: string, after = 0): Promise<AuditPage> { return this.get(`/api/v1/applications/${id}/audit?after=${after}`); }
 reviews(id: string): Promise<Review[]> { return this.get(`/api/v1/applications/${id}/reviews`); }
 reviewSchedule(id: string, input: ReviewScheduleInput): Promise<ReviewSchedule> {
  return this.put(`/api/v1/applications/${encodeURIComponent(id)}/review-schedule`, input);
 }
 async logout(): Promise<void> { await this.post('/bff/logout', {}); location.assign('/'); }
}
