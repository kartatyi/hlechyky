namespace Hlechyky.Games.Impl;

/// <summary>
/// «Вогник і Крапля»: детермінована симуляція одного рівня (specs/vohnyk.md §2, §5). Лише цілі числа, жодного
/// double і жодного Random: той самий рівень і той самий ввід дають побітово той самий світ і в C#, і в
/// <c>web/games/vohnyk-sim.js</c> — клієнт передбачає себе, сервер лишається суддею. Про кімнати, фази й мережу
/// тут не знають нічого. Будь-яка правка тут — ДОСЛІВНО й у JS, інакше впадуть тести паритету (і це добре).
/// <para>
/// Одиниці: субпікселі (su), 1 px = 16 su, плитка = 640 su; крок = 20 мс; швидкості — su/крок.
/// </para>
/// </summary>
public sealed class VohnykWorld
{
    public const int Px = 16, TileSu = 640;
    public const int HeroW = 384, HeroH = 576;
    public const int RunMax = 72, RunAcc = 12, AirAcc = 8, Friction = 16, AirDrag = 4;
    public const int Gravity = 12, FallMax = 240;
    public const int JumpV = 190, JumpMin = 4, Coyote = 4, BufferSteps = 5;
    public const int BoxSize = 640, BoxPush = 32;
    public const int DoorSpeed = 96, LiftSpeed = 48, LiftH = 256;
    public const int ExitHold = 10;
    /// <summary>Зона ніг: відступ з боків і висота від низу AABB.</summary>
    public const int FeetInset = 4 * Px, FeetH = 16 * Px;
    public const int KeyLeft = 1, KeyRight = 2, KeyJump = 4;
    /// <summary>Скільки int на героя в знімку стану.</summary>
    public const int HeroInts = 14;

    public readonly VohnykLevel Level;
    readonly int _nb, _nl, _nd, _nf, _nx;

    // ---- герої (0 — Вогник, 1 — Крапля) ----
    public readonly int[] X = new int[2], Y = new int[2], Vx = new int[2], Vy = new int[2], K = new int[2];
    public readonly int[] Grounded = new int[2], Facing = new int[2], CoyoteLeft = new int[2], Buffer = new int[2];
    /// <summary>Кроки від стрибка; −1 — не в стрибку (зрізати нема що).</summary>
    public readonly int[] JumpAge = new int[2];
    public readonly int[] WantCut = new int[2], InExit = new int[2], Died = new int[2], LeverIn = new int[2];
    // ---- скрині, двері, ліфти, важелі, кнопки ----
    public readonly int[] BoxX, BoxY, BoxVy;
    /// <summary>Наскільки відчинено двері, su (0..висота).</summary>
    public readonly int[] DoorO;
    public readonly int[] LiftX, LiftY;
    public readonly int[] Lever, Button;
    public int Gems, Hold, Cleared;

    // ---- статичне з рівня, у su ----
    readonly int[] _doorX, _doorY, _doorH, _liftAX, _liftAY, _liftBX, _liftBY, _liftW;
    readonly int[] _gemX, _gemY, _btnX, _btnY, _levX, _levY, _exitX, _exitY;
    // тимчасове для ліфтів (без алокацій у кроці): хто їде
    readonly bool[] _riderHero = new bool[2];
    readonly bool[] _riderBox;

    public VohnykWorld(VohnykLevel level)
    {
        Level = level;
        _nb = level.Buttons.Length;
        _nl = level.Levers.Length;
        _nd = level.Doors.Length;
        _nf = level.Lifts.Length;
        _nx = level.Boxes.Length;
        BoxX = new int[_nx]; BoxY = new int[_nx]; BoxVy = new int[_nx]; _riderBox = new bool[_nx];
        DoorO = new int[_nd];
        LiftX = new int[_nf]; LiftY = new int[_nf];
        Lever = new int[_nl]; Button = new int[_nb];
        _doorX = new int[_nd]; _doorY = new int[_nd]; _doorH = new int[_nd];
        for (var i = 0; i < _nd; i++)
        {
            var d = level.Doors[i];
            _doorX[i] = d.Col * TileSu; _doorY[i] = d.Row * TileSu; _doorH[i] = d.Tiles * TileSu;
        }
        _liftAX = new int[_nf]; _liftAY = new int[_nf]; _liftBX = new int[_nf]; _liftBY = new int[_nf]; _liftW = new int[_nf];
        for (var i = 0; i < _nf; i++)
        {
            var l = level.Lifts[i];
            _liftAX[i] = l.Col * TileSu; _liftAY[i] = l.Row * TileSu;
            _liftBX[i] = l.ToCol * TileSu; _liftBY[i] = l.ToRow * TileSu;
            _liftW[i] = l.Tiles * TileSu;
        }
        var ng = level.Gems.Length;
        _gemX = new int[ng]; _gemY = new int[ng];
        for (var i = 0; i < ng; i++)
        {
            _gemX[i] = level.Gems[i].Col * TileSu + 10 * Px;
            _gemY[i] = level.Gems[i].Row * TileSu + 10 * Px;
        }
        _btnX = new int[_nb]; _btnY = new int[_nb];
        for (var i = 0; i < _nb; i++)
        {
            _btnX[i] = level.Buttons[i].Col * TileSu + 4 * Px;
            _btnY[i] = level.Buttons[i].Row * TileSu + 32 * Px;
        }
        _levX = new int[_nl]; _levY = new int[_nl];
        for (var i = 0; i < _nl; i++)
        {
            _levX[i] = level.Levers[i].Col * TileSu;
            _levY[i] = level.Levers[i].Row * TileSu;
        }
        _exitX = [level.Exits[0].Col * TileSu, level.Exits[1].Col * TileSu];
        _exitY = [level.Exits[0].Row * TileSu, level.Exits[1].Row * TileSu];
        StateLength = 2 * HeroInts + 3 * _nx + _nd + 2 * _nf + _nl + _nb + 3;
        Reset(keepGems: false);
    }

