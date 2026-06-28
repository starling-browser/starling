"use strict";

// Magnet Alphabet — a pointer/touch toy inspired by a wooden magnetic letter
// board. Drag the magnet near a letter disc to pick it up, then drop it on its
// matching slot. The pull is "magnetic": you don't need to be exactly on a disc
// to grab it, and a held disc hangs below the magnet with a springy lag.

const DESIGN_W = 960;
const DESIGN_H = 720;

const LETTERS = "ABCDEFGHIJKLMNOPQRSTUVWXYZ".split("");
const COLS = 7;

const DISC_R = 34;          // disc radius
const SLOT_R = 37;          // target slot radius
const GRAB_R = 98;          // how close the magnet pole must be to snatch a disc
const SNAP_R = 50;          // how close a held disc must be to lock into its slot
const FIELD_R = 150;        // range at which idle discs feel a gentle tug

const SLOT_TOP = 128;
const SLOT_GAP_X = 116;
const SLOT_GAP_Y = 104;
const TRAY_ROWS = [560, 628];

const FONT = '"Baloo 2", "Comic Sans MS", sans-serif';

const hue = (i) => Math.round((i / LETTERS.length) * 360);
const hsl = (h, s, l) => "hsl(" + h + ", " + s + "%, " + l + "%)";

// Phaser int colour for a letter (used by rings, confetti, glows).
function letterColor(i) {
  return Phaser.Display.Color.HSLToColor(hue(i) / 360, 0.66, 0.55).color;
}

const config = {
  type: Phaser.AUTO,
  parent: "game",
  width: DESIGN_W,
  height: DESIGN_H,
  backgroundColor: "#bfe9ff",
  scale: {
    mode: Phaser.Scale.FIT,
    autoCenter: Phaser.Scale.CENTER_BOTH,
  },
  scene: { create, update },
};

function boot() {
  window.game = new Phaser.Game(config);
}

// Wait for the rounded font so canvas text isn't drawn in a fallback first.
if (document.fonts && document.fonts.load) {
  Promise.all([
    document.fonts.load('800 60px "Baloo 2"'),
    document.fonts.load('700 32px "Baloo 2"'),
    document.fonts.load('500 24px "Baloo 2"'),
  ]).then(boot).catch(boot);
} else {
  boot();
}

let discs = [];
let slots = [];
let held = null;            // the disc currently stuck to the magnet
let magnetOn = false;       // pointer is pressed
let magnet;                 // the magnet container
let magnetGlow;
let placedCount = 0;
let counterText;
let winLayer = null;
let pointerSeen = false;

function create() {
  const scene = this;

  buildTextures(scene);
  drawScene(scene);
  drawFrame(scene);
  buildTitle(scene);
  layoutSlots(scene);
  spawnDiscs(scene);
  buildMagnet(scene);
  buildHud(scene);

  scene.input.on("pointermove", () => { pointerSeen = true; });
  scene.input.on("pointerdown", (p) => {
    pointerSeen = true;
    magnetOn = true;
    if (winLayer) {
      restart(scene);
      return;
    }
    magnet.x = p.worldX;
    magnet.y = p.worldY;
    tryGrab(scene);
  });

  scene.input.on("pointerup", (p) => {
    magnetOn = false;
    release(scene, p);
  });
}

// ---- Textures (canvas-drawn, glossy and cartoony) --------------------------

function buildTextures(scene) {
  for (let i = 0; i < LETTERS.length; i++) {
    makeDiscTexture(scene, i);
  }
  makeMagnetTexture(scene);
  makeCloudTexture(scene);
}

const DISC_TEX_W = 100;
const DISC_TEX_H = 110;
const DISC_CX = 50;
const DISC_CY = 48;

