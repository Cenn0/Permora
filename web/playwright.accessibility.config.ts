import { defineConfig } from '@playwright/test';
export default defineConfig({
 testDir:'./accessibility',fullyParallel:true,forbidOnly:true,retries:0,
 workers:2,timeout:60000,reporter:'list',
 use:{baseURL:'http://127.0.0.1:4200',trace:'off',screenshot:'off',video:'off'},
 projects:[
  {name:'desktop',use:{viewport:{width:1440,height:1000}}},
  {name:'mobile',use:{viewport:{width:390,height:844}}},
  {name:'reflow-320',use:{viewport:{width:320,height:900}}}
 ],
 webServer:{command:'npm start',url:'http://127.0.0.1:4200',reuseExistingServer:false}
});