    /// <summary>Довжина знімка стану (<see cref="Save"/>/<see cref="Load"/>).</summary>
    public int StateLength { get; }

    public int SignalMask
    {
        get
        {
            var m = 0;
            for (var i = 0; i < _nb; i++) if (Button[i] != 0) m |= 1 << i;
            for (var i = 0; i < _nl; i++) if (Lever[i] != 0) m |= 1 << (_nb + i);
            return m;
        }
    }

    public bool AnyDied => Died[0] != 0 || Died[1] != 0;

    // ============================================================================================
    // Скидання, знімки, хеш
    // ============================================================================================

    /// <summary>Усе на старт: герої, скрині, двері зачинені, ліфти в <c>at</c>, важелі в init. Самоцвіти — за бажанням.</summary>
    public void Reset(bool keepGems)
    {
        for (var i = 0; i < 2; i++)
        {
            var (c, r) = Level.Spawn[i];
            X[i] = c * TileSu + 8 * Px;
            Y[i] = (r + 1) * TileSu - HeroH;
            Vx[i] = 0; Vy[i] = 0; K[i] = 0;
            Grounded[i] = 1; Facing[i] = 1; CoyoteLeft[i] = Coyote; Buffer[i] = 0; JumpAge[i] = -1;
            WantCut[i] = 0; InExit[i] = 0; Died[i] = 0; LeverIn[i] = 0;
        }
        for (var i = 0; i < _nx; i++)
        {
            BoxX[i] = Level.Boxes[i].Col * TileSu;
            BoxY[i] = Level.Boxes[i].Row * TileSu;
            BoxVy[i] = 0;
        }
        for (var i = 0; i < _nd; i++) DoorO[i] = 0;
        for (var i = 0; i < _nf; i++) { LiftX[i] = _liftAX[i]; LiftY[i] = _liftAY[i]; }
        for (var i = 0; i < _nl; i++) Lever[i] = Level.Levers[i].Init;
        for (var i = 0; i < _nb; i++) Button[i] = 0;
        if (!keepGems) Gems = 0;
        Hold = 0;
        Cleared = 0;
        // Сигнали й «хто в клітинці важеля» — одразу чесні: кнопка під стартовою скринею натиснута з першого кроку.
        UpdateSignals();
    }

    public void Save(int[] s)
    {
        var p = 0;
        for (var i = 0; i < 2; i++)
        {
            s[p++] = X[i]; s[p++] = Y[i]; s[p++] = Vx[i]; s[p++] = Vy[i]; s[p++] = K[i];
            s[p++] = Grounded[i]; s[p++] = Facing[i]; s[p++] = CoyoteLeft[i]; s[p++] = Buffer[i]; s[p++] = JumpAge[i];
            s[p++] = WantCut[i]; s[p++] = InExit[i]; s[p++] = Died[i]; s[p++] = LeverIn[i];
        }
        for (var i = 0; i < _nx; i++) { s[p++] = BoxX[i]; s[p++] = BoxY[i]; s[p++] = BoxVy[i]; }
        for (var i = 0; i < _nd; i++) s[p++] = DoorO[i];
        for (var i = 0; i < _nf; i++) { s[p++] = LiftX[i]; s[p++] = LiftY[i]; }
        for (var i = 0; i < _nl; i++) s[p++] = Lever[i];
        for (var i = 0; i < _nb; i++) s[p++] = Button[i];
        s[p++] = Gems; s[p++] = Hold; s[p] = Cleared;
    }

