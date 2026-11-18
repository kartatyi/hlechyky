use strict; use utf8; use open qw(:std :utf8);
# «Обрій», другий заплив: Тетерів у Житомирі — каньйон із лісистими берегами, підвісний пішохідний міст,
# гранітна скеля, каміння у воді. Усе далеке — в тумані (розмиті шари), чіткий лише передній план з очеретом.
my $OUT = shift || 'web/static';   # запуск із кореня репозиторію: perl tools/obrii/gen.pl
my $PI = 3.14159265358979;
sub ss { my $t = shift; $t = 0 if $t < 0; $t = 1 if $t > 1; $t * $t * (3 - 2 * $t) }
sub smooth { my @p = @_; my $d = sprintf('M%.0f %.1f', @{$p[0]});
  for my $i (1 .. $#p - 1) { $d .= sprintf(' Q%.0f %.1f %.0f %.1f', $p[$i][0], $p[$i][1], ($p[$i][0] + $p[$i+1][0]) / 2, ($p[$i][1] + $p[$i+1][1]) / 2); }
  $d . sprintf(' L%.0f %.1f', @{$p[-1]}) }
sub wave { my ($x, $w) = @_; my $y = 0; $y += $_->[0] * sin($x * 2 * $PI / $_->[1] + $_->[2]) for @$w; $y }

# ---- далекі пагорби
my @W1 = ([.6, 1150, .4], [.3, 520, 2.1], [.1, 230, 1.2]);
my @p; for (my $x = -40; $x <= 2040; $x += 40) { push @p, [$x, 322 - 16 * wave($x, \@W1)]; }
my $far = smooth(@p) . ' V450 H-40 Z';

# ---- береги каньйону: лівий спускається до води на x=900, правий піднімається від x=1110
sub yL { my $x = shift; my $t = ss(($x - 420) / 480); my $top = 252 + 9 * sin($x / 170 + 1) + 4 * sin($x / 61 + .3);
  my $crown = 3.6 * sin($x / 23 + .6) + 2.4 * sin($x / 9.5 + 2) + 1.3 * sin($x / 5.1);
  $top + (358 - $top) * $t + $crown * (1 - .85 * $t) }
sub yR { my $x = shift; my $t = ss((1600 - $x) / 490); my $top = 264 + 8 * sin($x / 150 + 2.2) + 4 * sin($x / 57 + 1.1);
  my $crown = 3.4 * sin($x / 21 + 1.9) + 2.6 * sin($x / 10.3 + .4) + 1.2 * sin($x / 4.7);
  $top + (358 - $top) * $t + $crown * (1 - .85 * $t) }
@p = (); for (my $x = -40; $x <= 900; $x += 10) { push @p, [$x, yL($x)]; }
my $bankL = smooth(@p) . ' V450 H-40 Z';
@p = (); for (my $x = 1110; $x <= 2040; $x += 10) { push @p, [$x, yR($x)]; }
my $bankR = smooth(@p) . ' V450 H1110 Z';
my $reliefFar = fold(\&yL, -40, 900, 24, .4, 360) . fold(\&yL, -40, 900, 52, 2.1, 360) . fold(\&yR, 1110, 2040, 22, 1.3, 360) . fold(\&yR, 1110, 2040, 50, 3.0, 360)
  . shade('ob-cbl', -40, 230, 940, 130) . shade('ob-cbr', 1110, 230, 930, 130);
my $snowFar = snow(\&yL, -40, 880, 3, 8, .3) . snow(\&yR, 1130, 2040, 3, 8, 1.7);
my $rock = q{M1284 358 L1290 336 L1302 327 L1310 311 L1330 303 L1352 301 L1364 291 L1392 293 L1404 305 L1420 307 L1432 331 L1440 358 Z};
my $rockL = q{M1310 311 L1330 303 L1338 330 L1318 340 Z M1364 291 L1392 293 L1398 322 L1374 328 Z};

# ---- ближчі схили: сосни й крони, які вже видно поодинці, і гранітна скеля на лівому березі — «гори поруч»
sub yML { my $x = shift; my $t = ss(($x - 140) / 440); my $top = 300 + 7 * sin($x / 90 + .5); $top + (374 - $top) * $t }
sub yMR { my $x = shift; my $t = ss((1880 - $x) / 420); my $top = 296 + 7 * sin($x / 80 + 1.7); $top + (374 - $top) * $t }
sub fold { my ($f, $x0, $x1, $dy, $ph, $floor) = @_; my @q;
  for (my $x = $x0; $x <= $x1; $x += 14) { my $y = $f->($x) + $dy + 8 * sin($x / 77 + $ph); push @q, [$x, $y > $floor ? $floor : $y]; }
  '<path class="ld-fold" d="' . smooth(@q) . " V$floor H$x0 Z" . '"/>' }
# Тіньовий бік схилу: дві накладки на кожен масив (тінь ліворуч / праворуч), обрізані за його обрисом.
sub shade { my ($clip, $x, $y, $w, $h) = @_; join('', map { sprintf('<rect class="%s" clip-path="url(#%s)" x="%d" y="%d" width="%d" height="%d"/>', $_, $clip, $x, $y, $w, $h) } 'ld-sh-l', 'ld-sh-r') }
srand(11);
# ---- дерева. Щоб ліс не виглядав штучним, у ньому нема правильних фігур: крони й кущі — замкнені криві з нерівним
# радіусом (blob, dome), ялини — ламані з гілками різної довжини, сосни — вигнутий стовбур і хвоя хмарками вгорі.
# Кожне дерево стоїть на землі (стовбур іде від ґрунту в крону, крона завжди перекриває верх стовбура). Ростуть гуртами
# здебільшого однієї породи, з прогалинами й підліском; частина — на гребені, решта — нижче схилом. Листяні — трьох
# відтінків (восени жовті, руді й червоні), навесні цвітуть, узимку голі з гілками; узимку на гілках і кронах лежить сніг.
# Обриси пишемо відносними координатами (перша точка — де, решта — зсуви): малюнок той самий, а файл на третину легший.
sub r1 { sprintf('%.1f', $_[0]) + 0 }
sub pts { my @p = @_; my ($cx, $cy) = (r1($p[0][0]), r1($p[0][1])); my $d = "$cx $cy";
  for my $i (1 .. $#p) { my ($x, $y) = (r1($p[$i][0]), r1($p[$i][1])); $d .= sprintf('l%.1f %.1f', $x - $cx, $y - $cy); ($cx, $cy) = ($x, $y); }
  $d }
sub qrel { my ($start, @seg) = @_; my ($cx, $cy) = (r1($start->[0]), r1($start->[1])); my $d = "M$cx $cy";       # @seg: [керівна точка, кінець]
  for my $s (@seg) { my ($px, $py, $ex, $ey) = map { r1($_) } @{$s->[0]}, @{$s->[1]}; $d .= sprintf('q%.1f %.1f %.1f %.1f', $px - $cx, $py - $cy, $ex - $cx, $ey - $cy); ($cx, $cy) = ($ex, $ey); }
  $d }
sub half { my ($cl, @p) = @_; '<path class="' . $cl . '" d="M' . pts(@p) . 'Z"/>' }
sub blobpts { my ($cx, $cy, $rx, $ry, $j, $n, $a0, $a1) = @_; $a0 //= 0; $a1 //= 6.2832; my $full = $a1 - $a0 > 6.28;
  my @k = map { [1 + int(rand(3)), rand(6.2832), .4 + rand(.6)] } 1 .. 3; my @p;
  for my $i (0 .. $n - 1) { my $a = $a0 + ($a1 - $a0) * $i / ($full ? $n : $n - 1); my $r = 1 + (rand() - .5) * $j * .5; $r += $j * $_->[2] * sin($a * $_->[0] + $_->[1]) / 2 for @k;
    push @p, [$cx + $rx * $r * cos($a), $cy + $ry * $r * sin($a)]; }
  @p }
sub blob { my ($cx, $cy, $rx, $ry, $j) = @_; my $n = 7 + int(rand(4)); my @p = blobpts($cx, $cy, $rx, $ry, $j // .3, $n);
  qrel([($p[-1][0] + $p[0][0]) / 2, ($p[-1][1] + $p[0][1]) / 2], map { my $q = $p[($_ + 1) % $n]; [$p[$_], [($p[$_][0] + $q->[0]) / 2, ($p[$_][1] + $q->[1]) / 2]] } 0 .. $n - 1) . 'Z' }
sub dome { my ($cx, $y, $rx, $ry, $j) = @_; my $n = 7 + int(rand(4)); my @p = blobpts($cx, $y, $rx, $ry, $j // .3, $n, 3.1416, 6.2832);
  $p[0] = [$cx - $rx, $y]; $p[-1] = [$cx + $rx, $y];
  qrel($p[0], map { [$p[$_], [($p[$_][0] + $p[$_ + 1][0]) / 2, ($p[$_][1] + $p[$_ + 1][1]) / 2]] } 1 .. $n - 2) . sprintf('L%.1f %.1fZ', @{$p[-1]}) }
# ялина: гілки ярусами різної довжини, кінчики трохи звисають, унизу видно стовбур; сніг — латками на гілках
sub spruce { my ($x, $y, $h, $w, $cl) = @_; my $n = 4 + int($h / 11) + int(rand(2)); $n = 9 if $n > 9;
  my $tr = $h * (.05 + rand(.07)); my $tw = .6 + $w * .04; my @side;
  for my $sg (1, -1) { my @a; my $yy = $y - $h;
    for my $i (1 .. $n) { $yy += ($h - $tr) / $n * (.7 + rand(.6)); $yy = $y - $tr if $i == $n || $yy > $y - $tr;
      my $wd = $w / 2 * ($i / $n) ** .9 * (.68 + rand(.6));
      push @a, [$x + $sg * $wd, $yy + rand(2)];
      push @a, [$x + $sg * $wd * (.28 + rand(.25)), $yy - $h * .015 - rand(1.2)] if $i < $n; }
    push @side, \@a; }
  my ($R, $L) = @side; my @top = ([$x, $y - $h]); my $c = $cl ? qq{ class="$cl"} : '';
  my $o = "<path$c d=\"M" . pts(@top, @$R, [$x + $tw, $y - $tr], [$x + $tw, $y], [$x - $tw, $y], [$x - $tw, $y - $tr], reverse(@$L)) . 'Z"/>';
  $o .= half('ld-ts-r', @top, @$R, [$x, $y - $tr]) . half('ld-ts-l', @top, @$L, [$x, $y - $tr]) unless $cl;
  my $sn = sprintf('M%.1f %.1fv%.1f', $x, $y - $h - .4, $h * .07);
  for my $a ($R, $L) { for (my $i = 0; $i + 1 < @$a; $i += 2) { next if rand() < .35; my ($tip, $in) = ($a->[$i], $i ? $a->[$i - 1] : [$x, $y - $h]);
    $sn .= sprintf('M%.1f %.1fl%.1f %.1f', $in->[0], $in->[1] - .5, ($tip->[0] - $in->[0]) * .92, ($tip->[1] - $in->[1]) * .92); } }
  $o . qq{<path class="ld-snow-s" d="$sn"/>} }
# сосна: ледь вигнутий стовбур на всю висоту; хвоя — кількома пласкими нерівними ярусами, що чергуються вліво-вправо
# (між ними видно стовбур і небо), а не шапкою — інакше дерево схоже на гриб; сніг — латками на ярусах
sub sosna { my ($x, $y, $h, $w, $cl) = @_; my $c = $cl ? qq{ class="$cl"} : ''; my $tw = .7 + $w * .045; my $bend = (rand() - .5) * $w * .3;
  my ($tx, $ty) = ($x + $bend, $y - $h * .92);
  my $o = sprintf('<path%s d="M%.1f %.1fQ%.1f %.1f %.1f %.1fL%.1f %.1fQ%.1f %.1f %.1f %.1fZ"/>', $c, $x - $tw, $y, $x - $tw * .7 + $bend * .3, $y - $h * .45, $tx - $tw * .3, $ty,
    $tx + $tw * .3, $ty, $x + $tw * .7 + $bend * .3, $y - $h * .45, $x + $tw, $y);
  my $nt = 3 + int(rand(3)); my ($d, $sn) = ('', ''); my $sg = rand() < .5 ? -1 : 1;
  for my $i (0 .. $nt - 1) { my $f = $i / ($nt - 1);                      # 0 — нижній ярус, 1 — верхівка
    my $cy = $y - $h * (.56 + .38 * $f) + (rand() - .5) * $h * .03; my $rx = $w * (.5 - .24 * $f) * (.75 + rand(.5)); my $ry = $h * (.045 + rand(.035));
    my $cx = $x + $bend * (.45 + .55 * $f) + $sg * $rx * (.38 - .3 * $f) * (.6 + rand(.8)); $sg = -$sg;
    $d .= blob($cx, $cy, $rx, $ry, .42);
    $sn .= sprintf('M%.1f %.1fq%.1f %.1f %.1f 0', $cx - $rx * .7, $cy - $ry * .55, $rx * .7, -$ry * .5, $rx * 1.4) if rand() < .8; }      # сніг — дужкою по верху яруса
  $d .= blob($tx, $ty, $w * .15, $h * .05, .3);
  $o . qq{<path$c d="$d"/><path class="ld-snow-s ld-snow-w" d="$sn"/>} }
# листяне: стовбур до розгалуження, гілки з пагонами (їх видно, коли листя облетіло), крона — один нерівний обрис
sub leafy { my ($x, $y, $h, $w, $birch) = @_; my $v = $birch ? ' ld-c2' : ('', ' ld-c2', ' ld-c3')[int(rand(3))]; my $tw = .7 + $w * .04; my $th = $h * ($birch ? .5 + rand(.1) : .3 + rand(.12));
  my $small = $h < 27;                                                   # у дрібних дерев пагонів і зайвих завитків крони не видно — не малюємо (файл легший)
  my $tc = $birch ? 'ld-trunk ld-birch' : 'ld-trunk';
  my $o = sprintf('<path class="' . $tc . '" d="M%.1f %.1fL%.1f %.1fL%.1f %.1fL%.1f %.1fZ"/>', $x - $tw, $y, $x - $tw * .5, $y - $th, $x + $tw * .5, $y - $th, $x + $tw, $y);
  my ($tw_d, $sn_d) = ('', '');
  for (1 .. ($small ? 2 : 3 + int(rand(3)))) { my $a = -1.5708 + (rand() - .5) * 1.9; my $len = ($h - $th) * (.5 + rand(.4));
    my ($ex, $ey) = ($x + cos($a) * $len * ($w / ($h - $th)) * .9, $y - $th + sin($a) * $len);
    my ($mx, $my) = ($x + ($ex - $x) * .5 + (rand() - .5) * 2, $y - $th + ($ey - $y + $th) * .55);
    $tw_d .= sprintf('M%.1f %.1fQ%.1f %.1f %.1f %.1f', $x, $y - $th + 1, $mx, $my + 1.5, $ex, $ey);
    $sn_d .= sprintf('M%.1f %.1fL%.1f %.1f', $mx, $my - .2, $ex, $ey - .6);
    for (1 .. ($small ? 0 : 1 + int(rand(2)))) { my $f = .4 + rand(.4); my ($sx, $sy) = ($x + ($ex - $x) * $f, $y - $th + ($ey - $y + $th) * $f);
      $tw_d .= sprintf('M%.1f %.1fL%.1f %.1f', $sx, $sy, $sx + (rand() - .5) * $w * .35, $sy - $h * (.08 + rand(.1))); } }
  $o .= qq{<path class="ld-twig" d="$tw_d"/><path class="ld-snow-s" d="$sn_d"/>};
  my $cy = $y - $th - ($h - $th) * .46; my ($rx, $ry) = ($w * .5, ($h - $th) * .58);
  my $d = blob($x, $cy, $rx, $ry, .24);
  for (1 .. ($small ? int(rand(2)) : 1 + int(rand(2)))) { $d .= blob($x + (rand() - .5) * $rx * 1.1, $cy + (rand() - .5) * $ry * .9, $rx * (.45 + rand(.3)), $ry * (.4 + rand(.3)), .34); }
  $o .= qq{<path class="ld-crown$v" d="$d"/>};
  $o . '<path class="ld-bloom" d="' . join('', map { sprintf('M%.1f %.1fh0', $x + (rand() - .5) * $rx * 1.4, $cy + (rand() - .5) * $ry * 1.4) } 1 .. ($small ? 2 : 4) + int(rand($small ? 3 : 5))) . '"/>' }
sub bush { my ($x, $y, $hh, $w) = @_; my $v = ('', ' ld-c2', ' ld-c3')[int(rand(3))]; qq{<path class="ld-crown$v" d="} . dome($x, $y + 1, $w / 2, $hh, .4) . '"/>' }
# $kind — порода гурту (0..1): сім дерев із десяти в гурті — саме вона
sub tree { my ($x, $y, $s, $cl, $kind) = @_; my $r = defined $kind && rand() < .7 ? $kind : rand(); my $t;
  if ($r < .4) { my $h = (30 + rand(28)) * $s; $t = spruce($x, $y, $h, $h * (.3 + rand(.16)), $cl); }
  elsif ($r < .62) { my $h = (34 + rand(24)) * $s; $t = sosna($x, $y, $h, $h * (.38 + rand(.22)), $cl); }
  elsif ($r < .93) { my $b = rand() < .35; my $h = (24 + rand(20)) * $s * ($b ? 1.25 : 1); $t = leafy($x, $y, $h, $h * ($b ? .3 + rand(.14) : .45 + rand(.65)), $b); }
  else { $t = bush($x, $y, (7 + rand(6)) * $s, (16 + rand(12)) * $s); }
  sprintf('<g transform="rotate(%.1f %.1f %.1f)">%s</g>', rand(6) - 3, $x, $y, $t) }
sub trees { my ($f, $x0, $x1, $tf, $skip, $floor) = @_; my @all; my $x = $x0;
  my $ok = sub { my $t = $tf->($_[0]); !($t > .88 || ($skip && $_[0] > $skip->[0] && $_[0] < $skip->[1])) };
  while ($x < $x1) { my $n = 3 + int(rand(rand(16))); my $cs = .7 + rand(.6); my $kind = rand();
    for (1 .. $n) { last if $x >= $x1; my $k = 1 - .45 * $tf->($x);
      if ($ok->($x)) { my $d = rand() < .38 ? rand(5) : 5 + rand() ** 1.25 * 52;         # частина — на гребені, решта — нижче схилом, аж до води
        $d = rand(4) if $f->($x) + 3 + $d > $floor - 4;
        my $y = $f->($x) + 3 + $d; push @all, [$y, tree($x, $y, $k * $cs * (.75 + rand(.5)) * (1 + $d * .012), 'ld-pine', $kind)]; }
      $x += (1.8 + rand(5.4)) * $k; }
    if (rand() < .55 && $ok->($x)) { my $k = 1 - .45 * $tf->($x); push @all, [$f->($x) + 6, bush($x + rand(8) - 4, $f->($x) + 4, (6 + rand(5)) * $k, (14 + rand(12)) * $k)]; }   # підлісок на узліссі
    $x += 2 + rand() ** 2.4 * 22; }
  join('', map { $_->[1] } sort { $a->[0] <=> $b->[0] } @all) }
# сніг: смуга вздовж гребеня, нерівної товщини (видно лише серед зими)
sub snow { my ($f, $x0, $x1, $t0, $t1, $ph) = @_; my (@a, @b);
  for (my $x = $x0; $x <= $x1; $x += 12) { my $y = $f->($x); push @a, [$x, $y - .6]; unshift @b, [$x, $y + $t0 + ($t1 - $t0) * (.5 + .5 * sin($x / 37 + $ph))]; }
  '<path class="ld-snow" d="M' . join(' L', map { sprintf('%.0f %.1f', @$_) } @a, @b) . ' Z"/>' }
@p = (); for (my $x = -40; $x <= 580; $x += 12) { push @p, [$x, yML($x)]; }
my $midL = smooth(@p) . ' V380 H-40 Z';
@p = (); for (my $x = 1460; $x <= 2040; $x += 12) { push @p, [$x, yMR($x)]; }
my $midR = smooth(@p) . ' V380 H1460 Z';
my $midT = trees(\&yML, -30, 560, sub { ss(($_[0] - 140) / 440) }, [296, 486], 376) . trees(\&yMR, 1480, 2030, sub { ss((1880 - $_[0]) / 420) }, undef, 376);
my $reliefMid = fold(\&yML, -40, 580, 20, .9, 380) . fold(\&yMR, 1460, 2040, 20, 2.4, 380) . shade('ob-cml', -40, 280, 620, 100) . shade('ob-cmr', 1460, 280, 580, 100);
my $snowMid = snow(\&yML, -40, 478, 3, 9, 2.2) . snow(\&yMR, 1480, 2040, 3, 9, .9);   # праворуч від скелі сніг лежить на горбку під вербою ($knollS)
my $rock2 = 'M290 380 L298 352 L316 341 L322 318 L348 305 L370 309 L392 296 L420 300 L434 319 L460 326 L470 350 L486 380 Z';
my $rock2L = 'M322 318 L348 305 L358 336 L332 347 Z M392 296 L420 300 L425 331 L400 336 Z M434 319 L460 326 L462 352 L444 356 Z';
# дерева на скелі стоять на її обрисі (rockY) і намальовані перед нею — скеля прикриває їхній низ
my @RK = ([298, 352], [316, 341], [322, 318], [348, 305], [370, 309], [392, 296], [420, 300], [434, 319], [460, 326], [470, 350]);
sub rockY { my $x = shift; for my $i (0 .. $#RK - 1) { my ($a, $b) = ($RK[$i], $RK[$i + 1]); return $a->[1] + ($b->[1] - $a->[1]) * ($x - $a->[0]) / ($b->[0] - $a->[0]) if $x >= $a->[0] && $x <= $b->[0]; } 352 }
my $rock2T = join('', map { tree($_->[0], rockY($_->[0]) + 2, $_->[1], 'ld-pine', $_->[2]) } [324, .5, .5], [331, .7, .1], [343, .6, .75], [352, .85, .5], [366, .65, .1], [381, .95, .1], [395, .55, .75], [404, .7, .5], [416, .6, .1], [428, .6, .95], [440, .5, .5], [449, .7, .1], [461, .5, .75]);
# ---- далекий ліс: далекі береги вкриті лісом суцільно. Ліс — яруси крон один за одним, від гребеня до води: у кожного
# ярусу верхівки світліші, під ними темно, низ тане — тож наступний ярус читається на тлі попереднього, і схил виглядає
# лісом, а не лугом із поодинокими деревами. Листяні — суцільною смугою крон (у барвах пори року, взимку прозорішають),
# ялини — окремими шпилями, гуртами (темні цілий рік). Ярус порізано на шматки; у кожного своя система координат,
# покладена на схил (нахил і масштаб — у transform), тому градієнт один на всіх і завжди йде від верхівок до землі.
sub yLb { my $x = shift; my $t = ss(($x - 420) / 480); my $top = 252 + 9 * sin($x / 170 + 1) + 4 * sin($x / 61 + .3); $top + (358 - $top) * $t }
sub yRb { my $x = shift; my $t = ss((1600 - $x) / 490); my $top = 264 + 8 * sin($x / 150 + 2.2) + 4 * sin($x / 57 + 1.1); $top + (358 - $top) * $t }
sub n3 { my $v = sprintf('%.3f', $_[0]); $v =~ s/0+$//; $v =~ s/\.$//; $v =~ s/^(-?)0\./$1./; $v eq '' || $v eq '-' ? 0 : $v }
sub frow { my ($side, $r, $dy, $hh) = @_;
  my ($f, $fb, $tf, $x0, $x1, $occ) = $side
    ? (\&yR, \&yRb, sub { ss((1600 - $_[0]) / 490) }, 1130, 2040, sub { $_[0] > 1460 ? yMR($_[0]) : 999 })
    : (\&yL, \&yLb, sub { ss(($_[0] - 420) / 480) }, -40, 880, sub { $_[0] < 580 ? yML($_[0]) : 999 });
  my @ph = map { rand(6.2832) } 1 .. 5;
  my $kf = sub { 1 - .45 * $tf->($_[0]) };
  my $base = sub { my $x = shift; $r ? $fb->($x) + $dy * $kf->($x) * (1 + .26 * sin($x / 83 + $ph[0])) + 2.6 * sin($x / 37 + $ph[1]) : $f->($x) + 2 };   # яруси не повторюють гребінь слід у слід
  my $grove = sub { .5 + .5 * sin($_[0] / 67 + $ph[2]) * cos($_[0] / 29 + $ph[3]) };       # де дерева вищі, де нижчі
  my $stand = sub { sin($_[0] / 47 + $ph[4]) + .6 * sin($_[0] / 17 + $ph[2]) };             # де ялинник
  my ($o, @dom, @spi) = ('');
  my $flush = sub { my $n = @dom; return unless $n;
    my @P = ([$dom[0][0], undef]);                                                         # улоговини між кронами; undef — край лісу, крона спускається до землі
    for my $i (1 .. $n - 1) { my $h = $dom[$i - 1][2] < $dom[$i][2] ? $dom[$i - 1][2] : $dom[$i][2]; push @P, [$dom[$i][0], $base->($dom[$i][0]) - $h * (.42 + rand(.25))]; }
    push @P, [$dom[-1][1], undef];
    my $i0 = 0;
    while ($i0 < $n) { my $i1 = $i0; $i1++ while $i1 < $n - 1 && $dom[$i1][1] - $dom[$i0][0] < 74;
      $i1 = $n - 1 if $n - 1 - $i1 < 2;                                                     # хвостик у дві крони — у той самий шматок
      my ($xa, $xb) = ($P[$i0][0], $P[$i1 + 1][0]); my $ba = $base->($xa); my $m = ($base->($xb) - $ba) / ($xb - $xa); my $s = $kf->(($xa + $xb) / 2) * $hh / 18;
      my $loc = sub { [($_[0] - $xa) / $s, defined $_[1] ? ($_[1] - $ba - $m * ($_[0] - $xa)) / $s : 12] };
      my $cur = $loc->(@{$P[$i0]}); my ($cx, $cy) = (r1($cur->[0]), r1($cur->[1])); my $d = "M$cx $cy";
      for my $j ($i0 .. $i1) { my $e = $loc->(@{$P[$j + 1]}); my $xm = ($dom[$j][0] + $dom[$j][1]) / 2; my $pk = $loc->($xm, $base->($xm) - $dom[$j][2]);
        my ($qx, $qy) = (r1($pk->[0] + (rand() - .5) * ($e->[0] - $cur->[0]) * .5), r1(2 * $pk->[1] - ($cur->[1] + $e->[1]) / 2)); my ($ex, $ey) = (r1($e->[0]), r1($e->[1]));
        $d .= sprintf('q%.1f %.1f %.1f %.1f', $qx - $cx, $qy - $cy, $ex - $cx, $ey - $cy); ($cx, $cy, $cur) = ($ex, $ey, $e); }
      $d .= ($i1 < $n - 1 ? sprintf('h%.1fV12', .8 / $s) : '') . 'H0Z';                     # сусідні шматки трохи заходять один на одного — без шпарин
      my $sp = '';
      for my $q (grep { $_->[0] >= $xa && $_->[0] < $xb } @spi) { my ($X, $Y) = @{$loc->($q->[0], $base->($q->[0]))}; my ($w, $h) = ($q->[1] / $s, $q->[2] / $s);
        $sp .= 'M' . ($r ? pts([$X - $w / 2, $Y + 3], [$X + (rand() - .5) * $w * .2, $Y - $h], [$X + $w / 2, $Y + 3])                 # нижчі яруси — простий шпиль: зблизька їх не видно
          : pts([$X - $w / 2, $Y + 3], [$X - $w * .16, $Y - $h * .5], [$X + (rand() - .5) * $w * .2, $Y - $h], [$X + $w * .16, $Y - $h * .5], [$X + $w / 2, $Y + 3])) . 'z'; }
      $o .= sprintf('<g transform="matrix(%s %s 0 %s %s %s)"><path d="%s"/>%s</g>', n3($s), n3($m * $s), n3($s), r1($xa), r1($ba), $d, $sp ne '' ? qq{<path class="ld-fe" d="$sp"/>} : '');
      $i0 = $i1 + 1; }
    @dom = @spi = (); };
  my $x = $x0;
  while ($x < $x1) { my $s = $kf->($x) * $hh / 18; my $w = (9 + rand(8)) * $s; my $xm = $x + $w / 2; my $b = $base->($xm);
    if ($b < 355 && $b - .6 * $hh * $kf->($xm) < $occ->($xm)) { my $st = $stand->($xm) > .55;
      push @dom, [$x, $x + $w, 18 * $s * (.55 + rand(.4)) * (.7 + .5 * $grove->($xm)) * ($st ? .75 : 1)];
      if ($st) { for (my $sx = $x + rand($w * .3); $sx < $x + $w; $sx += (4.4 + rand(5.5)) * $s) { push @spi, [$sx, (5.5 + rand(3.5)) * $s, 18 * $s * (.9 + rand(.5)) * (.75 + .4 * $grove->($sx))]; } }
      elsif (rand() < .1) { push @spi, [$x + rand($w), (5.5 + rand(3.5)) * $s, 18 * $s * (.9 + rand(.4))]; }
      $x += $w; }
    else { $flush->(); $x += $w * .6; } }
  $flush->(); $o }
srand(57);
my $farT = '';
for my $row ([2, 17, .95], [10, 17.5, .9], [19, 18, .85], [29, 18.5, .8], [40, 19, .74], [52, 19.5, .68], [65, 20, .62], [79, 20, .56]) { my ($dy, $hh, $op) = @$row; my $r = $dy > 2;
  $farT .= qq{<g opacity="$op">} . frow(0, $r, $dy, $hh) . frow(1, $r, $dy, $hh) . '</g>'; }
# ---- верба над водою (одна) — під скелею, на горбку біля самої води. Товстий стовбур хилиться до річки й розходиться
# на три гілки; крона — не шапка, а каскади: кожна гілка вигинається догори й скидає донизу завісу довгих пасом різної
# довжини, з просвітами між ними. Задні каскади темніші, передні світліші — видно, що крона об'ємна. З боку води пасма
# сягають води, посередині коротші — під ними видно стовбур. Узимку лишаються стовбур, гілки й тонкі батоги.
sub curtain { my ($ax, $ay, $wc, $n, $flare, $bot) = @_; my $hc = $wc * .3; my ($lx, $rx, $y0) = ($ax - $wc / 2, $ax + $wc / 2, $ay + $hc);
  my $d = sprintf('M%.1f %.1fQ%.1f %.1f %.1f %.1f', $lx, $y0, $ax + (rand() - .5) * $wc * .3, $ay - $hc * .9, $rx, $y0); my ($px, $py) = ($rx, $y0); my (@tips, $ty);
  for my $i (0 .. $n - 1) { my $tx = $rx + $flare - ($wc + 2 * $flare) * ($i + .5) / $n + (rand() - .5) * $wc / $n * .5; $ty = $bot->($tx); push @tips, [$tx, $ty];
    $d .= sprintf('Q%.1f %.1f %.1f %.1f', $px + ($tx - $px) * .15 + 1.4, $py + ($ty - $py) * .72, $tx, $ty);                        # зовнішній бік пасма — опуклий
    if ($i < $n - 1) { my ($nx, $ny) = ($rx + $flare * .5 - ($wc + $flare) * ($i + 1) / $n, $y0 + ($ty - $y0) * (.28 + rand(.34)));  # просвіт між пасмами
      $d .= sprintf('Q%.1f %.1f %.1f %.1f', $tx - 1.7, $ty - ($ty - $ny) * .42, $nx, $ny); ($px, $py) = ($nx, $ny); } }
  $d .= sprintf('Q%.1f %.1f %.1f %.1fZ', $lx - $flare * .8 - 1.2, $y0 + ($ty - $y0) * .42, $lx, $y0);
  ($d, @tips) }
sub willow { my ($x, $y, $h, $w, $dir) = @_; my ($fx, $fy) = ($x + $dir * $w * .07, $y - $h * .43);                 # розвилка
  my @limb = ([$fx - $w * .2, $fy - $h * .3], [$fx + $dir * $w * .05, $fy - $h * .38], [$fx + $w * .23, $fy - $h * .29]);
  my $o = '<path class="ld-trunk" d="M' . pts([$x - 4.6, $y + 1], [$x - 2.9, $y - $h * .05], [$x - 2.1 + $dir * $w * .02, $y - $h * .22], [$fx - 2, $fy], [$limb[0][0] - .5, $limb[0][1]], [$limb[0][0] + .5, $limb[0][1]],
    [$fx - .7, $fy - $h * .07], [$limb[1][0] - .4, $limb[1][1]], [$limb[1][0] + .4, $limb[1][1]], [$fx + .9, $fy - $h * .07], [$limb[2][0] - .5, $limb[2][1]], [$limb[2][0] + .5, $limb[2][1]],
    [$fx + 2.1, $fy], [$x + 2.5 + $dir * $w * .035, $y - $h * .22], [$x + 3.2, $y - $h * .05], [$x + 5, $y + 1]) . 'Z"/>';
  my $cx = $x + $dir * $w * .1;
  my $bot = sub { my $u = ($_[0] - $cx) / ($w / 2) * $dir;                                                           # докуди сягає пасмо: +1 — бік води
    $y - $h * (abs($u - .05) < .27 ? .27 + rand(.2) : $u > 0 ? -.03 + rand(.2) * (1.5 - $u) : .1 + rand(.24)) };
  my ($back, $front, $tw) = (blob($cx + $dir * $w * .02, $y - $h * .72, $w * .3, $h * .15, .3), '', '');
  # каскад: $u — де він у кроні (−1 — від води, +1 — над водою), $up — наскільки нижче за верхівку починається
  my $cas = sub { my ($u, $up, $wk) = @_; my $ax = $cx + $dir * $u * $w * .38; my $ay = $y - $h * (1 - $up) + (1 - cos($u * 1.5708)) * $h * ($u > 0 ? .3 : .4);
    my ($d, @tips) = curtain($ax, $ay, $w * $wk * (.9 + rand(.2)), 3 + int(rand(3)), 1 + rand(2.4), $bot);
    my $l = $limb[$u * $dir < -.33 ? 0 : $u * $dir > .33 ? 2 : 1];
    for my $t (@tips[grep { $_ % 2 } 0 .. $#tips]) { $tw .= sprintf('M%.1f %.1fQ%.1f %.1f %.1f %.1f', $l->[0], $l->[1], $ax + ($t->[0] - $ax) * .8, $ay - $h * .1, $t->[0], $t->[1]); }
    $d };
  $back .= $cas->(@$_) for [-.9, .03, .22], [-.5, -.02, .27], [-.08, .07, .25], [.3, -.05, .28], [.68, .04, .26], [.98, .02, .2];
  $front .= $cas->(@$_) for [-.72, .16, .2], [-.22, .22, .19], [.5, .14, .22], [.86, .2, .17];
  $o . qq{<path class="ld-twig" d="$tw"/><path class="ld-crown ld-c2" d="$back"/><path class="ld-crown ld-wl" d="$front"/>} }
srand(83);
my $willows = willow(508, 365, 58, 84, 1);
# горбок під вербою: берег тут трохи вищий — дерево стоїть на землі, а не на воді
my $knoll = 'M474 380L482 372Q495 363 510 362.6Q528 363 546 369Q562 374.5 580 380Z';
my $knollS = '<path class="ld-snow" d="M482 372Q495 363 510 362.6Q528 363 546 369Q562 374.5 580 380L566 379.4Q546 373.5 528 369.6Q506 366.6 482 372Z"/>';
# ---- дерева на ближніх берегах — великі, майже чіткі. Їхній розклад не має залежати від того, що намальовано вище
# (далекий ліс, верба), тож відлік випадкових чисел — той самий, з якого їх колись уперше розставлено.
srand(57); rand() for 1 .. 8094;
my $nearT = '';
for my $side (0, 1) { my $x = $side ? 1772 + rand(20) : 12 + rand(16); my $end = $side ? 1996 : 238;
  while ($x < $end) {
    for (1 .. 1 + int(rand(4))) { last if $x >= $end; my $s = 1.5 + rand(1.6); my $y = ($side ? y3R($x) : y3L($x)) + 4;
      $nearT .= sprintf('<g transform="rotate(%.1f %.1f %.1f)">%s</g>', rand(5) - 2.5, $x, $y, rand() < .8 ? spruce($x, $y, 38 * $s, 38 * $s * (.3 + rand(.14))) : sosna($x, $y, 40 * $s, 40 * $s * (.4 + rand(.2))));
      $x += 8 + rand(15); }
    $x += 7 + rand(20); } }

# ---- підвісний міст: два пілони-рами, несучий трос, підвіски, тонкий настил
my $br = '';
for my $x (740, 1260) {
  $br .= sprintf('<rect x="%.1f" y="226" width="2.2" height="122"/><rect x="%.1f" y="226" width="2.2" height="122"/>', $x - 4.3, $x + 2.1);
  $br .= sprintf('<rect x="%.1f" y="224" width="12" height="3"/><rect x="%.1f" y="249" width="8.6" height="1.6"/><rect x="%.1f" y="266" width="8.6" height="1.6"/>', $x - 6, $x - 4.3, $x - 4.3);
  $br .= sprintf('<path class="ld-br-w" d="M%.1f 228 L%.1f 249 M%.1f 228 L%.1f 249"/>', $x - 3.2, $x + 3.2, $x + 3.2, $x - 3.2);
}
$br .= '<rect x="520" y="279" width="960" height="3.4"/>';
my $brS = $br;   # для відбиття у воді — лише пілони й настил, без тросів
my $h = '';
for (my $x = 760; $x <= 1240; $x += 20) { my $t = ($x - 740) / 520; $h .= sprintf('M%d %.1f V279', $x, 227 + 180 * $t * (1 - $t)); }
for (my $x = 560; $x <= 720; $x += 20) { $h .= sprintf('M%d %.1f V279', $x, 281 - ($x - 540) / 200 * 54); }
for (my $x = 1280; $x <= 1440; $x += 20) { $h .= sprintf('M%d %.1f V279', $x, 227 + ($x - 1260) / 200 * 54); }
my $lamp = q{}; for (my $x = 580; $x <= 1420; $x += 70) { $lamp .= sprintf(q{<circle class="ld-lamp-g" cx="%d" cy="275" r="5"/><circle cx="%d" cy="275.4" r="1.3"/>}, $x, $x); }
$br .= '<path class="ld-br-c" d="M540 281 L740 227 Q1000 317 1260 227 L1460 281"/><path class="ld-br-w" d="' . $h . ' M520 274.6 H1480"/>';

# ---- вода: відблиски, каміння, латаття
# ---- вода: брижі — короткі хвильки різної довжини (дрібні біля обрію, довші ближче до нас), темні хвильки для
# контрасту; сонячна доріжка — розсип нерівних блискіток, густіший уздовж осі й біля обрію (sky.js ставить її під
# світило); блиск уздовж обрію; кола від крапель (лише в дощ); опале листя (лише восени)
srand(23);
sub ripples { my ($n, $y0, $y1, $lmin, $lmax) = @_; my $d = '';
  for (1 .. $n) { my $u = rand() ** 1.6; my $len = ($lmin + rand($lmax - $lmin)) * (.35 + $u * 1.1);
    $d .= sprintf('M%.0f %.1fq%.1f %.1f %.1f 0', -40 + rand(2080), $y0 + ($y1 - $y0) * $u, $len / 2, (rand() < .5 ? -1 : 1) * (.4 + rand(1.3)) * (.5 + $u), $len); }
  $d }
my $rip1 = ripples(170, 358, 470, 6, 42);
my $rip2 = ripples(100, 360, 470, 8, 54);
my $glit = ''; for (1 .. 130) { my $u = rand() ** 1.5; my $gx = (rand() + rand() + rand() - 1.5) / 1.5 * (5 + $u * 80); my $gy = 358 + 96 * $u;
  $glit .= sprintf('<ellipse class="ld-g%d" cx="%.1f" cy="%.1f" rx="%.1f" ry="%.1f" transform="rotate(%.1f %.1f %.1f)"/>', 1 + int(rand(3)), $gx, $gy,
    (2.5 + rand(8)) * (.4 + $u * 1.9) / 2, (.5 + rand(.9)) * (.5 + $u * 1.3) / 2, rand(8) - 4, $gx, $gy); }
my $splash = ''; for (1 .. 46) { my $u = rand(); $splash .= sprintf('<ellipse cx="%.0f" cy="%.1f" rx="%.1f" ry="%.1f"/>', rand(2000), 362 + 100 * $u, 2 + $u * 5 + rand(2), .7 + $u * 1.4); }
my $leaves = ''; for (1 .. 60) { my $u = rand(); my $lx = rand() < .5 ? 300 + rand(520) : 1180 + rand(520);
  $leaves .= sprintf('<ellipse class="ld-lf%d" cx="%.0f" cy="%.1f" rx="%.1f" ry="%.1f" transform="rotate(%.0f %.0f %.1f)"/>', 1 + int(rand(2)), $lx, 364 + 84 * $u, 1.4 + $u * 2, .6 + $u * .8, rand(60) - 30, $lx, 364 + 84 * $u); }
my $st = '';
for my $q ([636, 374, 27, 9], [692, 381, 13, 5], [588, 384, 9, 3.4], [1334, 372, 31, 10], [1396, 380, 15, 5.5], [1092, 366, 11, 4], [912, 364, 8, 3]) {
  my ($x, $y, $rx, $ry) = @$q; $st .= sprintf('<path d="M%.0f %.1f A%.0f %.1f 0 0 1 %.0f %.1f Z"/>', $x - $rx, $y, $rx, $ry, $x + $rx, $y); }
# ---- латаття й глечики жовті. Листки — кола з вирізом, покладені на воду, з нерівним краєм; різного розміру, повороту
# й чотирьох відтінків (один восени буріє); лежать гуртами, ближчі більші; під кожним тінь, по дальньому краю — світла
# кромка. Квіти різні: пуп'янок, напіврозкритий келих, розкрита квітка; кожна на своєму стебельці, зі своїм нахилом,
# з відбитком на воді. Сяйво — не кружечок, а світла пляма на воді під квіткою, у кожної своя; що темніше надворі, то
# воно сильніше (квіти світяться, як ліхтарики), а теплої ночі довкола ще й світляки.
srand(41);
my (@PADS, $fl_soft, $fly); $fl_soft = $fly = '';
sub pad { my ($x, $y, $r) = @_; my $a0 = rand(6.2832); my $gap = .22 + rand(.3); my $n = 13; my @p;
  for my $i (0 .. $n) { my $a = $a0 + $gap / 2 + (6.2832 - $gap) * $i / $n; my $rr = $r * (1 + (rand() - .5) * .12); push @p, [$x + $rr * cos($a), $y + $rr * sin($a) * .24]; }
  my $d = sprintf('M%.1f %.1fL%.1f %.1f', $x + $r * .1 * cos($a0), $y + $r * .1 * sin($a0) * .24, @{$p[0]});
  for my $i (1 .. $n - 1) { $d .= sprintf('Q%.1f %.1f %.1f %.1f', @{$p[$i]}, ($p[$i][0] + $p[$i + 1][0]) / 2, ($p[$i][1] + $p[$i + 1][1]) / 2); }
  $d .= sprintf('L%.1f %.1fZ', @{$p[$n]});
  my $cl = ('', ' ld-p2', ' ld-p3', ' ld-p4')[int(rand(4))];
  my $rim = sprintf('M%.1f %.1fA%.1f %.1f 0 0 1 %.1f %.1f', $x - $r * .92, $y - $r * .09, $r, $r * .24, $x + $r * .92, $y - $r * .09);
  sprintf('<ellipse class="ld-pad-s" cx="%.1f" cy="%.1f" rx="%.1f" ry="%.1f"/><path class="ld-pad%s" d="%s"/><path class="ld-pad-r" d="%s"/>', $x + $r * .06, $y + $r * .07 + .8, $r * 1.02, $r * .25, $cl, $d, $rim) }
sub flower { my ($x, $y, $r) = @_; my $k = rand(); my $hgt = $r * (1 + rand(1.8)); my $lean = (rand() - .5) * $r; my ($fx, $fy) = ($x + $lean, $y - $hgt);
  $fl_soft .= sprintf('<ellipse class="ld-fl-rf" cx="%.1f" cy="%.1f" rx="%.1f" ry="%.1f"/>', $x + $lean * .3, $y + $r * 1.3, $r * .75, $r * (1.2 + rand(.8)));
  $fl_soft .= sprintf('<ellipse class="ld-fl-glow" cx="%.1f" cy="%.1f" rx="%.1f" ry="%.1f"/>', $x + (rand() - .5) * $r, $y + 1, $r * (3.5 + rand(4)), $r * (1 + rand(.9)));
  my $o = sprintf('<path class="ld-fl-st" d="M%.1f %.1fQ%.1f %.1f %.1f %.1f"/>', $x, $y, $x + $lean * .2, $y - $hgt * .6, $fx, $fy + $r * .5);
  if ($k < .28) {          # пуп'янок
    $o .= sprintf('<ellipse class="ld-fl ld-bud" cx="%.1f" cy="%.1f" rx="%.1f" ry="%.1f"/>', $fx, $fy, $r * .5, $r * .66); }
  elsif ($k < .64) {       # напіврозкритий: кулястий келих, згори видно серединку
    $o .= sprintf('<ellipse class="ld-fl" cx="%.1f" cy="%.1f" rx="%.1f" ry="%.1f"/><ellipse class="ld-fl-in" cx="%.1f" cy="%.1f" rx="%.1f" ry="%.1f"/><ellipse class="ld-fl-c" cx="%.1f" cy="%.1f" rx="%.1f" ry="%.1f"/>',
      $fx, $fy, $r * .92, $r * .84, $fx, $fy - $r * .5, $r * .5, $r * .17, $fx, $fy - $r * .5, $r * .16, $r * .07); }
  else {                   # розкрита: пелюстки-чашолистки довкола широкої серединки
    for my $a (-62, -28, 28, 62) { $o .= sprintf('<ellipse class="ld-fl" cx="%.1f" cy="%.1f" rx="%.1f" ry="%.1f" transform="rotate(%d %.1f %.1f)"/>', $fx, $fy - $r * .62, $r * .46, $r * .7, $a, $fx, $fy); }
    $o .= sprintf('<ellipse class="ld-fl" cx="%.1f" cy="%.1f" rx="%.1f" ry="%.1f"/><ellipse class="ld-fl-in" cx="%.1f" cy="%.1f" rx="%.1f" ry="%.1f"/><ellipse class="ld-fl-c" cx="%.1f" cy="%.1f" rx="%.1f" ry="%.1f"/>',
      $fx, $fy + $r * .1, $r * .86, $r * .6, $fx, $fy - $r * .2, $r * .46, $r * .2, $fx, $fy - $r * .2, $r * .16, $r * .08); }
  $o }
for my $c ([440, 394, 86, 7], [1335, 394, 70, 6], [975, 398, 62, 5], [705, 404, 30, 3], [1150, 402, 26, 2]) { my ($cx, $cy, $sp, $n) = @$c; my $fn = 0; my @at;
  for (1 .. $n + int(rand(3))) { my $u = (rand() + rand()) / 2; my $x = $cx + (rand() - .5) * 2 * $sp; my $y = $cy - 9 + 18 * $u; my $r = (9 + rand(13)) * (.7 + $u * .7);
    push @PADS, [$y, pad($x, $y, $r)]; push @at, [$x, $y, $r, $u];
    if (rand() < .42) { $fn++; push @PADS, [$y + .1, flower($x + (rand() - .5) * $r * .8, $y + (rand() - .5) * 1.5, 3 + $u * 2.6 + rand(1.4))]; } }
  if (!$fn) { my ($x, $y, $r, $u) = @{$at[0]}; push @PADS, [$y + .1, flower($x, $y, 3.4 + $u * 2.6)]; }       # у кожному гурті є хоч одна квітка
  for (1 .. 5 + int(rand(4))) { $fly .= sprintf('<circle class="ld-fly" cx="%.1f" cy="%.1f" r="%.1f"/>', $cx + (rand() - .5) * 2.4 * $sp, $cy - 4 - rand(30), .6 + rand(.7)); } }
my $l = join('', map { $_->[1] } sort { $a->[0] <=> $b->[0] } @PADS);

# ---- ближні береги (з боків, вода йде між ними до нас)
sub y3L { my $x = shift; my $t = ss(($x - 250) / 380); my $top = 400 + 6 * sin($x / 120 + .8); my $bush = 3 * sin($x / 17 + 1) + 2 * sin($x / 7.3); $top + (452 - $top) * $t + $bush * (1 - $t) }
sub y3R { my $x = shift; my $t = ss((1760 - $x) / 380); my $top = 398 + 6 * sin($x / 110 + 2.4); my $bush = 3 * sin($x / 19 + .2) + 2 * sin($x / 6.9 + 1); $top + (452 - $top) * $t + $bush * (1 - $t) }
@p = (); for (my $x = -40; $x <= 640; $x += 10) { push @p, [$x, y3L($x)]; }
my $nearL = smooth(@p) . ' V480 H-40 Z';
@p = (); for (my $x = 1370; $x <= 2040; $x += 10) { push @p, [$x, y3R($x)]; }
my $nearR = smooth(@p) . ' V480 H1370 Z';

# ---- передній план і очерет (єдине чітке в кадрі)
my @W4 = ([.7, 2300, 4.2], [.3, 900, 1.0]);
sub y4 { 458 - 16 * wave($_[0], \@W4) }
@p = (); for (my $x = -40; $x <= 2040; $x += 60) { push @p, [$x, y4($x)]; }
my $fore = smooth(@p) . ' V560 H-40 Z';
sub reeds { my @q = @_; my $r = '';
  for my $q (@q) { my ($x, $hh, $lean, $kind) = @$q; my $y0 = y4($x) + 26; my ($tx, $ty) = ($x + $lean, y4($x) + 6 - $hh);
    if ($kind) {   # листок: вузька лінза, що гнеться вбік
      $r .= sprintf('<path d="M%.1f %.1f Q%.1f %.1f %.1f %.1f Q%.1f %.1f %.1f %.1f Z"/>', $x - 2, $y0, $x + $lean * .15 - 1.5, $y0 - $hh * .6, $tx, $ty, $x + $lean * .15 + 2.6, $y0 - $hh * .6, $x + 2, $y0);
    } else {       # стебло з качалкою
      $r .= sprintf('<path d="M%.0f %.1f Q%.1f %.1f %.1f %.1f" fill="none" stroke-width="2.2"/>', $x, $y0, $x + $lean * .2, $y0 - $hh * .55, $tx, $ty);
      $r .= sprintf('<rect x="%.1f" y="%.1f" width="5.8" height="21" rx="2.9" transform="rotate(%.1f %.1f %.1f)"/>', $tx - 2.9, $ty + 4, $lean * 1.2, $tx, $ty) if $hh > 92;
    } }
  $r }
my @RA = ([26, 98, -5], [48, 124, 3], [66, 76, 14, 1], [84, 90, 8], [104, 138, -2], [122, 96, -16, 1], [138, 108, 6], [160, 80, -8], [182, 116, 2], [200, 104, 18, 1],
  [214, 86, 9], [244, 128, -3], [262, 70, -12, 1], [286, 96, 6], [316, 112, -5], [338, 64, 10, 1], [366, 84, 4], [392, 58, -9, 1], [424, 72, -4], [452, 50, 8, 1], [476, 62, 3]);
my @RB = map { [2000 - $_->[0], $_->[1] + (($_->[0] * 7) % 23) - 11, -$_->[2], $_->[3]] } @RA;
my $reedA = reeds(@RA); my $reedB = reeds(@RB);

my $stars = ''; srand(7); for (1 .. 110) { $stars .= sprintf('<circle cx="%.0f" cy="%.0f" r="%.1f" opacity="%.2f"/>', rand(2000), 90 + rand(210), .6 + rand(1.1), .35 + rand(.6)); }

my $svg = <<"SVG";
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 90 2000 470" preserveAspectRatio="xMidYMax slice">
  <!-- «Обрій»: Тетерів у Житомирі на головній (стилі шарів — static/obrii.css; web/sky.js вписує їх у малюнок разом із кольорами цієї миті й показує однією картинкою).
       Згори вниз: зорі (хмари малює sky.js окремим шаром), далекі пагорби, підвісний пішохідний міст, лісисті береги каньйону з гранітною скелею,
       вода з камінням і лататтям, ближні береги, передній план з очеретом. Птахів у небі домальовує sky.js (ld-birds) — за погодою, часом і порою року. Далеке не розмите, а тихе: мало контрасту, нерівні «живі» краї (зсув шумом)
       й туман між шарами; чіткий і темний лише передній план. На воді — тіні берегів, дерев і моста: sky.js
       нахиляє й видовжує їх за сонцем (data-shd — рівень води, від якого падає тінь). Геометрію згенеровано скриптом, руками не правити. -->
  <defs>
    <filter id="ob-b0" x="-50%" y="-100%" width="200%" height="300%"><feGaussianBlur stdDeviation="2.2"/></filter>
    <filter id="ob-b1" x="-20%" y="-200%" width="140%" height="500%"><feGaussianBlur stdDeviation="14 5"/></filter>
    <filter id="ob-b2" x="-20%" y="-100%" width="140%" height="300%"><feGaussianBlur stdDeviation="34 16"/></filter>
    <filter id="ob-org" filterUnits="userSpaceOnUse" x="-60" y="60" width="2120" height="520"><feTurbulence type="fractalNoise" baseFrequency=".045 .07" numOctaves="2" seed="4" result="n"/><feDisplacementMap in="SourceGraphic" in2="n" scale="6" xChannelSelector="R" yChannelSelector="G"/></filter>
    <filter id="ob-fr" filterUnits="userSpaceOnUse" x="-60" y="60" width="2120" height="520"><feGaussianBlur stdDeviation="5 2.4"/></filter>
    <!-- тінь лісистих схилів на воді: той самий малюнок (use — щоб не тримати дерева у файлі двічі), розмитий і залитий кольором тіні -->
    <filter id="ob-frs" filterUnits="userSpaceOnUse" x="-60" y="60" width="2120" height="520"><feGaussianBlur stdDeviation="5 2.4" result="b"/><feFlood class="ob-fshd"/><feComposite operator="in" in2="b"/></filter>
    <linearGradient id="ob-g1" gradientUnits="userSpaceOnUse" x1="0" y1="300" x2="0" y2="352"><stop offset="0" class="ob-s1"/><stop offset="1" class="ob-s1m"/></linearGradient>
    <linearGradient id="ob-g2" gradientUnits="userSpaceOnUse" x1="0" y1="244" x2="0" y2="360"><stop offset="0" class="ob-s2"/><stop offset="1" class="ob-s2m"/></linearGradient>
    <linearGradient id="ob-g2b" gradientUnits="userSpaceOnUse" x1="0" y1="286" x2="0" y2="382"><stop offset="0" class="ob-s2b"/><stop offset="1" class="ob-s2bm"/></linearGradient>
    <linearGradient id="ob-g3t" gradientUnits="userSpaceOnUse" x1="0" y1="290" x2="0" y2="412"><stop offset="0" class="ob-s3t"/><stop offset="1" class="ob-s3tm"/></linearGradient>
    <linearGradient id="ob-g3" gradientUnits="userSpaceOnUse" x1="0" y1="392" x2="0" y2="456"><stop offset="0" class="ob-s3m"/><stop offset="1" class="ob-s3"/></linearGradient>
    <linearGradient id="ob-g4" x1="0" y1="0" x2="0" y2="1"><stop offset="0" class="ob-s4"/><stop offset=".55" class="ob-sbg"/></linearGradient>
    <linearGradient id="ob-gw" gradientUnits="userSpaceOnUse" x1="0" y1="356" x2="0" y2="470"><stop offset="0" class="ob-sw0"/><stop offset="1" class="ob-sw1"/></linearGradient>
    <linearGradient id="ob-gm" x1="0" y1="0" x2="0" y2="1"><stop offset="0" class="ob-sm0"/><stop offset=".55" class="ob-sm1"/><stop offset="1" class="ob-sm0"/></linearGradient>
    <clipPath id="ob-cw"><rect x="-40" y="356" width="2080" height="120"/></clipPath>
    <clipPath id="ob-cbl"><path d="$bankL"/></clipPath><clipPath id="ob-cbr"><path d="$bankR"/></clipPath>
    <clipPath id="ob-cml"><path d="$midL"/></clipPath><clipPath id="ob-cmr"><path d="$midR"/></clipPath>
    <linearGradient id="ob-gdl"><stop offset="0" class="ob-sd1"/><stop offset=".8" class="ob-sd0"/></linearGradient>
    <linearGradient id="ob-gdr"><stop offset=".2" class="ob-sd0"/><stop offset="1" class="ob-sd1"/></linearGradient>
    <!-- жодного плаского елемента: у кожного свій градієнт (світліший верх чи середина), кольори — зі змінних у obrii.css -->
    <linearGradient id="ob-gp" x1="0" y1="0" x2="0" y2="1"><stop offset="0" class="ob-sp0"/><stop offset="1" class="ob-sp1"/></linearGradient>
    <radialGradient id="ob-gc" cx=".5" cy=".28" r=".8"><stop offset="0" class="ob-sc0"/><stop offset="1" class="ob-sc1"/></radialGradient>
    <linearGradient id="ob-gbr" gradientUnits="userSpaceOnUse" x1="0" y1="224" x2="0" y2="350"><stop offset="0" class="ob-sb0"/><stop offset="1" class="ob-sb1"/></linearGradient>
    <linearGradient id="ob-grk" x1="0" y1="0" x2="0" y2="1"><stop offset="0" class="ob-sk0"/><stop offset=".7" class="ob-sk1"/></linearGradient>
    <linearGradient id="ob-grk2" x1="0" y1="0" x2="0" y2="1"><stop offset="0" class="ob-sk20"/><stop offset=".7" class="ob-sk21"/></linearGradient>
    <linearGradient id="ob-gst" x1="0" y1="0" x2="0" y2="1"><stop offset="0" class="ob-st0"/><stop offset="1" class="ob-st1"/></linearGradient>
    <radialGradient id="ob-gpad" cx=".4" cy=".35" r=".75"><stop offset="0" class="ob-spd0"/><stop offset="1" class="ob-spd1"/></radialGradient>
    <radialGradient id="ob-gpad2" cx=".4" cy=".35" r=".75"><stop offset="0" class="ob-spd20"/><stop offset="1" class="ob-spd21"/></radialGradient>
    <radialGradient id="ob-gpad3" cx=".4" cy=".35" r=".75"><stop offset="0" class="ob-spd30"/><stop offset="1" class="ob-spd31"/></radialGradient>
    <radialGradient id="ob-gpad4" cx=".4" cy=".35" r=".75"><stop offset="0" class="ob-spd40"/><stop offset="1" class="ob-spd41"/></radialGradient>
    <radialGradient id="ob-gbud" cx=".4" cy=".3" r=".8"><stop offset="0" class="ob-sbd0"/><stop offset="1" class="ob-sbd1"/></radialGradient>
    <radialGradient id="ob-gfl" cx=".4" cy=".35" r=".7"><stop offset="0" class="ob-sf0"/><stop offset="1" class="ob-sf1"/></radialGradient>
    <linearGradient id="ob-grd" gradientUnits="userSpaceOnUse" x1="0" y1="310" x2="0" y2="470"><stop offset="0" class="ob-srd0"/><stop offset="1" class="ob-srd1"/></linearGradient>
    <radialGradient id="ob-gc2" cx=".5" cy=".28" r=".8"><stop offset="0" class="ob-sc20"/><stop offset="1" class="ob-sc21"/></radialGradient>
    <radialGradient id="ob-gc3" cx=".5" cy=".28" r=".8"><stop offset="0" class="ob-sc30"/><stop offset="1" class="ob-sc31"/></radialGradient>
    <linearGradient id="ob-gsn" x1="0" y1="0" x2="0" y2="1"><stop offset="0" class="ob-ssn0"/><stop offset="1" class="ob-ssn1"/></linearGradient>
    <linearGradient id="ob-gts" x1="0" y1="0" x2="0" y2="1"><stop offset="0" class="ob-sts0"/><stop offset="1" class="ob-sts1"/></linearGradient>
    <linearGradient id="ob-gr" gradientUnits="userSpaceOnUse" x1="0" y1="356" x2="0" y2="478"><stop offset="0" class="ob-sr0"/><stop offset="1" class="ob-sr1"/></linearGradient>
    <linearGradient id="ob-gwl" x1="0" y1="0" x2="0" y2="1"><stop offset="0" class="ob-swl0"/><stop offset="1" class="ob-swl1"/></linearGradient>
    <!-- далекий ліс: координати — свої в кожного шматка ярусу (0 — земля, вгору — мінус), тож градієнт один на всіх -->
    <linearGradient id="ob-gff" gradientUnits="userSpaceOnUse" x1="0" y1="-22" x2="0" y2="12"><stop offset="0" class="ob-sff0"/><stop offset=".42" class="ob-sff1"/><stop offset=".68" class="ob-sff2"/><stop offset="1" class="ob-sff3"/></linearGradient>
    <linearGradient id="ob-gfe" gradientUnits="userSpaceOnUse" x1="0" y1="-26" x2="0" y2="4"><stop offset="0" class="ob-sfe0"/><stop offset=".6" class="ob-sfe1"/><stop offset="1" class="ob-sfe2"/></linearGradient>
  </defs>
  <g class="ld-stars">$stars</g>
  <g class="ld-birds"></g>
  <path class="ld-l1" d="$far"/>
  <rect class="ld-mist" style="--k:.8" x="-40" y="286" width="2080" height="84"/>
  <g class="ld-br">$br</g>
  <g class="ld-lamp">$lamp</g>
  <g class="ld-l2" filter="url(#ob-org)"><path d="$bankL"/><path d="$bankR"/>$snowFar$reliefFar</g>
  <g class="ld-l2t">$farT</g>
  <g><path class="ld-rock" d="$rock"/><path class="ld-rock-l" d="$rockL"/><path class="ld-ts-l" d="M1284 358 L1290 336 L1302 327 L1310 311 L1318 340 L1300 358 Z"/><path class="ld-ts-r" d="M1404 305 L1420 307 L1432 331 L1440 358 L1412 358 Z"/></g>
  <rect class="ld-water" x="-40" y="356" width="2080" height="120"/>
  <g class="ld-shd" clip-path="url(#ob-cw)"><g filter="url(#ob-fr)" data-shd="356" transform="matrix(1 0 0 -.6 0 569.6)"><path d="$bankL"/><path d="$bankR"/><path d="$rock"/>$brS</g></g>
  <g clip-path="url(#ob-cw)"><path class="ld-refl" filter="url(#ob-b1)" d="M-13 346L13 346L62 482L-62 482Z"/></g>
  <g class="ld-mist" style="--k:.9" filter="url(#ob-b1)"><ellipse cx="1000" cy="354" rx="560" ry="13"/><ellipse cx="330" cy="346" rx="330" ry="11"/><ellipse cx="1680" cy="348" rx="340" ry="11"/></g>
  <path class="ld-horizon" d="M-40 357.2H2040"/>
  <path class="ld-rip2" d="$rip2"/><path class="ld-rip" d="$rip1"/>
  <g clip-path="url(#ob-cw)"><g class="ld-glit">$glit</g></g>
  <g class="ld-leaves">$leaves</g>
  <g class="ld-splash">$splash</g>
  <g class="ld-st">$st</g>
  <g id="ob-mid"><g class="ld-l2b" filter="url(#ob-org)"><path d="$midL"/><path d="$midR"/><path d="$knoll"/>$snowMid$knollS$reliefMid$midT</g>
  <g class="ld-willow" filter="url(#ob-org)">$willows</g></g>
  <g class="ld-shd" clip-path="url(#ob-cw)"><g filter="url(#ob-frs)" data-shd="380" transform="matrix(1 0 0 -.6 0 608)"><use href="#ob-mid"/></g></g>
  <g>$rock2T<path class="ld-rock2" d="$rock2"/><path class="ld-rock2-l" d="$rock2L"/><path class="ld-snow" d="M322 318 L348 305 L370 309 L392 296 L420 300 L434 319 L420 307 L394 304 L372 316 L350 312 L330 323 Z"/><path class="ld-ts-l" d="M290 380 L298 352 L316 341 L322 318 L332 347 L318 380 Z"/><path class="ld-ts-r" d="M434 319 L460 326 L470 350 L486 380 L452 380 L444 356 Z"/></g>
  <g class="ld-mist" style="--k:.5" filter="url(#ob-b1)"><ellipse cx="250" cy="378" rx="330" ry="9"/><ellipse cx="1760" cy="378" rx="320" ry="9"/></g>
  <g filter="url(#ob-b0)">$fl_soft</g>
  <g>$l</g>
  <g>$fly</g>
  <g class="ld-l3" filter="url(#ob-org)"><path d="$nearL"/><path d="$nearR"/>@{[snow(\&y3L, -40, 620, 4, 11, 1.1) . snow(\&y3R, 1390, 2040, 4, 11, 2.8)]}</g>
  <g class="ld-tr3" filter="url(#ob-org)">$nearT</g>
  <rect class="ld-mist" style="--k:.45" x="-40" y="396" width="2080" height="70"/>
  <path class="ld-l4" d="$fore"/>
  @{[snow(\&y4, -40, 2040, 5, 13, .6)]}
  <g class="ld-reed ld-reed-a">$reedA</g>
  <g class="ld-reed ld-reed-b">$reedB</g>
</svg>
SVG
# числа в обрисах: без «.0», без нуля перед крапкою, без зайвих пробілів — на вигляд не впливає
sub compact { my $d = shift; $d =~ s/(\d)\.0(?!\d)/$1/g; $d =~ s/(?<![\d.])0\.(\d)/.$1/g; $d =~ s/ -/-/g; $d =~ s/([A-Za-z]) /$1/g; $d }
$svg =~ s/ d="([^"]+)"/' d="' . compact($1) . '"'/ge;
open O, ">:utf8", "$OUT/obrii.svg" or die; print O $svg; close O; print length($svg), " bytes obrii; trees: ", scalar(() = $svg =~ /<g transform="rotate/g), "\n";