function makeDiscTexture(scene, i) {
  const key = "disc" + i;
  if (scene.textures.exists(key)) {
    return;
  }
  const tex = scene.textures.createCanvas(key, DISC_TEX_W, DISC_TEX_H);
  const ctx = tex.getContext();
  const h = hue(i);
  const cx = DISC_CX;
  const cy = DISC_CY;
  const r = DISC_R;

  // Soft contact shadow.
  ctx.fillStyle = "rgba(60, 42, 20, 0.20)";
  ctx.beginPath();
  ctx.ellipse(cx, cy + 36, 30, 11, 0, 0, Math.PI * 2);
  ctx.fill();

  // Candy body.
  const body = ctx.createRadialGradient(cx - 12, cy - 15, 4, cx, cy + 6, r + 6);
  body.addColorStop(0, hsl(h, 88, 78));
  body.addColorStop(0.5, hsl(h, 74, 58));
  body.addColorStop(1, hsl(h, 70, 44));
  ctx.fillStyle = body;
  ctx.beginPath();
  ctx.arc(cx, cy, r, 0, Math.PI * 2);
  ctx.fill();

  // Chunky outline.
  ctx.lineWidth = 4.5;
  ctx.strokeStyle = hsl(h, 58, 32);
  ctx.beginPath();
  ctx.arc(cx, cy, r - 1, 0, Math.PI * 2);
  ctx.stroke();

  // Glossy top highlight.
  const gloss = ctx.createRadialGradient(cx - 9, cy - 16, 1, cx - 9, cy - 14, 24);
  gloss.addColorStop(0, "rgba(255, 255, 255, 0.75)");
  gloss.addColorStop(1, "rgba(255, 255, 255, 0)");
  ctx.fillStyle = gloss;
  ctx.beginPath();
  ctx.ellipse(cx - 6, cy - 12, 21, 15, -0.5, 0, Math.PI * 2);
  ctx.fill();

  // Sparkle dot.
  ctx.fillStyle = "rgba(255, 255, 255, 0.92)";
  ctx.beginPath();
  ctx.arc(cx - 15, cy - 17, 3.2, 0, Math.PI * 2);
  ctx.fill();

  tex.refresh();
}

function makeMagnetTexture(scene) {
  if (scene.textures.exists("magnet")) {
    return;
  }
  const w = 88;
  const h = 96;
  const tex = scene.textures.createCanvas("magnet", w, h);
  const ctx = tex.getContext();
  const cx = w / 2;
  const archY = 38;
  const R = 23;
  const legBottom = 74;

  function uPath(inset) {
    ctx.beginPath();
    ctx.moveTo(cx - R, legBottom);
    ctx.lineTo(cx - R, archY);
    ctx.arc(cx, archY, R, Math.PI, 0, false);
    ctx.lineTo(cx + R, legBottom);
  }

  // Drop shadow.
  ctx.save();
  ctx.translate(0, 3);
  ctx.lineWidth = 20;
  ctx.lineCap = "round";
  ctx.strokeStyle = "rgba(60, 42, 20, 0.22)";
  uPath();
  ctx.stroke();
  ctx.restore();

  // Red body.
  const red = ctx.createLinearGradient(cx - R, 0, cx + R, 0);
  red.addColorStop(0, "#ff7a6b");
  red.addColorStop(0.5, "#ec3a39");
  red.addColorStop(1, "#b81f2c");
  ctx.lineWidth = 19;
  ctx.lineCap = "round";
  ctx.strokeStyle = red;
  uPath();
  ctx.stroke();

  // Inner shine along the body.
  ctx.lineWidth = 6;
  ctx.strokeStyle = "rgba(255, 255, 255, 0.5)";
  ctx.beginPath();
  ctx.moveTo(cx - R - 5, legBottom - 6);
  ctx.lineTo(cx - R - 5, archY);
  ctx.arc(cx, archY, R + 5, Math.PI, Math.PI * 1.55, false);
  ctx.stroke();

  // Steel pole tips.
  function pole(px) {
    const grad = ctx.createLinearGradient(px - 11, 0, px + 11, 0);
    grad.addColorStop(0, "#f4f6f8");
    grad.addColorStop(0.5, "#c7ccd2");
    grad.addColorStop(1, "#8e959d");
    ctx.fillStyle = grad;
    roundRect(ctx, px - 11, legBottom - 14, 22, 22, 6);
    ctx.fill();
    ctx.fillStyle = "rgba(255, 255, 255, 0.6)";
    roundRect(ctx, px - 8, legBottom - 12, 6, 16, 3);
    ctx.fill();
  }
  pole(cx - R);
  pole(cx + R);

  tex.refresh();
}