    public void Load(int[] s)
    {
        var p = 0;
        for (var i = 0; i < 2; i++)
        {
            X[i] = s[p++]; Y[i] = s[p++]; Vx[i] = s[p++]; Vy[i] = s[p++]; K[i] = s[p++];
            Grounded[i] = s[p++]; Facing[i] = s[p++]; CoyoteLeft[i] = s[p++]; Buffer[i] = s[p++]; JumpAge[i] = s[p++];
            WantCut[i] = s[p++]; InExit[i] = s[p++]; Died[i] = s[p++]; LeverIn[i] = s[p++];
        }
        for (var i = 0; i < _nx; i++) { BoxX[i] = s[p++]; BoxY[i] = s[p++]; BoxVy[i] = s[p++]; }
        for (var i = 0; i < _nd; i++) DoorO[i] = s[p++];
        for (var i = 0; i < _nf; i++) { LiftX[i] = s[p++]; LiftY[i] = s[p++]; }
        for (var i = 0; i < _nl; i++) Lever[i] = s[p++];
        for (var i = 0; i < _nb; i++) Button[i] = s[p++];
        Gems = s[p++]; Hold = s[p++]; Cleared = s[p];
    }

    /// <summary>FNV-1a 32-біт над знімком (кожен int — 4 байти little-endian). Так само в JS (Math.imul, &gt;&gt;&gt; 0).</summary>
    public static uint Hash(int[] s, int length)
    {
        var h = 2166136261u;
        for (var i = 0; i < length; i++)
        {
            var v = (uint)s[i];
            h = (h ^ (v & 0xff)) * 16777619u;
            h = (h ^ ((v >> 8) & 0xff)) * 16777619u;
            h = (h ^ ((v >> 16) & 0xff)) * 16777619u;
            h = (h ^ (v >> 24)) * 16777619u;
        }
        return h;
    }

    readonly int[] _hashBuf = new int[128];

    public uint Hash()
    {
        Save(_hashBuf);
        return Hash(_hashBuf, StateLength);
    }

    // ============================================================================================
    // Крок (§5.1): ліфти → двері → герої → скрині → сигнали → самоцвіти → небезпека → виходи
    // ============================================================================================

    public void Step(int kFire, int kWater)
    {
        var sig = SignalMask;
        for (var i = 0; i < _nf; i++) StepLift(i, sig);
        for (var i = 0; i < _nd; i++) StepDoor(i, sig);
        StepHero(0, kFire & 7);
        StepHero(1, kWater & 7);
        for (var i = 0; i < _nx; i++) StepBox(i);
        UpdateSignals();
        // самоцвіти
        for (var g = 0; g < _gemX.Length; g++)
        {
            if ((Gems & (1 << g)) != 0) continue;
            var who = Level.Gems[g].Who;
            if (Overlap(X[who], Y[who], HeroW, HeroH, _gemX[g], _gemY[g], 20 * Px, 20 * Px)) Gems |= 1 << g;
        }
        // небезпека: зона ніг у чужій рідині чи болоті
        for (var i = 0; i < 2; i++)
            if (Died[i] == 0 && FeetInDanger(i)) Died[i] = 1;
        // виходи
        for (var i = 0; i < 2; i++)
        {
            var cx = X[i] + HeroW / 2;
            var cy = Y[i] + HeroH / 2;
            InExit[i] = Grounded[i] != 0 && cx >= _exitX[i] && cx < _exitX[i] + TileSu && cy >= _exitY[i] && cy < _exitY[i] + 2 * TileSu ? 1 : 0;
        }
        if (InExit[0] != 0 && InExit[1] != 0)
        {
            Hold++;
            if (Hold >= ExitHold) Cleared = 1;
        }
        else Hold = 0;
    }

    static bool Active(int sig, int mask, bool all, bool inv)
    {
        var on = all ? (sig & mask) == mask : (sig & mask) != 0;
        return on != inv;
    }

    // ---------- ліфти ----------

