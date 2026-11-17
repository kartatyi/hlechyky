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
my $rock = q{M1284 358 L1290 336 L1302 327 L1310 311 L1330 303 L1352 301 L1364 291 L1392 293 L1404 305 L1420 307 L1432 331 L1440 358 Z};
my $rockL = q{M1310 311 L1330 303 L1338 330 L1318 340 Z M1364 291 L1392 293 L1398 322 L1374 328 Z};

# ---- ближчі схили: сосни й крони, які вже видно поодинці, і гранітна скеля на лівому березі — «гори поруч»
sub yML { my $x = shift; my $t = ss(($x - 140) / 440); my $top = 300 + 7 * sin($x / 90 + .5); $top + (374 - $top) * $t }
sub yMR { my $x = shift; my $t = ss((1880 - $x) / 420); my $top = 296 + 7 * sin($x / 80 + 1.7); $top + (374 - $top) * $t }
sub pine { my ($x, $y, $h, $w) = @_; sprintf('<path d="M%.1f %.1f L%.1f %.1f L%.1f %.1f L%.1f %.1f L%.1f %.1f L%.1f %.1f L%.1f %.1f L%.1f %.1f L%.1f %.1f L%.1f %.1f L%.1f %.1f Z"/>',
  $x, $y - $h, $x + $w * .32, $y - $h * .62, $x + $w * .14, $y - $h * .62, $x + $w * .42, $y - $h * .3, $x + $w * .2, $y - $h * .3, $x + $w * .5, $y,
  $x - $w * .5, $y, $x - $w * .2, $y - $h * .3, $x - $w * .42, $y - $h * .3, $x - $w * .14, $y - $h * .62, $x - $w * .32, $y - $h * .62) }
sub crown { my ($x, $y, $h, $w) = @_; sprintf('<ellipse cx="%.1f" cy="%.1f" rx="%.1f" ry="%.1f"/><ellipse cx="%.1f" cy="%.1f" rx="%.1f" ry="%.1f"/>',
  $x, $y - $h * .55, $w * .75, $h * .5, $x + $w * .45, $y - $h * .36, $w * .55, $h * .34) }
srand(11);
sub trees { my ($f, $x0, $x1, $tf, $skip) = @_; my $o = ''; my $x = $x0;
  while ($x < $x1) { my $t = $tf->($x); my $k = 1 - .45 * $t; my $y = $f->($x) + 3;
    unless ($t > .88 || ($skip && $x > $skip->[0] && $x < $skip->[1])) {
      $o .= rand() < .62 ? pine($x, $y, (30 + rand(22)) * $k, (12 + rand(6)) * $k) : crown($x, $y, (22 + rand(14)) * $k, (12 + rand(5)) * $k); }
    $x += (8 + rand(11)) * $k + 3; }
  $o }
@p = (); for (my $x = -40; $x <= 580; $x += 12) { push @p, [$x, yML($x)]; }
my $midL = smooth(@p) . ' V380 H-40 Z';
@p = (); for (my $x = 1460; $x <= 2040; $x += 12) { push @p, [$x, yMR($x)]; }
my $midR = smooth(@p) . ' V380 H1460 Z';
my $midT = trees(\&yML, -30, 560, sub { ss(($_[0] - 140) / 440) }, [296, 486]) . trees(\&yMR, 1480, 2030, sub { ss((1880 - $_[0]) / 420) });
my $rock2 = 'M290 380 L298 352 L316 341 L322 318 L348 305 L370 309 L392 296 L420 300 L434 319 L460 326 L470 350 L486 380 Z';
my $rock2L = 'M322 318 L348 305 L358 336 L332 347 Z M392 296 L420 300 L425 331 L400 336 Z M434 319 L460 326 L462 352 L444 356 Z';
my $rock2T = pine(336, 308, 24, 11) . pine(378, 306, 30, 13) . crown(408, 300, 22, 12) . pine(446, 324, 22, 10);
# ---- дерева на ближніх берегах — великі, майже чіткі
my $nearT = '';
for my $q ([34, 104, 34], [78, 78, 27], [124, 118, 38], [176, 86, 29], [216, 64, 23], [1790, 70, 24], [1832, 96, 31], [1880, 122, 39], [1930, 84, 28], [1972, 108, 35]) { my ($x, $hh, $w) = @$q;
  $nearT .= pine($x, ($x < 1000 ? y3L($x) : y3R($x)) + 4, $hh, $w); }
# ---- гребля вдалині, у просвіті під мостом: низька стіна й біла вода зі скидів
my $dam = '<rect x="900" y="347.4" width="208" height="9.2"/><rect x="938" y="343.6" width="8" height="5"/><rect x="1060" y="343.6" width="8" height="5"/>';
my $damW = '<rect x="952" y="349.6" width="102" height="7"/><ellipse cx="1003" cy="357.8" rx="72" ry="2.2"/>';

