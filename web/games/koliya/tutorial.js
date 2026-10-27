// «Колія» — навчальна смуга (K22): ручна розкладка, детермінована, без сіду. Перша партія має навчити, а не вбити.
// Контракт:
//  TUTOR_PARAMS — що навчання ставить поверх params (повільний паротяг, довга пауза, котел терплячий, перегрів — зупинка,
//    без гусей і проміжних станцій). rules.newGame за params.mode === 'tutorial' бере tutorialParams(params) і tutorialWorld.
//  tutorialParams(params) → нові params; tutorialWorld(params) → { w,h,ground,obj,hp,drop,dropN,path,spawn,station,zones }
//    (той самий формат, що gen.generate). TUTOR_MAP — сама мапа (рядки), для тестів і тих, хто хоче глянути.
//
// По черзі, як вчить: галявина з сокирою, кайлом і відром → дерева просто перед колією (одне — на самій колії: «розчисть»)
// → склад поруч (вагони стоять на галявині) → скелі із залізом трохи далі → вузька річка на 1 клітинку (місток із дров;
// до містка на той бік не перейти — інакше й не вчить) → калюжа для відра → гайок і скелі на другу половину → степ →
// станція. Котел уперше гріється (підказка «відро → вода → паротяг»), коли паротяг десь біля скель — річка за 6–8 кроків.
import { G, O, I } from './rules.js';

// # стіна  . трава  T дерево  R скеля  ~ вода  S станція  = колія на старті
// a сокира  p кайло  b відро (лежать на траві)
export const TUTOR_MAP = [
  '########################################################',
  '...................~....................................',
  '........TTT...RR...~....TTT...........TT................',
  '........TTT...R....~....TTT..................TT.........',
  '....ap..TTT........~....................................',
  '..................#~.................................SSS',
  '======...T.........~.................................SSS',
  '..................#~.................................SSS',
  '....b........RRR...~.......~~...........................',
  '.........TT..RRR...~.......~~..RRR......R.......R.......',
  '.........TT..RRR...~...........RRR........T.............',
  '...................~....................................',
  '########################################################',
];

// Біоми для HUD (на правила не впливають); x < 9 — галявина
const ZONES = [
  { x0: 9, x1: 12, kind: 'grove' },
  { x0: 12, x1: 18, kind: 'stone' },
  { x0: 18, x1: 22, kind: 'river' },
  { x0: 22, x1: 36, kind: 'grove' },
  { x0: 36, x1: 53, kind: 'steppe' },
];

export const TUTOR_PARAMS = {
  speedStart: 0.08,     // типово 0.12
  speedGrowth: 0.01,    // типово 0.03
  speedMax: 0.25,
  speedCurve: 'linear',
  startSec: 60,         // хвилина на роздивитись, узяти сокиру й зрубати перше дерево
  heatSec: 150,         // котел уперше кричить через ~1,5 хв їзди
  soloSpeed: 1,         // K13: без соло-знижки — навчання й так повільне, а знижка розтягнула б котел до ~4,5 хв (підказка про відро не встигала)
  fireSec: 30,
  overheat: 'stop',     // перегрів — паротяг стоїть, доки не поллєш (не пожежа й не поразка)
  geese: 'off',
  stationMode: 'end',
  stripLen: TUTOR_MAP[0].length,
  stripWidth: TUTOR_MAP.length,
};

export function tutorialParams(params) {
  return { ...params, ...TUTOR_PARAMS, mode: 'tutorial' };
}

export function tutorialWorld(params) {
  const h = TUTOR_MAP.length, w = TUTOR_MAP[0].length, n = w * h;
  const ground = new Uint8Array(n), obj = new Uint8Array(n), hp = new Uint8Array(n);
  const drop = new Uint8Array(n), dropN = new Uint8Array(n);
  const path = [];
  const tH = Math.max(1, Math.min(255, params.treeHits | 0 || 3)), rH = Math.max(1, Math.min(255, params.rockHits | 0 || 4));
  for (let y = 0; y < h; y++) {
    const row = TUTOR_MAP[y];
    if (row.length !== w) throw new Error(`tutorial: рядок ${y} завдовжки ${row.length}, а не ${w}`);
    for (let x = 0; x < w; x++) {
      const i = y * w + x;
      switch (row[x]) {
        case '#': obj[i] = O.WALL; break;
        case 'T': obj[i] = O.TREE; hp[i] = tH; break;
        case 'R': obj[i] = O.ROCK; hp[i] = rH; break;
        case '~': ground[i] = G.WATER; break;
        case 's': ground[i] = G.SAND; break;
        case 'S': ground[i] = G.STATION; break;
        case '=': path.push(i); break;   // зліва направо — від хвоста
        case 'a': drop[i] = I.AXE; dropN[i] = 1; break;
        case 'p': drop[i] = I.PICK; dropN[i] = 1; break;
        case 'b': drop[i] = I.BUCKET; dropN[i] = 1; break;
      }
    }
  }
  const mid = (path[0] / w) | 0;
  const spawn = [{ x: 2, y: mid - 1 }, { x: 3, y: mid + 1 }, { x: 1, y: mid - 2 }, { x: 2, y: mid + 2 }];
  const station = { x0: w - 3, y0: mid - 1, x1: w - 1, y1: mid + 1 };
  const zones = ZONES.map((z) => ({ ...z, d: 0, traps: 0 }));
  return { w, h, ground, obj, hp, drop, dropN, path, spawn, station, zones };
}