    void StepLift(int i, int sig)
    {
        var def = Level.Lifts[i];
        var active = Active(sig, def.ByMask, def.All, def.Inv);
        var tx = active ? _liftBX[i] : _liftAX[i];
        var ty = active ? _liftBY[i] : _liftAY[i];
        var dx = Clamp(tx - LiftX[i], -LiftSpeed, LiftSpeed);
        var dy = Clamp(ty - LiftY[i], -LiftSpeed, LiftSpeed);
        if (dx == 0 && dy == 0) return;
        var lx = LiftX[i];
        var ly = LiftY[i];
        var lw = _liftW[i];
        // Пасажири: стоять на плиті (низ == верх плити, є перетин по X), плюс герої, що стоять на скрині-пасажирці.
        _riderHero[0] = _riderHero[1] = false;
        for (var b = 0; b < _nx; b++)
            _riderBox[b] = BoxY[b] + BoxSize == ly && BoxX[b] < lx + lw && BoxX[b] + BoxSize > lx;
        for (var h = 0; h < 2; h++)
        {
            var feet = Y[h] + HeroH;
            if (feet == ly && X[h] < lx + lw && X[h] + HeroW > lx) { _riderHero[h] = true; continue; }
            for (var b = 0; b < _nx; b++)
                if (_riderBox[b] && feet == BoxY[b] && X[h] < BoxX[b] + BoxSize && X[h] + HeroW > BoxX[b]) { _riderHero[h] = true; break; }
        }
        // Нова плита не давить: перетнула б героя чи скриню, що не їде, — стоїть цього кроку.
        var nx = lx + dx;
        var ny = ly + dy;
        for (var h = 0; h < 2; h++)
            if (!_riderHero[h] && Overlap(nx, ny, lw, LiftH, X[h], Y[h], HeroW, HeroH)) return;
        for (var b = 0; b < _nx; b++)
            if (!_riderBox[b] && Overlap(nx, ny, lw, LiftH, BoxX[b], BoxY[b], BoxSize, BoxSize)) return;
        // Пасажира впирає в тверде — ліфт стоїть.
        for (var h = 0; h < 2; h++)
            if (_riderHero[h] && HeroBlockedAt(h, X[h] + dx, Y[h] + dy, i)) return;
        for (var b = 0; b < _nx; b++)
            if (_riderBox[b] && BoxBlockedAt(b, BoxX[b] + dx, BoxY[b] + dy, i)) return;
        LiftX[i] = nx;
        LiftY[i] = ny;
        for (var h = 0; h < 2; h++)
            if (_riderHero[h]) { X[h] += dx; Y[h] += dy; }
        for (var b = 0; b < _nx; b++)
            if (_riderBox[b]) { BoxX[b] += dx; BoxY[b] += dy; }
    }

    /// <summary>Чи впирається герой-пасажир у тверде в позиції (x, y): плитки, двері, інші ліфти, скрині-не-пасажирки.</summary>
    bool HeroBlockedAt(int h, int x, int y, int lift)
    {
        if (TileSolidIn(x, y, HeroW, HeroH, h)) return true;
        for (var d = 0; d < _nd; d++)
            if (Overlap(x, y, HeroW, HeroH, _doorX[d], _doorY[d], TileSu, _doorH[d] - DoorO[d])) return true;
        for (var f = 0; f < _nf; f++)
            if (f != lift && Overlap(x, y, HeroW, HeroH, LiftX[f], LiftY[f], _liftW[f], LiftH)) return true;
        for (var b = 0; b < _nx; b++)
            if (!_riderBox[b] && Overlap(x, y, HeroW, HeroH, BoxX[b], BoxY[b], BoxSize, BoxSize)) return true;
        return false;
    }

    bool BoxBlockedAt(int box, int x, int y, int lift)
    {
        if (TileSolidIn(x, y, BoxSize, BoxSize, -1)) return true;
        for (var d = 0; d < _nd; d++)
            if (Overlap(x, y, BoxSize, BoxSize, _doorX[d], _doorY[d], TileSu, _doorH[d] - DoorO[d])) return true;
        for (var f = 0; f < _nf; f++)
            if (f != lift && Overlap(x, y, BoxSize, BoxSize, LiftX[f], LiftY[f], _liftW[f], LiftH)) return true;
        for (var b = 0; b < _nx; b++)
            if (b != box && !_riderBox[b] && Overlap(x, y, BoxSize, BoxSize, BoxX[b], BoxY[b], BoxSize, BoxSize)) return true;
        return false;
    }

    // ---------- двері ----------

    void StepDoor(int i, int sig)
    {
        var def = Level.Doors[i];
        var h = _doorH[i];
        if (Active(sig, def.ByMask, def.All, def.Inv))
        {
            DoorO[i] = Math.Min(h, DoorO[i] + DoorSpeed);
            return;
        }
        if (DoorO[i] == 0) return;
        var o = Math.Max(0, DoorO[i] - DoorSpeed);
        // Зачинення не давить: новий твердий прямокутник перетнув би героя чи скриню — двері цього кроку стоять.
        var dh = h - o;
        for (var k = 0; k < 2; k++)
            if (Overlap(_doorX[i], _doorY[i], TileSu, dh, X[k], Y[k], HeroW, HeroH)) return;
        for (var b = 0; b < _nx; b++)
            if (Overlap(_doorX[i], _doorY[i], TileSu, dh, BoxX[b], BoxY[b], BoxSize, BoxSize)) return;
        DoorO[i] = o;
    }

    // ---------- герої ----------

