// «Колія» — налагоджувальний вид згори на 2D-канві (?view=2d). Той самий інтерфейс, що й render.js.
import { U, G, O, I, WAGONS, wagonPos } from './rules.js';

const GC = ['#5a8f3a', '#2f6fb0', '#8a6a3a', '#c9b26b', '#d8c88a'];
const OC = [null, '#1f5a22', '#7d7f86', '#3b3b40'];
const IC = [null, '#a0703a', '#c0c4cc', '#555', '#d33', '#38c', '#9bd', '#16e'];
const PC = ['#ffd23f', '#ff5fa2', '#5fd3ff', '#b6ff5f'];

export function createDebug2d(canvas) {
  const g = canvas.getContext('2d');
  return {
    setWorld() { },
    events() { },
    draw(s) {
      const W = canvas.width = canvas.clientWidth, H = canvas.height = canvas.clientHeight;
      const c = Math.max(8, Math.floor(H / s.h));
      const head = wagonPos(s, 0).x / U;
      const ox = Math.max(0, Math.min(s.w * c - W, head * c - W * 0.35));
      g.fillStyle = '#111'; g.fillRect(0, 0, W, H);
      g.save(); g.translate(-ox, (H - s.h * c) / 2);
      for (let y = 0; y < s.h; y++) for (let x = 0; x < s.w; x++) {
        const i = y * s.w + x;
        g.fillStyle = GC[s.ground[i]] || '#f0f'; g.fillRect(x * c, y * c, c, c);
        if (s.obj[i]) { g.fillStyle = OC[s.obj[i]]; g.fillRect(x * c + 2, y * c + 2, c - 4, c - 4); }
        if (s.drop[i]) { g.fillStyle = IC[s.drop[i]]; g.fillRect(x * c + c / 4, y * c + c / 4, c / 2, c / 2); }
      }
      g.fillStyle = '#444';
      for (const p of s.path) g.fillRect((p % s.w) * c + 2, Math.floor(p / s.w) * c + c / 2 - 2, c - 4, 4);
      WAGONS.forEach((k, n) => {
        const w = wagonPos(s, n);
        g.fillStyle = n === 0 ? (s.train.fire ? '#f60' : '#222') : n === 1 ? '#6b4' : '#a64';
        g.fillRect(w.x / U * c - c * 0.4, w.y / U * c - c * 0.4, c * 0.8, c * 0.8);
      });
      s.players.forEach((p, n) => {
        g.fillStyle = PC[n]; g.beginPath(); g.arc(p.x / U * c, p.y / U * c, c * 0.3, 0, 7); g.fill();
        g.strokeStyle = '#000'; g.beginPath(); g.moveTo(p.x / U * c, p.y / U * c); g.lineTo((p.x / U + p.fx * 0.5) * c, (p.y / U + p.fy * 0.5) * c); g.stroke();
        if (p.hold.k) { g.fillStyle = IC[p.hold.k]; g.fillRect(p.x / U * c - 3, p.y / U * c - c * 0.6, 6, 6); }
      });
      g.restore();
    },
  };
}