function makeCloudTexture(scene) {
  if (scene.textures.exists("cloud")) {
    return;
  }
  const w = 150;
  const h = 80;
  const tex = scene.textures.createCanvas("cloud", w, h);
  const ctx = tex.getContext();
  ctx.fillStyle = "rgba(255, 255, 255, 0.95)";
  const puffs = [
    [42, 48, 26],
    [72, 40, 32],
    [104, 50, 24],
    [60, 56, 22],
    [90, 58, 20],
  ];
  for (const [x, y, r] of puffs) {
    ctx.beginPath();
    ctx.arc(x, y, r, 0, Math.PI * 2);
    ctx.fill();
  }
  tex.refresh();
}

function roundRect(ctx, x, y, w, h, r) {
  ctx.beginPath();
  ctx.moveTo(x + r, y);
  ctx.arcTo(x + w, y, x + w, y + h, r);
  ctx.arcTo(x + w, y + h, x, y + h, r);
  ctx.arcTo(x, y + h, x, y, r);
  ctx.arcTo(x, y, x + w, y, r);
  ctx.closePath();
}

// ---- Scenery ---------------------------------------------------------------

function drawScene(scene) {
  const g = scene.add.graphics().setDepth(0);

  // Sky inside the board.
  g.fillGradientStyle(0xbfe9ff, 0xbfe9ff, 0xeaf7ff, 0xeaf7ff, 1);
  g.fillRect(26, 26, DESIGN_W - 52, DESIGN_H - 52);

  // Sun, top-left, with rays.
  const sx = 92;
  const sy = 92;
  g.fillStyle(0xffe27a, 0.9);
  for (let i = 0; i < 12; i++) {
    const a = (i / 12) * Math.PI * 2;
    g.slice(sx, sy, 56, a - 0.12, a + 0.12, false);
    g.fillPath();
  }
  g.fillStyle(0xffd23f, 1);
  g.fillCircle(sx, sy, 34);
  g.fillStyle(0xffe487, 1);
  g.fillCircle(sx, sy, 26);

  // Rolling hills behind the play area.
  g.fillStyle(0x8fd06a, 1);
  g.fillEllipse(250, 560, 620, 300);
  g.fillStyle(0x7cc659, 1);
  g.fillEllipse(760, 580, 640, 300);

  // Grass floor + tray panel.
  g.fillStyle(0x86cc5e, 1);
  g.fillRect(26, 500, DESIGN_W - 52, DESIGN_H - 526);
  g.fillStyle(0x74bd4d, 1);
  g.fillRoundedRect(60, 536, DESIGN_W - 120, 150, 22);
  g.fillStyle(0x000000, 0.08);
  g.fillRoundedRect(60, 536, DESIGN_W - 120, 16, 8);

  decorate(scene);
}

function decorate(scene) {
  const g = scene.add.graphics().setDepth(0);

  // A leafy tree on the right.
  g.fillStyle(0x9c6b3f, 1);
  g.fillRoundedRect(875, 300, 26, 210, 8);
  g.fillStyle(0x5bbf57, 1);
  g.fillCircle(888, 300, 50);
  g.fillCircle(846, 322, 34);
  g.fillCircle(930, 322, 34);
  g.fillStyle(0x6fd06a, 1);
  g.fillCircle(888, 286, 34);

  // Bushes bottom-left.
  g.fillStyle(0x5bbf57, 1);
  g.fillCircle(96, 512, 26);
  g.fillCircle(132, 514, 22);
  g.fillCircle(70, 516, 18);

  // Little flowers dotted on the grass.
  const flowers = [[180, 700], [300, 692], [520, 702], [690, 694], [820, 702]];
  for (let i = 0; i < flowers.length; i++) {
    flower(g, flowers[i][0], flowers[i][1], letterColor((i * 5 + 3) % 26));
  }

  // Drifting clouds.
  addCloud(scene, 360, 84, 0.8, 18000);
  addCloud(scene, 690, 70, 0.6, 24000);
  addCloud(scene, 540, 120, 0.5, 30000);
}

function flower(g, x, y, color) {
  g.fillStyle(color, 1);
  for (let i = 0; i < 5; i++) {
    const a = (i / 5) * Math.PI * 2;
    g.fillCircle(x + Math.cos(a) * 8, y + Math.sin(a) * 8, 6);
  }
  g.fillStyle(0xfff3b0, 1);
  g.fillCircle(x, y, 5);
}