    void StepHero(int i, int k)
    {
        var edge = (k & KeyJump) != 0 && (K[i] & KeyJump) == 0;
        K[i] = k;
        // Скриня впала на голову (скрині героїв не бачать) — герой вилазить нагору й стоїть на ній.
        for (var b = 0; b < _nx; b++)
            if (Overlap(X[i], Y[i], HeroW, HeroH, BoxX[b], BoxY[b], BoxSize, BoxSize))
            {
                Y[i] = BoxY[b] - HeroH;
                if (Vy[i] > 0) Vy[i] = 0;
            }

        // а) горизонталь
        var dir = ((k & KeyRight) != 0 ? 1 : 0) - ((k & KeyLeft) != 0 ? 1 : 0);
        var ground = Grounded[i] != 0;
        if (dir != 0)
        {
            var acc = ground ? RunAcc : AirAcc;
            // розворот гальмує ще й тертям — інакше з повного бігу назад їхали б дванадцять кроків
            if (Vx[i] * dir < 0) acc += ground ? Friction : AirDrag;
            Vx[i] = Clamp(Vx[i] + dir * acc, -RunMax, RunMax);
            Facing[i] = dir > 0 ? 1 : 0;
        }
        else
        {
            var dec = ground ? Friction : AirDrag;
            Vx[i] = Vx[i] > 0 ? Math.Max(0, Vx[i] - dec) : Math.Min(0, Vx[i] + dec);
        }
        if (Vx[i] != 0) MoveHeroX(i, Vx[i]);

        // б) вертикаль: буфер/койот → стрибок; зріз; рух; ПОТІМ гравітація
        if (edge) Buffer[i] = BufferSteps;
        if (Buffer[i] > 0 && (ground || CoyoteLeft[i] > 0))
        {
            Vy[i] = -JumpV;
            JumpAge[i] = 0;
            WantCut[i] = 0;
            Buffer[i] = 0;
            CoyoteLeft[i] = 0;
        }
        else if (Buffer[i] > 0 && !edge) Buffer[i]--;   // крок натиску не рахується: буфер — 5 наступних кроків
        if (JumpAge[i] >= 0 && (k & KeyJump) == 0) WantCut[i] = 1;
        if (WantCut[i] != 0 && JumpAge[i] >= JumpMin && Vy[i] < 0)
        {
            Vy[i] = Vy[i] * 2 / 5;
            WantCut[i] = 0;
            JumpAge[i] = -1;
        }
        if (Vy[i] != 0) MoveHeroY(i, Vy[i]);
        Vy[i] = Math.Min(Vy[i] + Gravity, FallMax);
        if (JumpAge[i] >= 0)
        {
            JumpAge[i]++;
            if (Vy[i] >= 0) { JumpAge[i] = -1; WantCut[i] = 0; }
        }

        // в) опора в 1 su під ногами
        Grounded[i] = Vy[i] >= 0 && HeroSupported(i) ? 1 : 0;
        // Койот: крок, на якому опора зникла, запас не їсть — ще 4 кроки після нього можна стрибнути.
        CoyoteLeft[i] = Grounded[i] != 0 ? Coyote : ground ? CoyoteLeft[i] : Math.Max(0, CoyoteLeft[i] - 1);
    }

    void MoveHeroX(int i, int dx)
    {
        var x0 = X[i];
        var y0 = Y[i];
        var limit = x0 + dx;
        var r0 = FloorDiv(y0, TileSu);
        var r1 = FloorDiv(y0 + HeroH - 1, TileSu);
        if (dx > 0)
        {
            var front = x0 + HeroW;
            for (var c = FloorDiv(front - 1, TileSu) + 1; c <= FloorDiv(limit + HeroW - 1, TileSu); c++)
                for (var r = r0; r <= r1; r++)
                    if (SolidFor(Level.Tile(c, r), i)) { limit = Math.Min(limit, c * TileSu - HeroW); break; }
            for (var d = 0; d < _nd; d++)
            {
                var dh = _doorH[d] - DoorO[d];
                if (dh > 0 && _doorY[d] < y0 + HeroH && _doorY[d] + dh > y0 && _doorX[d] >= front && _doorX[d] < limit + HeroW)
                    limit = _doorX[d] - HeroW;
            }
            for (var f = 0; f < _nf; f++)
                if (LiftY[f] < y0 + HeroH && LiftY[f] + LiftH > y0 && LiftX[f] >= front && LiftX[f] < limit + HeroW)
                    limit = LiftX[f] - HeroW;
            // скриня попереду — штовхаємо
            var box = -1;
            for (var b = 0; b < _nx; b++)
                if (BoxY[b] < y0 + HeroH && BoxY[b] + BoxSize > y0 && BoxX[b] >= front && BoxX[b] < limit + HeroW && (box < 0 || BoxX[b] < BoxX[box]))
                    box = b;
            if (box >= 0)
            {
                var push = Math.Min(limit + HeroW - BoxX[box], BoxPush);
                PushBox(box, push, i);
                limit = Math.Min(limit, BoxX[box] - HeroW);
            }
        }
        else
        {
            for (var c = FloorDiv(x0, TileSu) - 1; c >= FloorDiv(limit, TileSu); c--)
                for (var r = r0; r <= r1; r++)
                    if (SolidFor(Level.Tile(c, r), i)) { limit = Math.Max(limit, (c + 1) * TileSu); break; }
            for (var d = 0; d < _nd; d++)
            {
                var dh = _doorH[d] - DoorO[d];
                var right = _doorX[d] + TileSu;
                if (dh > 0 && _doorY[d] < y0 + HeroH && _doorY[d] + dh > y0 && right <= x0 && right > limit)
                    limit = right;
            }
            for (var f = 0; f < _nf; f++)
            {
                var right = LiftX[f] + _liftW[f];
                if (LiftY[f] < y0 + HeroH && LiftY[f] + LiftH > y0 && right <= x0 && right > limit)
                    limit = right;
            }
            var box = -1;
            for (var b = 0; b < _nx; b++)
            {
                var right = BoxX[b] + BoxSize;
                if (BoxY[b] < y0 + HeroH && BoxY[b] + BoxSize > y0 && right <= x0 && right > limit && (box < 0 || right > BoxX[box] + BoxSize))
                    box = b;
            }
            if (box >= 0)
            {
                var push = Math.Max(limit - (BoxX[box] + BoxSize), -BoxPush);
                PushBox(box, push, i);
                limit = Math.Max(limit, BoxX[box] + BoxSize);
            }
        }
        if (limit != x0 + dx) Vx[i] = 0;
        X[i] = limit;
    }

