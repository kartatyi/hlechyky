// «Колія» — налагоджувальний вид згори на 2D-канві (?view=2d). Той самий інтерфейс, що й render.js.
import { U, G, O, I, WAGONS, wagonPos } from './rules.js';

const GC = ['#5a8f3a', '#2f6fb0', '#8a6a3a', '#c9b26b', '#d8c88a', '#55603a', '#bfe6f5', '#eef2f5', '#2a1d14', '#b07a40'];   // …, болото (K7), лід, сніг, урвище, каркас (K26)
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
      s.players.forEach((p, n) => {   // приціл (K14, aim='smart') — рамка кольором гравця
        if (p.aim) { g.strokeStyle = PC[n]; g.lineWidth = 2; g.strokeRect(p.aim.x * c + 1, p.aim.y * c + 1, c - 2, c - 2); g.lineWidth = 1; }
      });
      s.players.forEach((p, n) => {
        g.fillStyle = PC[n]; g.beginPath(); g.arc(p.x / U * c, p.y / U * c, c * 0.3, 0, 7); g.fill();
        g.strokeStyle = '#000'; g.beginPath(); g.moveTo(p.x / U * c, p.y / U * c); g.lineTo((p.x / U + p.fx * 0.5) * c, (p.y / U + p.fy * 0.5) * c); g.stroke();
        if (p.hold.k) { g.fillStyle = IC[p.hold.k]; g.fillRect(p.x / U * c - 3, p.y / U * c - c * 0.6, 6, 6); }
        if (p.stun > 0) {   // K18: зірочки
          g.fillStyle = '#fff36b';
          for (let k = 0; k < 3; k++) {
            const a = s.tick * 0.5 + k * 2.1;
            g.fillRect(p.x / U * c + Math.cos(a) * c * 0.35 - 2, p.y / U * c - c * 0.55 + Math.sin(a) * c * 0.12 - 2, 4, 4);
          }
        }
      });
      for (const f of s.flying || []) {   // K18: кинуте — тінь на землі й річ над нею
        g.fillStyle = 'rgba(0,0,0,.35)'; g.beginPath(); g.ellipse(f.x / U * c, f.y / U * c, c * 0.2, c * 0.1, 0, 0, 7); g.fill();
        g.fillStyle = IC[f.k] || '#fff'; g.fillRect(f.x / U * c - c * 0.18, (f.y - f.z) / U * c - c * 0.18, c * 0.36, c * 0.36);
      }
      g.restore();
    },
  };
}