function addCloud(scene, x, y, scale, duration) {
  const c = scene.add.image(x, y, "cloud").setScale(scale).setAlpha(0.95).setDepth(0);
  scene.tweens.add({
    targets: c,
    x: x + 60,
    duration: duration,
    yoyo: true,
    repeat: -1,
    ease: "Sine.inOut",
  });
}

function drawFrame(scene) {
  const g = scene.add.graphics().setDepth(5);
  // Wooden frame drawn as a thick rounded border around the scene.
  g.lineStyle(34, 0xd6a85f, 1);
  g.strokeRoundedRect(17, 17, DESIGN_W - 34, DESIGN_H - 34, 30);
  g.lineStyle(34, 0xc6964c, 0.35);
  g.strokeRoundedRect(17, 17, DESIGN_W - 34, DESIGN_H - 34, 30);
  g.lineStyle(4, 0xb5853f, 1);
  g.strokeRoundedRect(34, 34, DESIGN_W - 68, DESIGN_H - 68, 20);

  // Corner screws.
  const screws = [[40, 40], [DESIGN_W - 40, 40], [40, DESIGN_H - 40], [DESIGN_W - 40, DESIGN_H - 40]];
  for (const [x, y] of screws) {
    g.fillStyle(0xead7ad, 1);
    g.fillCircle(x, y, 9);
    g.fillStyle(0xb5853f, 1);
    g.fillCircle(x, y, 4);
  }
}

function buildTitle(scene) {
  const cx = DESIGN_W / 2;
  const cy = 60;
  const g = scene.add.graphics().setDepth(40);
  g.fillStyle(0x000000, 0.12);
  g.fillRoundedRect(cx - 188, cy - 27, 376, 56, 18);
  g.fillStyle(0xfff6e0, 1);
  g.fillRoundedRect(cx - 190, cy - 30, 380, 56, 18);
  g.lineStyle(4, 0xe0b877, 1);
  g.strokeRoundedRect(cx - 190, cy - 30, 380, 56, 18);

  const title = scene.add.text(cx, cy - 2, "Magnet Alphabet", {
    fontFamily: FONT,
    fontSize: "34px",
    fontStyle: "800",
    color: "#e8643c",
  }).setOrigin(0.5).setDepth(41);
  title.setStroke("#ffffff", 5);
  title.setShadow(0, 3, "rgba(120,80,40,0.25)", 4);

  // Tiny colored dots beside the title.
  scene.add.circle(cx - 168, cy - 2, 6, letterColor(2)).setDepth(41);
  scene.add.circle(cx + 168, cy - 2, 6, letterColor(18)).setDepth(41);
}

// ---- Slots -----------------------------------------------------------------

function rowCount(row) {
  const remaining = LETTERS.length - row * COLS;
  return Math.min(COLS, remaining);
}

function slotPos(i) {
  const row = Math.floor(i / COLS);
  const col = i % COLS;
  const count = rowCount(row);
  const rowWidth = (count - 1) * SLOT_GAP_X;
  const x = DESIGN_W / 2 - rowWidth / 2 + col * SLOT_GAP_X;
  const y = SLOT_TOP + row * SLOT_GAP_Y;
  return { x, y };
}

function layoutSlots(scene) {
  for (let i = 0; i < LETTERS.length; i++) {
    const { x, y } = slotPos(i);
    const color = letterColor(i);
    const g = scene.add.graphics({ x, y }).setDepth(1);

    const label = scene.add.text(x, y, LETTERS[i], {
      fontFamily: FONT,
      fontSize: "34px",
      fontStyle: "800",
      color: "#7a5a2e",
    }).setOrigin(0.5).setAlpha(0.28).setDepth(1);

    const slot = { x, y, glow: g, label, baseColor: color };
    drawSocket(slot, false);
    slots.push(slot);
  }
}

function drawSocket(slot, on) {
  const g = slot.glow;
  g.clear();
  // Recessed cavity.
  g.fillStyle(0x000000, 0.10);
  g.fillCircle(0, 4, SLOT_R + 1);
  g.fillStyle(0xfaf1d8, 1);
  g.fillCircle(0, 0, SLOT_R);
  g.fillStyle(0x000000, 0.05);
  g.fillCircle(0, 2, SLOT_R - 4);
  if (on) {
    g.fillStyle(slot.baseColor, 0.22);
    g.fillCircle(0, 0, SLOT_R);
  }
  // Coloured rim.
  g.lineStyle(on ? 6 : 4, slot.baseColor, on ? 0.95 : 0.5);
  g.strokeCircle(0, 0, SLOT_R - 1);
}

