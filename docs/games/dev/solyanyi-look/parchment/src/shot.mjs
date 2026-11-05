import { page } from '/private/tmp/claude-501/-Users-admin-olimp-glechyk/a2ec8d08-6007-4765-b570-a92fbb040f3d/scratchpad/cdp/cdp.mjs';
const [,, port, url, out, w, h, mob] = process.argv;
const p = await page(+port);
await p.send('Emulation.setDeviceMetricsOverride', { width:+w, height:+h, deviceScaleFactor:1, mobile:mob==='1' });
await p.go(url);
await p.shot(out,+w,+h,mob==='1');
console.log(JSON.stringify(p.logs()).slice(0,800));
p.close();