    /// <summary>
    /// Штовхнути скриню на d su (знак — напрямок). Тверді для неї: камінь, двері, ліфти, інші скрині й інший герой
    /// (штовхнути скриню в партнера не можна — він стоїть стіною). Скриня, на якій лежить інша скриня, не зрушить.
    /// </summary>
    void PushBox(int b, int d, int pusher)
    {
        if (d == 0) return;
        var bx = BoxX[b];
        var by = BoxY[b];
        for (var o = 0; o < _nx; o++)
            if (o != b && BoxY[o] + BoxSize == by && BoxX[o] < bx + BoxSize && BoxX[o] + BoxSize > bx) return;
        var limit = bx + d;
        var r0 = FloorDiv(by, TileSu);
        var r1 = FloorDiv(by + BoxSize - 1, TileSu);
        var other = 1 - pusher;
        if (d > 0)
        {
            var front = bx + BoxSize;
            for (var c = FloorDiv(front - 1, TileSu) + 1; c <= FloorDiv(limit + BoxSize - 1, TileSu); c++)
                for (var r = r0; r <= r1; r++)
                    if (Level.Tile(c, r) == VohnykLevel.Stone) { limit = Math.Min(limit, c * TileSu - BoxSize); break; }
            for (var dd = 0; dd < _nd; dd++)
            {
                var dh = _doorH[dd] - DoorO[dd];
                if (dh > 0 && _doorY[dd] < by + BoxSize && _doorY[dd] + dh > by && _doorX[dd] >= front && _doorX[dd] < limit + BoxSize)
                    limit = _doorX[dd] - BoxSize;
            }
            for (var f = 0; f < _nf; f++)
                if (LiftY[f] < by + BoxSize && LiftY[f] + LiftH > by && LiftX[f] >= front && LiftX[f] < limit + BoxSize)
                    limit = LiftX[f] - BoxSize;
            for (var o = 0; o < _nx; o++)
                if (o != b && BoxY[o] < by + BoxSize && BoxY[o] + BoxSize > by && BoxX[o] >= front && BoxX[o] < limit + BoxSize)
                    limit = BoxX[o] - BoxSize;
            if (Y[other] < by + BoxSize && Y[other] + HeroH > by && X[other] >= front && X[other] < limit + BoxSize)
                limit = X[other] - BoxSize;
            BoxX[b] = Math.Max(bx, limit);
        }
        else
        {
            for (var c = FloorDiv(bx, TileSu) - 1; c >= FloorDiv(limit, TileSu); c--)
                for (var r = r0; r <= r1; r++)
                    if (Level.Tile(c, r) == VohnykLevel.Stone) { limit = Math.Max(limit, (c + 1) * TileSu); break; }
            for (var dd = 0; dd < _nd; dd++)
            {
                var dh = _doorH[dd] - DoorO[dd];
                var right = _doorX[dd] + TileSu;
                if (dh > 0 && _doorY[dd] < by + BoxSize && _doorY[dd] + dh > by && right <= bx && right > limit)
                    limit = right;
            }
            for (var f = 0; f < _nf; f++)
            {
                var right = LiftX[f] + _liftW[f];
                if (LiftY[f] < by + BoxSize && LiftY[f] + LiftH > by && right <= bx && right > limit)
                    limit = right;
            }
            for (var o = 0; o < _nx; o++)
            {
                var right = BoxX[o] + BoxSize;
                if (o != b && BoxY[o] < by + BoxSize && BoxY[o] + BoxSize > by && right <= bx && right > limit)
                    limit = right;
            }
            var hr = X[other] + HeroW;
            if (Y[other] < by + BoxSize && Y[other] + HeroH > by && hr <= bx && hr > limit)
                limit = hr;
            BoxX[b] = Math.Min(bx, limit);
        }
    }