// ---- Discs -----------------------------------------------------------------

function spawnDiscs(scene) {
  const order = Phaser.Utils.Array.Shuffle([...Array(LETTERS.length).keys()]);
  const perRow = 13;
  const trayLeft = 96;
  const trayRight = DESIGN_W - 96;
  const gapX = (trayRight - trayLeft) / (perRow - 1);

  order.forEach((idx, n) => {
    const col = n % perRow;
    const row = Math.floor(n / perRow);
    const x = trayLeft + col * gapX + Phaser.Math.Between(-2, 2);
    const y = TRAY_ROWS[row] + Phaser.Math.Between(-2, 2);
    discs.push(makeDisc(scene, idx, x, y));
  });
}

function makeDisc(scene, idx, x, y) {
  const color = letterColor(idx);
  const container = scene.add.container(x, y).setDepth(10);

  const img = scene.add.image(0, 0, "disc" + idx);
  img.setOrigin(0.5, DISC_CY / DISC_TEX_H);

  const text = scene.add.text(0, 0, LETTERS[idx], {
    fontFamily: FONT,
    fontSize: "30px",
    fontStyle: "800",
    color: "#ffffff",
  }).setOrigin(0.5);
  text.setStroke(rgbCss(Phaser.Display.Color.HSLToColor(hue(idx) / 360, 0.6, 0.3)), 4);
  text.setShadow(0, 2, "rgba(0,0,0,0.18)", 2);

  container.add([img, text]);

  return {
    idx,
    container,
    placed: false,
    color,
    baseX: x,
    baseY: y,
    phase: Math.random() * Math.PI * 2,
  };
}

function rgbCss(c) {
  return "rgb(" + c.r + "," + c.g + "," + c.b + ")";
}

// ---- Magnet ----------------------------------------------------------------

function buildMagnet(scene) {
  magnet = scene.add.container(DESIGN_W / 2, 360).setDepth(50);
  magnetGlow = scene.add.graphics();
  const img = scene.add.image(0, 0, "magnet");
  magnet.add([magnetGlow, img]);
  magnet.glow = magnetGlow;
}

const ANCHOR_DY = 30;
function anchorX(mx) { return mx; }
function anchorY(my) { return my + ANCHOR_DY; }

// ---- HUD -------------------------------------------------------------------

function buildHud(scene) {
  const x = DESIGN_W - 150;
  const y = 60;
  const g = scene.add.graphics().setDepth(40);
  g.fillStyle(0x000000, 0.12);
  g.fillRoundedRect(x - 2, y - 22, 120, 48, 24);
  g.fillStyle(0xfff6e0, 1);
  g.fillRoundedRect(x - 4, y - 25, 120, 48, 24);
  g.lineStyle(4, 0xe0b877, 1);
  g.strokeRoundedRect(x - 4, y - 25, 120, 48, 24);
  scene.add.circle(x + 18, y, 11, letterColor(8)).setDepth(41);
  scene.add.text(x + 18, y - 1, "A", {
    fontFamily: FONT, fontSize: "16px", fontStyle: "800", color: "#ffffff",
  }).setOrigin(0.5).setDepth(42);

  counterText = scene.add.text(x + 44, y - 1, "", {
    fontFamily: FONT,
    fontSize: "26px",
    fontStyle: "800",
    color: "#6b4f2a",
  }).setOrigin(0, 0.5).setDepth(42);
  updateCounter();
}

function updateCounter() {
  counterText.setText(placedCount + "/" + LETTERS.length);
}

// ---- Interaction -----------------------------------------------------------

function tryGrab(scene) {
  if (held) {
    return;
  }
  const ax = anchorX(magnet.x);
  const ay = anchorY(magnet.y);
  let best = null;
  let bestDist = GRAB_R;
  for (const d of discs) {
    if (d.placed) {
      continue;
    }
    const dist = Phaser.Math.Distance.Between(ax, ay, d.container.x, d.container.y);
    if (dist < bestDist) {
      bestDist = dist;
      best = d;
    }
  }
  if (best) {
    held = best;
    held.container.setDepth(45);
    scene.tweens.add({
      targets: held.container,
      scale: 1.16,
      duration: 140,
      ease: "Back.out",
      yoyo: true,
      hold: 0,
      onComplete: () => held && scene.tweens.add({ targets: held.container, scale: 1.1, duration: 80 }),
    });
    sparkle(scene, best.container.x, best.container.y, best.color);
  }
}