# ---- підвісний міст: два пілони-рами, несучий трос, підвіски, тонкий настил
my $br = '';
for my $x (740, 1260) {
  $br .= sprintf('<rect x="%.1f" y="226" width="2.2" height="122"/><rect x="%.1f" y="226" width="2.2" height="122"/>', $x - 4.3, $x + 2.1);
  $br .= sprintf('<rect x="%.1f" y="224" width="12" height="3"/><rect x="%.1f" y="249" width="8.6" height="1.6"/><rect x="%.1f" y="266" width="8.6" height="1.6"/>', $x - 6, $x - 4.3, $x - 4.3);
  $br .= sprintf('<path class="ld-br-w" d="M%.1f 228 L%.1f 249 M%.1f 228 L%.1f 249"/>', $x - 3.2, $x + 3.2, $x + 3.2, $x - 3.2);
}
$br .= '<rect x="520" y="279" width="960" height="3.4"/>';
my $h = '';
for (my $x = 760; $x <= 1240; $x += 20) { my $t = ($x - 740) / 520; $h .= sprintf('M%d %.1f V279', $x, 227 + 180 * $t * (1 - $t)); }
for (my $x = 560; $x <= 720; $x += 20) { $h .= sprintf('M%d %.1f V279', $x, 281 - ($x - 540) / 200 * 54); }
for (my $x = 1280; $x <= 1440; $x += 20) { $h .= sprintf('M%d %.1f V279', $x, 227 + ($x - 1260) / 200 * 54); }
my $lamp = q{}; for (my $x = 580; $x <= 1420; $x += 70) { $lamp .= sprintf(q{<circle class="ld-lamp-g" cx="%d" cy="275" r="5"/><circle cx="%d" cy="275.4" r="1.3"/>}, $x, $x); }
$br .= '<path class="ld-br-c" d="M540 281 L740 227 Q1000 317 1260 227 L1460 281"/><path class="ld-br-w" d="' . $h . ' M520 274.6 H1480"/>';

# ---- вода: відблиски, каміння, латаття
my $glint = 'M930 366h110M1010 372h70M700 382h120M1180 380h150M420 394h90M1500 392h120M860 400h80M1040 388h60';
my $st = '';
for my $q ([636, 374, 27, 9], [692, 381, 13, 5], [588, 384, 9, 3.4], [1334, 372, 31, 10], [1396, 380, 15, 5.5], [1092, 366, 11, 4], [912, 364, 8, 3]) {
  my ($x, $y, $rx, $ry) = @$q; $st .= sprintf('<path d="M%.0f %.1f A%.0f %.1f 0 0 1 %.0f %.1f Z"/>', $x - $rx, $y, $rx, $ry, $x + $rx, $y); }
my $l = '';
for my $q ([440, 392, 30, 6.5], [504, 398, 24, 5.5], [390, 400, 20, 4.6], [1320, 394, 28, 6], [1378, 400, 21, 4.8], [966, 396, 26, 5.6], [1022, 402, 18, 4], [930, 403, 14, 3.2]) {
  my ($x, $y, $rx, $ry) = @$q; $l .= sprintf('<path class="ld-pad" d="M%.0f %.1f A%.0f %.1f 0 1 1 %.1f %.1f L%.0f %.1f Z"/>', $x + $rx, $y, $rx, $ry, $x + $rx * .86, $y - $ry * .5, $x, $y); }