    void MoveHeroY(int i, int dy)
    {
        var x0 = X[i];
        var y0 = Y[i];
        var limit = y0 + dy;
        var c0 = FloorDiv(x0, TileSu);
        var c1 = FloorDiv(x0 + HeroW - 1, TileSu);
        if (dy > 0)
        {
            var feet = y0 + HeroH;
            for (var r = FloorDiv(feet - 1, TileSu) + 1; r <= FloorDiv(limit + HeroH - 1, TileSu); r++)
            {
                var hit = false;
                for (var c = c0; c <= c1; c++)
                    if (SolidFor(Level.Tile(c, r), i)) { hit = true; break; }
                if (hit) { limit = Math.Min(limit, r * TileSu - HeroH); break; }
            }
            for (var d = 0; d < _nd; d++)
            {
                var dh = _doorH[d] - DoorO[d];
                if (dh > 0 && _doorX[d] < x0 + HeroW && _doorX[d] + TileSu > x0 && _doorY[d] >= feet && _doorY[d] < limit + HeroH)
                    limit = _doorY[d] - HeroH;
            }
            for (var f = 0; f < _nf; f++)
                if (LiftX[f] < x0 + HeroW && LiftX[f] + _liftW[f] > x0 && LiftY[f] >= feet && LiftY[f] < limit + HeroH)
                    limit = LiftY[f] - HeroH;
            for (var b = 0; b < _nx; b++)
                if (BoxX[b] < x0 + HeroW && BoxX[b] + BoxSize > x0 && BoxY[b] >= feet && BoxY[b] < limit + HeroH)
                    limit = BoxY[b] - HeroH;
            if (limit != y0 + dy) Vy[i] = 0;
        }
        else
        {
            for (var r = FloorDiv(y0, TileSu) - 1; r >= FloorDiv(limit, TileSu); r--)
            {
                var hit = false;
                for (var c = c0; c <= c1; c++)
                    if (SolidFor(Level.Tile(c, r), i)) { hit = true; break; }
                if (hit) { limit = Math.Max(limit, (r + 1) * TileSu); break; }
            }
            for (var d = 0; d < _nd; d++)
            {
                var bottom = _doorY[d] + _doorH[d] - DoorO[d];
                if (bottom > _doorY[d] && _doorX[d] < x0 + HeroW && _doorX[d] + TileSu > x0 && bottom <= y0 && bottom > limit)
                    limit = bottom;
            }
            for (var f = 0; f < _nf; f++)
            {
                var bottom = LiftY[f] + LiftH;
                if (LiftX[f] < x0 + HeroW && LiftX[f] + _liftW[f] > x0 && bottom <= y0 && bottom > limit)
                    limit = bottom;
            }
            for (var b = 0; b < _nx; b++)
            {
                var bottom = BoxY[b] + BoxSize;
                if (BoxX[b] < x0 + HeroW && BoxX[b] + BoxSize > x0 && bottom <= y0 && bottom > limit)
                    limit = bottom;
            }
            if (limit != y0 + dy) { Vy[i] = 0; JumpAge[i] = -1; WantCut[i] = 0; }
        }
        Y[i] = limit;
    }

    bool HeroSupported(int i)
    {
        var x = X[i];
        var feet = Y[i] + HeroH;
        var r = FloorDiv(feet, TileSu);
        if (feet % TileSu == 0)
            for (var c = FloorDiv(x, TileSu); c <= FloorDiv(x + HeroW - 1, TileSu); c++)
                if (SolidFor(Level.Tile(c, r), i)) return true;
        for (var d = 0; d < _nd; d++)
            if (_doorY[d] == feet && _doorH[d] - DoorO[d] > 0 && _doorX[d] < x + HeroW && _doorX[d] + TileSu > x) return true;
        for (var f = 0; f < _nf; f++)
            if (LiftY[f] == feet && LiftX[f] < x + HeroW && LiftX[f] + _liftW[f] > x) return true;
        for (var b = 0; b < _nx; b++)
            if (BoxY[b] == feet && BoxX[b] < x + HeroW && BoxX[b] + BoxSize > x) return true;
        return false;
    }

    // ---------- скрині ----------