function release(scene, p) {
  if (!held) {
    return;
  }
  const slot = slots[held.idx];
  const ax = anchorX(p ? p.worldX : magnet.x);
  const ay = anchorY(p ? p.worldY : magnet.y);
  const dist = Phaser.Math.Distance.Between(slot.x, slot.y, ax, ay);
  const disc = held;
  held = null;

  if (dist < SNAP_R) {
    place(scene, disc, slot);
  } else {
    drawSocket(slot, false);
    disc.container.setDepth(10);
    scene.tweens.add({ targets: disc.container, scale: 1, duration: 150, ease: "Back.out" });
  }
}

function place(scene, disc, slot) {
  disc.placed = true;
  disc.container.setDepth(2);
  scene.tweens.add({
    targets: disc.container,
    x: slot.x,
    y: slot.y,
    scale: 1,
    duration: 200,
    ease: "Back.out",
    onComplete: () => {
      popSlot(scene, slot);
      scene.tweens.add({
        targets: disc.container, scale: 1.12, duration: 110, yoyo: true, ease: "Sine.inOut",
      });
    },
  });

  placedCount++;
  updateCounter();
  drawSocket(slot, false);
  sparkle(scene, slot.x, slot.y, slot.baseColor);

  if (placedCount === LETTERS.length) {
    scene.time.delayedCall(280, () => showWin(scene));
  }
}

function popSlot(scene, slot) {
  for (let k = 0; k < 2; k++) {
    const ring = scene.add.graphics({ x: slot.x, y: slot.y }).setDepth(3);
    ring.lineStyle(5, slot.baseColor, 1);
    ring.strokeCircle(0, 0, SLOT_R);
    scene.tweens.add({
      targets: ring,
      scale: 1.7 + k * 0.5,
      alpha: 0,
      duration: 420 + k * 120,
      ease: "Cubic.out",
      onComplete: () => ring.destroy(),
    });
  }
}

function sparkle(scene, x, y, color) {
  for (let i = 0; i < 7; i++) {
    const a = Math.random() * Math.PI * 2;
    const d = 18 + Math.random() * 22;
    const star = scene.add.star(x, y, 4, 2, 6, color).setDepth(48).setAngle(Math.random() * 90);
    scene.tweens.add({
      targets: star,
      x: x + Math.cos(a) * d,
      y: y + Math.sin(a) * d,
      scale: 0,
      alpha: 0,
      duration: 420,
      ease: "Cubic.out",
      onComplete: () => star.destroy(),
    });
  }
}

// ---- Main loop -------------------------------------------------------------

function update(time) {
  const p = this.input.activePointer;
  if (p && pointerSeen) {
    magnet.x = Phaser.Math.Linear(magnet.x, p.worldX, 0.5);
    magnet.y = Phaser.Math.Linear(magnet.y, p.worldY, 0.5);
  }

  magnet.glow.clear();
  if (magnetOn) {
    magnet.glow.fillStyle(0x7fc4ff, 0.14);
    magnet.glow.fillCircle(0, 26, GRAB_R);
    magnet.glow.fillStyle(0xffffff, 0.10);
    magnet.glow.fillCircle(0, 26, GRAB_R * 0.6);
  }

  const px = anchorX(magnet.x);
  const py = anchorY(magnet.y);

  if (held && magnetOn) {
    held.container.x = Phaser.Math.Linear(held.container.x, px, 0.35);
    held.container.y = Phaser.Math.Linear(held.container.y, py, 0.35);
    const slot = slots[held.idx];
    const near = Phaser.Math.Distance.Between(slot.x, slot.y, px, py) < SNAP_R;
    drawSocket(slot, near);
  }

  // Idle tray discs bob gently and feel a faint tug from a nearby magnet.
  for (const d of discs) {
    if (d.placed || d === held) {
      continue;
    }
    const c = d.container;
    const targetX = d.baseX;
    const targetY = d.baseY + Math.sin(time / 600 + d.phase) * 3;
    if (magnetOn) {
      const dist = Phaser.Math.Distance.Between(px, py, c.x, c.y);
      if (dist < FIELD_R && dist > 1) {
        const pull = (1 - dist / FIELD_R) * 0.06;
        c.x += (px - c.x) * pull;
        c.y += (py - c.y) * pull;
      }
    }
    c.x = Phaser.Math.Linear(c.x, targetX, 0.05);
    c.y = Phaser.Math.Linear(c.y, targetY, 0.08);
  }
}