for my $q ([432, 387, 5.2], [510, 394, 4.4], [1328, 389, 5], [972, 392, 4.6]) { my ($x, $y, $r) = @$q;
  $l .= sprintf('<circle class="ld-fl-glow" cx="%.0f" cy="%.1f" r="%.1f"/><circle class="ld-fl" cx="%.0f" cy="%.1f" r="%.1f"/><circle class="ld-fl-c" cx="%.0f" cy="%.1f" r="%.1f"/>', $x, $y, $r * 2.6, $x, $y, $r, $x, $y, $r * .42); }

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
  <!-- «Обрій»: Тетерів у Житомирі на головній (web/sky.js ставить кольори змінними, static/polyva.css розкладає їх по шарах).
       Згори вниз: зорі, хмари, далекі пагорби, підвісний пішохідний міст, лісисті береги каньйону з гранітною скелею,
       вода з камінням і лататтям, ближні береги, передній план з очеретом. Усе далеке розмите й тоне в тумані —
       чіткий лише передній план. Геометрію згенеровано скриптом, руками не правити. -->
  <defs>
    <filter id="ob-b1" x="-20%" y="-200%" width="140%" height="500%"><feGaussianBlur stdDeviation="14 5"/></filter>
    <filter id="ob-b2" x="-20%" y="-100%" width="140%" height="300%"><feGaussianBlur stdDeviation="34 16"/></filter>
    <filter id="ob-f1" filterUnits="userSpaceOnUse" x="-60" y="60" width="2120" height="520"><feGaussianBlur stdDeviation="4.5"/></filter>
    <filter id="ob-f2" filterUnits="userSpaceOnUse" x="-60" y="60" width="2120" height="520"><feGaussianBlur stdDeviation="2.2"/></filter>
    <filter id="ob-f3" filterUnits="userSpaceOnUse" x="-60" y="60" width="2120" height="520"><feGaussianBlur stdDeviation="1.1"/></filter>
    <filter id="ob-fb" filterUnits="userSpaceOnUse" x="-60" y="60" width="2120" height="520"><feGaussianBlur stdDeviation=".55"/></filter>
    <filter id="ob-fr" filterUnits="userSpaceOnUse" x="-60" y="60" width="2120" height="520"><feGaussianBlur stdDeviation="5 2.4"/></filter>
    <linearGradient id="ob-g1" gradientUnits="userSpaceOnUse" x1="0" y1="300" x2="0" y2="352"><stop offset="0" class="ob-s1"/><stop offset="1" class="ob-s1m"/></linearGradient>
    <linearGradient id="ob-g2" gradientUnits="userSpaceOnUse" x1="0" y1="244" x2="0" y2="360"><stop offset="0" class="ob-s2"/><stop offset="1" class="ob-s2m"/></linearGradient>
    <linearGradient id="ob-g2b" gradientUnits="userSpaceOnUse" x1="0" y1="286" x2="0" y2="382"><stop offset="0" class="ob-s2b"/><stop offset="1" class="ob-s2bm"/></linearGradient>
    <linearGradient id="ob-g3" gradientUnits="userSpaceOnUse" x1="0" y1="392" x2="0" y2="456"><stop offset="0" class="ob-s3m"/><stop offset="1" class="ob-s3"/></linearGradient>
    <linearGradient id="ob-g4" x1="0" y1="0" x2="0" y2="1"><stop offset="0" class="ob-s4"/><stop offset=".55" class="ob-sbg"/></linearGradient>
    <linearGradient id="ob-gw" gradientUnits="userSpaceOnUse" x1="0" y1="356" x2="0" y2="470"><stop offset="0" class="ob-sw0"/><stop offset="1" class="ob-sw1"/></linearGradient>
    <linearGradient id="ob-gm" x1="0" y1="0" x2="0" y2="1"><stop offset="0" class="ob-sm0"/><stop offset=".55" class="ob-sm1"/><stop offset="1" class="ob-sm0"/></linearGradient>
    <clipPath id="ob-cw"><rect x="-40" y="356" width="2080" height="120"/></clipPath>
    <radialGradient id="ob-gr" cx=".5" cy="0" r="1"><stop offset="0" class="ob-sr0"/><stop offset="1" class="ob-sr1"/></radialGradient>
  </defs>
  <g class="ld-stars">$stars</g>
  <g class="ld-cirrus" filter="url(#ob-b1)"><ellipse cx="430" cy="232" rx="360" ry="9"/><ellipse cx="1380" cy="206" rx="430" ry="11"/><ellipse cx="1010" cy="250" rx="250" ry="6"/></g>
  <g class="ld-deck" filter="url(#ob-b2)"><ellipse cx="300" cy="126" rx="520" ry="58"/><ellipse cx="1050" cy="104" rx="620" ry="64"/><ellipse cx="1760" cy="140" rx="480" ry="56"/><ellipse cx="700" cy="196" rx="420" ry="34"/><ellipse cx="1480" cy="212" rx="460" ry="30"/></g>
  <path class="ld-l1" filter="url(#ob-f1)" d="$far"/>
  <rect class="ld-mist" style="--k:.8" x="-40" y="286" width="2080" height="84"/>
  <g class="ld-br" filter="url(#ob-fb)">$br</g>
  <g class="ld-dam" filter="url(#ob-fb)">$dam</g>
  <g class="ld-lamp">$lamp</g>
  <g class="ld-l2" filter="url(#ob-f2)"><path d="$bankL"/><path d="$bankR"/></g>
  <g filter="url(#ob-f3)"><path class="ld-rock" d="$rock"/><path class="ld-rock-l" d="$rockL"/></g>
  <rect class="ld-water" x="-40" y="356" width="2080" height="120"/>
  <g class="ld-l2r" clip-path="url(#ob-cw)"><g filter="url(#ob-fr)" transform="matrix(1 0 0 -.42 0 505.5)"><path d="$bankL"/><path d="$bankR"/><path d="$rock"/></g></g>
  <g clip-path="url(#ob-cw)"><ellipse class="ld-refl" cx="0" cy="356" rx="62" ry="50"/></g>
  <g class="ld-mist" style="--k:.9" filter="url(#ob-b1)"><ellipse cx="1000" cy="354" rx="560" ry="13"/><ellipse cx="330" cy="346" rx="330" ry="11"/><ellipse cx="1680" cy="348" rx="340" ry="11"/></g>
  <g class="ld-dam-w" filter="url(#ob-fb)">$damW</g>
  <path class="ld-glint" d="$glint"/>
  <g class="ld-st" filter="url(#ob-f3)">$st</g>
  <g class="ld-l2b" filter="url(#ob-f3)"><path d="$midL"/><path d="$midR"/>$midT$rock2T</g>
  <g filter="url(#ob-fb)"><path class="ld-rock2" d="$rock2"/><path class="ld-rock2-l" d="$rock2L"/></g>
  <g class="ld-mist" style="--k:.5" filter="url(#ob-b1)"><ellipse cx="250" cy="378" rx="330" ry="9"/><ellipse cx="1760" cy="378" rx="320" ry="9"/></g>
  <g>$l</g>
  <g class="ld-l3" filter="url(#ob-f3)"><path d="$nearL"/><path d="$nearR"/></g>
  <g class="ld-tr3" filter="url(#ob-fb)">$nearT</g>
  <rect class="ld-mist" style="--k:.45" x="-40" y="396" width="2080" height="70"/>
  <path class="ld-l4" d="$fore"/>
  <g class="ld-reed ld-reed-a">$reedA</g>
  <g class="ld-reed ld-reed-b">$reedB</g>
