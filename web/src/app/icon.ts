import { Component, input } from '@angular/core';
const paths: Record<string, string> = {
 dashboard:'M3 3h7v7H3z M14 3h7v7h-7z M3 14h7v7H3z M14 14h7v7h-7z',
 apps:'M12 3 3 8l9 5 9-5-9-5ZM3 8v9l9 5 9-5V8M12 13v9',
 users:'M16 21v-2a4 4 0 0 0-4-4H6a4 4 0 0 0-4 4v2M16 3a4 4 0 0 1 0 8M22 21v-2a4 4 0 0 0-3-3.87M9 3a4 4 0 1 0 0 8 4 4 0 0 0 0-8Z',
 roles:'m12 3 10 5-10 5L2 8l10-5ZM2 12l10 5 10-5M2 16l10 5 10-5',
 activity:'M8 3h10l3 3v15H3V3h5ZM7 9h10M7 13h10M7 17h6',
 plus:'M12 5v14M5 12h14', search:'M21 21l-5-5M10 3a7 7 0 1 0 0 14 7 7 0 0 0 0-14Z',
 arrow:'M5 12h14m-6-6 6 6-6 6', check:'m5 12 4 4L19 6', clock:'M12 8v5l3 2M12 2a10 10 0 1 0 0 20 10 10 0 0 0 0-20Z',
 key:'M8 14a5 5 0 1 1 4-4l-8 8H1v-3l4-4M7 6h.01', help:'M9 9a3 3 0 0 1 6 0c0 2-3 2-3 4M12 17h.01M12 2a10 10 0 1 0 0 20 10 10 0 0 0 0-20Z',
 settings:'M4 5h16M4 12h16M4 19h16M8 2v6M16 9v6M10 16v6', logout:'M9 4H3v16h6M13 8l4 4-4 4M8 12h13', refresh:'M20 7V3l-3 3a8 8 0 1 0 3 12M20 7h-5',
 factory:'M3 21V9l6 3V7l6 4V3h6v18H3ZM7 16h1M12 16h1M17 16h1', menu:'M4 6h16M4 12h16M4 18h16', close:'m6 6 12 12M18 6 6 18',
 trash:'M3 6h18M8 6V3h8v3M5 6l1 15h12l1-15M10 10v7M14 10v7', edit:'m16 3 5 5-13 13H3v-5L16 3ZM13 6l5 5'
};
@Component({selector:'p-icon',standalone:true,template:'<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path [attr.d]="path()" /></svg>',styles:[':host{display:inline-flex;width:1.25rem;height:1.25rem;flex-shrink:0}svg{width:100%;height:100%}']})
export class Icon { readonly name = input('apps'); path(): string { return paths[this.name()] ?? paths['apps']!; } }