// ---- Win + restart ---------------------------------------------------------

function showWin(scene) {
  burst(scene);

  winLayer = scene.add.container(0, 0).setDepth(100);
  const veil = scene.add.graphics();
  veil.fillStyle(0x2b2114, 0.5);
  veil.fillRect(0, 0, DESIGN_W, DESIGN_H);

  const cx = DESIGN_W / 2;
  const cy = DESIGN_H / 2;
  const card = scene.add.graphics();
  card.fillStyle(0x000000, 0.18);
  card.fillRoundedRect(cx - 250, cy - 116, 500, 236, 30);
  card.fillStyle(0xfff6e0, 1);
  card.fillRoundedRect(cx - 250, cy - 120, 500, 236, 30);
  card.lineStyle(6, 0xf2c879, 1);
  card.strokeRoundedRect(cx - 250, cy - 120, 500, 236, 30);

  const title = scene.add.text(cx, cy - 46, "You did it!", {
    fontFamily: FONT, fontSize: "60px", fontStyle: "800", color: "#e8643c",
  }).setOrigin(0.5);
  title.setStroke("#ffffff", 7);
  title.setShadow(0, 4, "rgba(120,80,40,0.25)", 5);

  const stars = scene.add.text(cx, cy + 6, "⭐ 🎉 ⭐", {
    fontFamily: FONT, fontSize: "34px",
  }).setOrigin(0.5);

  const btn = scene.add.graphics();
  btn.fillStyle(0x57b85a, 1);
  btn.fillRoundedRect(cx - 110, cy + 52, 220, 54, 27);
  btn.lineStyle(4, 0x3f9243, 1);
  btn.strokeRoundedRect(cx - 110, cy + 52, 220, 54, 27);
  const btnText = scene.add.text(cx, cy + 78, "Play again", {
    fontFamily: FONT, fontSize: "26px", fontStyle: "800", color: "#ffffff",
  }).setOrigin(0.5);
  btnText.setShadow(0, 2, "rgba(0,0,0,0.2)", 2);

  winLayer.add([veil, card, title, stars, btn, btnText]);
  scene.tweens.add({ targets: title, scale: 1.06, yoyo: true, repeat: -1, duration: 760, ease: "Sine.inOut" });
  scene.tweens.add({ targets: [btn, btnText], scale: 1.05, yoyo: true, repeat: -1, duration: 700, ease: "Sine.inOut" });
}

function burst(scene) {
  for (let i = 0; i < 120; i++) {
    const x = Phaser.Math.Between(80, DESIGN_W - 80);
    const color = letterColor(Phaser.Math.Between(0, 25));
    const piece = Math.random() < 0.5
      ? scene.add.rectangle(x, -20, 12, 12, color)
      : scene.add.star(x, -20, 5, 4, 9, color);
    piece.setDepth(99).setAngle(Phaser.Math.Between(0, 360));
    scene.tweens.add({
      targets: piece,
      y: DESIGN_H + 40,
      x: x + Phaser.Math.Between(-90, 90),
      angle: Phaser.Math.Between(180, 720),
      duration: Phaser.Math.Between(1500, 2900),
      delay: Phaser.Math.Between(0, 700),
      ease: "Cubic.in",
      onComplete: () => piece.destroy(),
    });
  }
}

function restart(scene) {
  held = null;
  magnetOn = false;
  placedCount = 0;
  discs.forEach((d) => d.container.destroy());
  slots.forEach((s) => {
    s.glow.destroy();
    s.label.destroy();
  });
  if (winLayer) {
    winLayer.destroy();
    winLayer = null;
  }
  discs = [];
  slots = [];

  layoutSlots(scene);
  spawnDiscs(scene);
  magnet.setDepth(50);
  updateCounter();
}