</svg>
SVG
open O, ">:utf8", "$OUT/obrii.svg" or die; print O $svg; close O; print length($svg), " bytes obrii\n";

# ---- низ сторінки: той самий берег — туман і очерет, що росте з нижнього краю
sub reeds2 { my @q = @_; my $r = '';
  for my $q (@q) { my ($x, $hh, $lean, $kind) = @$q; my $y0 = 246; my ($tx, $ty) = ($x + $lean, 236 - $hh);
    if ($kind) { $r .= sprintf('<path d="M%.1f %.1f Q%.1f %.1f %.1f %.1f Q%.1f %.1f %.1f %.1f Z"/>', $x - 2, $y0, $x + $lean * .15 - 1.5, $y0 - $hh * .6, $tx, $ty, $x + $lean * .15 + 2.6, $y0 - $hh * .6, $x + 2, $y0); }
    else { $r .= sprintf('<path d="M%.0f %.1f Q%.1f %.1f %.1f %.1f" fill="none" stroke-width="2.2"/>', $x, $y0, $x + $lean * .2, $y0 - $hh * .55, $tx, $ty);
      $r .= sprintf('<rect x="%.1f" y="%.1f" width="5.8" height="21" rx="2.9" transform="rotate(%.1f %.1f %.1f)"/>', $tx - 2.9, $ty + 4, $lean * 1.2, $tx, $ty) if $hh > 92; } }
  $r }
my @EA = map { [$_->[0] * 1.15 + 10, $_->[1] * .92 + 8, $_->[2], $_->[3]] } @RA;
my @EB = map { [2000 - $_->[0], $_->[1] + (($_->[0] * 5) % 19) - 9, -$_->[2], $_->[3]] } @EA;
my $niz = <<"SVG";
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 2000 240" preserveAspectRatio="xMidYMax slice">
  <!-- Низ головної: той самий берег, що й у «Обрії» вгорі, — пасмо туману й очерет з нижнього краю сторінки. -->
  <defs>
    <linearGradient id="ob-ge" x1="0" y1="0" x2="0" y2="1"><stop offset="0" class="ob-sm0"/><stop offset=".72" class="ob-sm1"/><stop offset="1" class="ob-sm0"/></linearGradient>
    <filter id="ob-e1" x="-20%" y="-200%" width="140%" height="500%"><feGaussianBlur stdDeviation="30 10"/></filter>
  </defs>
  <rect class="ld-emist" x="-40" y="60" width="2080" height="180"/>
  <g class="ld-emist2" filter="url(#ob-e1)"><ellipse cx="520" cy="196" rx="520" ry="18"/><ellipse cx="1500" cy="208" rx="560" ry="16"/></g>
  <g class="ld-ereed">@{[reeds2(@EA)]}</g>
  <g class="ld-ereed">@{[reeds2(@EB)]}</g>
</svg>
SVG
open O, ">:utf8", "$OUT/obrii-niz.svg" or die; print O $niz; close O; print length($niz), " bytes niz\n";
