import { Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { PortalApi } from './api';
import { Icon } from './icon';
import type { AppDetail, AuditEvent, Change, Operation, Permission, Principal, Review, Role } from './models';
import { label, problem, RequestKeys, safeExpiry, statusClass } from './view-model';

type Tab='roles'|'permissions'|'access'|'requests'|'reviews'|'audit'|'settings';
@Component({standalone:true,imports:[CommonModule,FormsModule,RouterLink,Icon],templateUrl:'./application.html'})
export class ApplicationPage {
 readonly api=inject(PortalApi); private readonly route=inject(ActivatedRoute); private readonly destroy=inject(DestroyRef);
 readonly detail=signal<AppDetail|null>(null); readonly error=signal(''); readonly notice=signal(''); readonly loading=signal(true); readonly busy=signal(false);
 readonly tab=signal<Tab>('roles'); readonly editor=signal<'permission'|'role'|'grant'|null>(null);
 readonly operations=signal<Operation[]>([]);readonly reviews=signal<Review[]>([]);readonly events=signal<AuditEvent[]>([]);readonly nextSequence=signal<number|null>(null);
 readonly principals=signal<Principal[]>([]);readonly searching=signal(false);private version=0; private searchVersion=0;private id='';private readonly keys=new RequestKeys();
 readonly define=computed(()=>['Owner','Maintainer'].includes(this.detail()?.managementRole??''));
 readonly assign=computed(()=>['Owner','AccessManager'].includes(this.detail()?.managementRole??''));
 readonly approve=computed(()=>['Owner','Approver'].includes(this.detail()?.managementRole??''));
 readonly active=computed(()=>this.detail()?.application.status==='Active');
 readonly label=label;readonly tone=statusClass;
 readonly tabs:{id:Tab;name:string}[]=[{id:'roles',name:'Rollen'},{id:'permissions',name:'Berechtigungen'},{id:'access',name:'Zugriffe'},{id:'requests',name:'Aufträge'},{id:'reviews',name:'Rezertifizierung'},{id:'audit',name:'Änderungsprotokoll'},{id:'settings',name:'Anbindung'}];
 editId='';key='';name='';description='';risk:'High'|'Standard'='High';permissionIds:string[]=[];reason='';query='';principalId='';roleId='';expiry='';reviewName='';reviewDue='';decisionReason='';clientUuid='';clientId='';loginClientUuid='';
 iamEvidence='';iamFgap=false;iamRoleProfile=false;iamAssignmentProfile=false;iamAudience=false;
 constructor(){this.route.paramMap.pipe(takeUntilDestroyed(this.destroy)).subscribe(p=>{this.id=p.get('id')??'';++this.searchVersion;this.searching.set(false);this.principals.set([]);this.operations.set([]);this.reviews.set([]);this.notice.set('');this.keys.clear();this.detail.set(null);this.events.set([]);this.editor.set(null);void this.reload();});this.route.queryParamMap.pipe(takeUntilDestroyed(this.destroy)).subscribe(p=>{const tab=p.get('tab');if(this.tabs.some(t=>t.id===tab))this.tab.set(tab as Tab);});}
 async reload():Promise<void>{const version=++this.version;const id=this.id;this.loading.set(true);this.error.set('');try{const detail=await this.api.application(id);if(version!==this.version)return;this.detail.set(detail);const [operations,reviews,audit]=await Promise.all([this.api.operations(id),this.api.reviews(id),this.api.audit(id)]);if(version!==this.version)return;this.operations.set(operations);this.reviews.set(reviews);this.events.set(audit.items);this.nextSequence.set(audit.nextSequence);}catch(e){if(version===this.version)this.error.set(problem(e));}finally{if(version===this.version)this.loading.set(false);}}
 selectTab(tab:Tab):void{this.tab.set(tab);this.editor.set(null);}
 open(kind:'permission'|'role'|'grant',item?:Permission|Role):void{this.editor.set(kind);this.error.set('');this.notice.set('');this.editId=item?.id??'';this.key=item?.key??'';this.name=item?.name??'';this.description=item?.description??'';this.risk=item&&'risk'in item?item.risk:'High';this.permissionIds=item&&'permissionIds'in item?[...item.permissionIds]:[];this.reason='';this.query='';this.principalId='';this.roleId='';this.expiry='';this.principals.set([]);this.keys.clear();}
 toggle(id:string,checked:boolean):void{this.permissionIds=checked?[...new Set([...this.permissionIds,id])]:this.permissionIds.filter(p=>p!==id);}
 async save():Promise<void>{let change:Change;const kind=this.editor();if(kind==='grant'){const expiresAt=safeExpiry(this.expiry);if(!expiresAt){this.error.set('Bitte ein gültiges Ablaufdatum angeben.');return;}change={kind:'GrantAccess',roleId:this.roleId,principalId:this.principalId,expiresAt,reason:this.reason};}else{change={kind:kind==='permission'?(this.editId?'UpdatePermission':'CreatePermission'):(this.editId?'UpdateRole':'CreateRole'),targetId:this.editId||undefined,key:this.key,name:this.name,description:this.description,risk:this.risk,permissionIds:this.permissionIds,reason:this.reason};}await this.submit(change);}
 async submit(change:Change):Promise<void>{const detail=this.detail();if(!detail||this.busy())return;const id=this.id;this.busy.set(true);this.error.set('');try{const payload={change,expectedRevision:detail.application.revision};const op=await this.api.changes(id,change,detail.application.revision,this.keys.for(payload));if(id!==this.id)return;this.notice.set(`Auftrag angenommen: ${label(op.status)}. Die Änderung ist noch nicht als wirksam bestätigt.`);this.editor.set(null);this.keys.clear();await this.reload();this.tab.set('requests');}catch(e){this.error.set(problem(e));}finally{this.busy.set(false);}}
 async remove(kind:string,id:string,name:string):Promise<void>{const reason=window.prompt(`„${name}“ entfernen: Bitte begründen. Eine Rechteänderung wird als Auftrag verarbeitet.`);if(reason===null)return;await this.submit({kind,targetId:id,reason});}
 async search():Promise<void>{if(this.query.trim().length<3){this.error.set('Mindestens drei Zeichen eingeben.');return;}const version=++this.searchVersion;this.searching.set(true);this.error.set('');try{const principals=await this.api.identities(this.id,this.query.trim());if(version===this.searchVersion)this.principals.set(principals);}catch(e){if(version===this.searchVersion)this.error.set(problem(e));}finally{if(version===this.searchVersion)this.searching.set(false);}}
 permissionName(id:string):string{return this.detail()?.application.permissions.find(p=>p.id===id)?.name??'Nicht auflösbare Berechtigung';}
 roleName(id:string):string{return this.detail()?.application.roles.find(p=>p.id===id)?.name??'Nicht auflösbare Rolle';}
 principalName(id:string):string{return this.detail()?.principals.find(p=>p.id===id)?.name??'Identität nicht mehr auflösbar';}
 canDecide(op:Operation):boolean{return this.approve()&&op.status==='AwaitingApproval'&&op.requestedBy.key!==this.api.session()?.subjectKey;}
 async decide(op:Operation,approve:boolean):Promise<void>{const reason=window.prompt(approve?'Freigabe begründen:':'Ablehnung begründen:');if(reason===null)return;await this.perform(()=>this.api.post(`/api/v1/applications/${this.id}/operations/${op.id}/decision`,{approve,reason}),'Entscheidung gespeichert.');}
 async retry(op:Operation):Promise<void>{await this.perform(()=>this.api.post(`/api/v1/applications/${this.id}/operations/${op.id}/retry`,{}),'Erneute Verarbeitung angefordert.');}
 async startReview():Promise<void>{const dueAt=safeExpiry(this.reviewDue);if(!dueAt){this.error.set('Bitte ein gültiges Fälligkeitsdatum wählen.');return;}await this.perform(()=>this.api.post(`/api/v1/applications/${this.id}/reviews`,{name:this.reviewName,dueAt}),'Rezertifizierung gestartet.');}
 async review(review:Review,assignmentId:string,retain:boolean):Promise<void>{await this.perform(()=>this.api.post(`/api/v1/applications/${this.id}/reviews/${review.id}/items/${assignmentId}`,{retain}),retain?'Berechtigung bestätigt.':'Entzug angefordert, noch nicht wirksam bestätigt.');}
 async reconcile():Promise<void>{await this.submit({kind:'Reconcile',reason:'Manueller Soll-Ist-Abgleich durch das Anwendungsteam'});}
 async binding():Promise<void>{await this.perform(()=>this.api.put(`/api/v1/admin/applications/${this.id}/binding`,{clientUuid:this.clientUuid,clientId:this.clientId,loginClientUuid:this.loginClientUuid||null,expectedRevision:this.detail()!.application.revision}),'Anbindung gespeichert. IAM-Freigabe muss separat dokumentiert werden.');}
 async approveIam():Promise<void>{await this.perform(()=>this.api.post(`/api/v1/admin/applications/${this.id}/iam-approval`,{
   expectedRevision:this.detail()!.application.revision, fgapVerified:this.iamFgap,
   rolePermissionsVerified:this.iamRoleProfile,assignmentPermissionsVerified:this.iamAssignmentProfile,
   tokenAudienceVerified:this.iamAudience,evidenceReference:this.iamEvidence
 }),'IAM-Freigabe protokolliert; technische Rückprüfung wird ausgeführt.');}
 async moreAudit():Promise<void>{const after=this.nextSequence();const id=this.id;if(after===null)return;this.busy.set(true);try{const page=await this.api.audit(id,after);if(id!==this.id)return;this.events.update(items=>[...items,...page.items]);this.nextSequence.set(page.nextSequence);}catch(e){this.error.set(problem(e));}finally{this.busy.set(false);}}
 private async perform(action:()=>Promise<unknown>,notice:string):Promise<void>{if(this.busy())return;const id=this.id;this.busy.set(true);this.error.set('');try{await action();if(id!==this.id)return;this.notice.set(notice);await this.reload();}catch(e){this.error.set(problem(e));}finally{this.busy.set(false);}}
}
