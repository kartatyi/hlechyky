import { page } from '/private/tmp/claude-501/-Users-admin-olimp-glechyk/a2ec8d08-6007-4765-b570-a92fbb040f3d/scratchpad/cdp/cdp.mjs';
const D=process.argv[2];
const p = await page(9412);
await p.send('Emulation.setDeviceMetricsOverride', { width:390, height:844, deviceScaleFactor:1, mobile:true });
await p.go('file://'+D+'/phone.html');
console.log(JSON.stringify(await p.eval(`({doc:[document.documentElement.scrollWidth,document.documentElement.scrollHeight],
 hdr:document.querySelector('.site').getBoundingClientRect().height, tabs:document.querySelector('.mtabs').getBoundingClientRect().height,
 game:document.querySelector('.game').getBoundingClientRect().height, sum:[...document.querySelector('.game').children].filter(e=>getComputedStyle(e).position!='absolute').reduce((a,e)=>a+e.getBoundingClientRect().height,0),
 small:[...document.querySelectorAll('button')].map(b=>{const r=b.getBoundingClientRect();return [b.className||b.textContent.trim().slice(0,12),Math.round(r.width),Math.round(r.height),Math.round(r.right)]}).filter(x=>x[1]<40||x[2]<40||x[3]>390)})`)));
await p.send('Emulation.setDeviceMetricsOverride', { width:1600, height:900, deviceScaleFactor:1, mobile:false });
await p.go('file://'+D+'/pc.html');
console.log(JSON.stringify(await p.eval(`({doc:[document.documentElement.scrollWidth,document.documentElement.scrollHeight], ledger:[...document.querySelectorAll('.ledger>section')].map(s=>[s.className,Math.round(s.getBoundingClientRect().top),Math.round(s.getBoundingClientRect().bottom)]), torba:document.querySelector('.torba').scrollHeight+'/'+document.querySelector('.torba').clientHeight, act:document.querySelector('.action').scrollHeight+'/'+document.querySelector('.action').clientHeight, fonts:[...document.fonts].filter(f=>f.status=='loaded').length})`)));
p.close();