    void StepBox(int b)
    {
        BoxVy[b] = Math.Min(BoxVy[b] + Gravity, FallMax);
        var dy = BoxVy[b];
        var x0 = BoxX[b];
        var y0 = BoxY[b];
        var limit = y0 + dy;
        var c0 = FloorDiv(x0, TileSu);
        var c1 = FloorDiv(x0 + BoxSize - 1, TileSu);
        var bottom = y0 + BoxSize;
        for (var r = FloorDiv(bottom - 1, TileSu) + 1; r <= FloorDiv(limit + BoxSize - 1, TileSu); r++)
        {
            var hit = false;
            for (var c = c0; c <= c1; c++)
                if (Level.Tile(c, r) == VohnykLevel.Stone) { hit = true; break; }
            if (hit) { limit = Math.Min(limit, r * TileSu - BoxSize); break; }
        }
        for (var d = 0; d < _nd; d++)
        {
            var dh = _doorH[d] - DoorO[d];
            if (dh > 0 && _doorX[d] < x0 + BoxSize && _doorX[d] + TileSu > x0 && _doorY[d] >= bottom && _doorY[d] < limit + BoxSize)
                limit = _doorY[d] - BoxSize;
        }
        for (var f = 0; f < _nf; f++)
            if (LiftX[f] < x0 + BoxSize && LiftX[f] + _liftW[f] > x0 && LiftY[f] >= bottom && LiftY[f] < limit + BoxSize)
                limit = LiftY[f] - BoxSize;
        for (var o = 0; o < _nx; o++)
            if (o != b && BoxX[o] < x0 + BoxSize && BoxX[o] + BoxSize > x0 && BoxY[o] >= bottom && BoxY[o] < limit + BoxSize)
                limit = BoxY[o] - BoxSize;
        if (limit != y0 + dy) BoxVy[b] = 0;
        BoxY[b] = limit;
    }

    // ---------- сигнали, небезпека ----------

    void UpdateSignals()
    {
        for (var i = 0; i < _nb; i++)
        {
            var on = 0;
            for (var h = 0; h < 2 && on == 0; h++)
                if (Overlap(_btnX[i], _btnY[i], 32 * Px, 8 * Px, X[h], Y[h], HeroW, HeroH)) on = 1;
            for (var b = 0; b < _nx && on == 0; b++)
                if (Overlap(_btnX[i], _btnY[i], 32 * Px, 8 * Px, BoxX[b], BoxY[b], BoxSize, BoxSize)) on = 1;
            Button[i] = on;
        }
        // Важіль перемикається, коли герой ВИХОДИТЬ із клітинки: праворуч — 1, ліворуч — 0, угору/вниз — як був.
        for (var h = 0; h < 2; h++)
        {
            var mask = 0;
            for (var i = 0; i < _nl; i++)
            {
                var inside = Overlap(X[h], Y[h], HeroW, HeroH, _levX[i], _levY[i], TileSu, TileSu);
                if (inside) { mask |= 1 << i; continue; }
                if ((LeverIn[h] & (1 << i)) == 0) continue;
                if (X[h] >= _levX[i] + TileSu) Lever[i] = 1;
                else if (X[h] + HeroW <= _levX[i]) Lever[i] = 0;
            }
            LeverIn[h] = mask;
        }
    }

    bool FeetInDanger(int i)
    {
        var fx0 = X[i] + FeetInset;
        var fx1 = X[i] + HeroW - FeetInset;          // не включно
        var fy1 = Y[i] + HeroH;                       // не включно
        var fy0 = fy1 - FeetH;
        for (var r = FloorDiv(fy0, TileSu); r <= FloorDiv(fy1 - 1, TileSu); r++)
            for (var c = FloorDiv(fx0, TileSu); c <= FloorDiv(fx1 - 1, TileSu); c++)
            {
                var t = Level.Tile(c, r);
                if (t == VohnykLevel.Mud || (t == VohnykLevel.Water && i == 0) || (t == VohnykLevel.Lava && i == 1)) return true;
            }
        return false;
    }

    /// <summary>Чи тверда плитка для героя who: камінь і своя рідина (Вогнику — лава, Краплі — вода). who = −1 — скриня.</summary>
    static bool SolidFor(byte t, int who) =>
        t == VohnykLevel.Stone || (who == 0 && t == VohnykLevel.Lava) || (who == 1 && t == VohnykLevel.Water);

    bool TileSolidIn(int x, int y, int w, int h, int who)
    {
        for (var r = FloorDiv(y, TileSu); r <= FloorDiv(y + h - 1, TileSu); r++)
            for (var c = FloorDiv(x, TileSu); c <= FloorDiv(x + w - 1, TileSu); c++)
                if (SolidFor(Level.Tile(c, r), who)) return true;
        return false;
    }

    static bool Overlap(int ax, int ay, int aw, int ah, int bx, int by, int bw, int bh) =>
        ax < bx + bw && ax + aw > bx && ay < by + bh && ay + ah > by;

    static int Clamp(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;

    /// <summary>Ділення з округленням униз (координати бувають від'ємні лише за рамкою, але хай буде чесно).</summary>
    static int FloorDiv(int a, int b) => a >= 0 ? a / b : -((-a + b - 1) / b);
}
