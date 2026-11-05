import { page } from '/private/tmp/claude-501/-Users-admin-olimp-glechyk/a2ec8d08-6007-4765-b570-a92fbb040f3d/scratchpad/cdp/cdp.mjs';
import { writeFileSync } from 'node:fs';
// node crop.mjs port url w h mob out x y cw ch scale
const [,, port, url, w, h, mob, out, x, y, cw, ch, sc] = process.argv;
const p = await page(+port);
await p.send('Emulation.setDeviceMetricsOverride', { width:+w, height:+h, deviceScaleFactor:+sc||2, mobile:mob==='1' });
await p.go(url); await p.sleep(400);
const r = await p.send('Page.captureScreenshot', { format:'png', clip:{x:+x,y:+y,width:+cw,height:+ch,scale:1} });
writeFileSync(out, Buffer.from(r.result.data,'base64'));
p.close();
