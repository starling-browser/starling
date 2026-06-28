"use strict";

// Magnet Alphabet — a pointer/touch toy inspired by a wooden magnetic letter
// board. Drag the magnet near a letter disc to pick it up, then drop it on its
// matching slot. The pull is "magnetic": you don't need to be exactly on a disc
// to grab it, and a held disc hangs below the magnet with a springy lag.

const DESIGN_W = 960;
const DESIGN_H = 720;

const LETTERS = "ABCDEFGHIJKLMNOPQRSTUVWXYZ".split("");
const COLS = 7;

const DISC_R = 33;          // disc radius
const SLOT_R = 36;          // target slot radius
const GRAB_R = 95;          // how close the magnet pole must be to snatch a disc
const SNAP_R = 46;          // how close a held disc must be to lock into its slot
const FIELD_R = 150;        // range at which idle discs feel a gentle tug

// A bright, distinct colour per letter, evenly spread around the colour wheel.
function letterColor(i) {
  const hue = Math.round((i / LETTERS.length) * 360);
  return Phaser.Display.Color.HSLToColor(hue / 360, 0.62, 0.55).color;
}

const config = {
  type: Phaser.AUTO,
  parent: "game",
  width: DESIGN_W,
  height: DESIGN_H,
  backgroundColor: "#f4e4c1",
  scale: {
    mode: Phaser.Scale.FIT,
    autoCenter: Phaser.Scale.CENTER_BOTH,
  },
  scene: { create, update },
};

window.game = new Phaser.Game(config);

let discs = [];
let slots = [];
let held = null;            // the disc currently stuck to the magnet
let magnetOn = false;       // pointer is pressed
let magnet;                 // the magnet container
let magnetGlow;
let placedCount = 0;
let counterText;
let winLayer = null;

function create() {
  const scene = this;

  drawBoard(scene);
  layoutSlots(scene);
  spawnDiscs(scene);
  buildMagnet(scene);
  buildHud(scene);

  scene.input.on("pointerdown", (p) => {
    magnetOn = true;
    if (winLayer) {
      restart(scene);
      return;
    }
    // Snap the magnet to the press point so the grab measures from where the
    // pointer actually is, not from where the eased magnet is drifting toward.
    magnet.x = p.worldX;
    magnet.y = p.worldY;
    tryGrab();
  });

  scene.input.on("pointerup", (p) => {
    magnetOn = false;
    release(scene, p);
  });
}

// ---- Board / scenery -------------------------------------------------------

function drawBoard(scene) {
  const g = scene.add.graphics();

  // Wooden frame.
  g.fillStyle(0xcaa46a, 1);
  g.fillRoundedRect(8, 8, DESIGN_W - 16, DESIGN_H - 16, 26);
  g.fillStyle(0xe9d3a6, 1);
  g.fillRoundedRect(26, 26, DESIGN_W - 52, DESIGN_H - 52, 20);

  // Sky / play area.
  g.fillStyle(0xfbf3df, 1);
  g.fillRoundedRect(44, 44, DESIGN_W - 88, 470, 16);

  // Grass strip + tray below.
  g.fillStyle(0xbfe39a, 1);
  g.fillRoundedRect(44, 470, DESIGN_W - 88, DESIGN_H - 470 - 44, 16);
  g.fillStyle(0x9ed47e, 1);
  g.fillRoundedRect(44, 512, DESIGN_W - 88, DESIGN_H - 512 - 44, 16);

  // Tray lip the loose discs rest against.
  g.fillStyle(0xd8b87e, 1);
  g.fillRoundedRect(70, 506, DESIGN_W - 140, 12, 6);

  // A couple of leafy decorations, matching the toy's look.
  g.fillStyle(0x5fae5a, 1);
  g.fillCircle(96, 470, 26);
  g.fillCircle(132, 470, 22);
  g.fillStyle(0xe0633a, 1);
  g.fillRect(872, 250, 26, 230);
  g.fillStyle(0x5fae5a, 1);
  g.fillCircle(885, 250, 44);
  g.fillCircle(850, 270, 30);
  g.fillCircle(916, 272, 30);
}

function rowCount(row) {
  const remaining = LETTERS.length - row * COLS;
  return Math.min(COLS, remaining);
}

function layoutSlots(scene) {
  const rows = Math.ceil(LETTERS.length / COLS);
  const gapX = 116;
  const topY = 108;
  const gapY = 118;

  for (let i = 0; i < LETTERS.length; i++) {
    const row = Math.floor(i / COLS);
    const col = i % COLS;
    const count = rowCount(row);
    const rowWidth = (count - 1) * gapX;
    const x = DESIGN_W / 2 - rowWidth / 2 + col * gapX;
    const y = topY + row * gapY;

    const g = scene.add.graphics({ x, y });
    g.fillStyle(0x000000, 0.06);
    g.fillCircle(0, 4, SLOT_R);
    g.lineStyle(4, letterColor(i), 0.35);
    g.strokeCircle(0, 0, SLOT_R);

    const label = scene.add.text(x, y, LETTERS[i], {
      fontFamily: "Baloo 2, Comic Sans MS, sans-serif",
      fontSize: "34px",
      fontStyle: "bold",
      color: "#000000",
    });
    label.setOrigin(0.5).setAlpha(0.18);

    slots.push({ x, y, glow: g, baseColor: letterColor(i) });
  }
}

function spawnDiscs(scene) {
  const order = Phaser.Utils.Array.Shuffle([...Array(LETTERS.length).keys()]);
  const perRow = 13;
  const trayLeft = 92;
  const trayRight = DESIGN_W - 92;
  const gapX = (trayRight - trayLeft) / (perRow - 1);
  const rowY = [565, 632];

  order.forEach((idx, n) => {
    const col = n % perRow;
    const row = Math.floor(n / perRow);
    const x = trayLeft + col * gapX + Phaser.Math.Between(-3, 3);
    const y = rowY[row] + Phaser.Math.Between(-3, 3);
    discs.push(makeDisc(scene, idx, x, y));
  });
}

function makeDisc(scene, idx, x, y) {
  const color = letterColor(idx);
  const container = scene.add.container(x, y);

  const g = scene.add.graphics();
  g.fillStyle(0x000000, 0.18);
  g.fillCircle(0, 4, DISC_R);
  g.fillStyle(color, 1);
  g.fillCircle(0, 0, DISC_R);
  g.fillStyle(0xffffff, 0.22);
  g.fillCircle(-10, -11, DISC_R * 0.42); // glossy highlight

  const text = scene.add.text(0, 0, LETTERS[idx], {
    fontFamily: "Baloo 2, Comic Sans MS, sans-serif",
    fontSize: "32px",
    fontStyle: "bold",
    color: "#ffffff",
  });
  text.setOrigin(0.5);

  container.add([g, text]);
  container.setDepth(1);

  return { idx, container, placed: false, vx: 0, vy: 0, color };
}

// ---- Magnet ----------------------------------------------------------------

function buildMagnet(scene) {
  magnet = scene.add.container(DESIGN_W / 2, 360).setDepth(50);

  magnetGlow = scene.add.graphics();
  magnetGlow.fillStyle(0x7fc4ff, 0.0);
  magnetGlow.fillCircle(0, 26, GRAB_R);

  const m = scene.add.graphics();
  // Horseshoe body, opening downward toward the discs.
  m.lineStyle(11, 0x9aa3ad, 1);
  m.beginPath();
  m.moveTo(-15, 18);
  m.lineTo(-15, 0);
  m.arc(0, 0, 15, Math.PI, 0, false);
  m.lineTo(15, 18);
  m.strokePath();
  // Coloured pole tips.
  m.fillStyle(0xe23b3b, 1);
  m.fillRoundedRect(-21, 14, 12, 14, 3);
  m.fillStyle(0x2f6df6, 1);
  m.fillRoundedRect(9, 14, 12, 14, 3);

  magnet.add([magnetGlow, m]);
  magnet.glow = magnetGlow;
}

// Where a held disc sits relative to a magnet at (mx, my): just below the
// opening so it reads as "stuck to the poles". Grab and snap both measure from
// this same anchor so picking up and dropping line up with what you see.
const ANCHOR_DY = 30;
function anchorX(mx) { return mx; }
function anchorY(my) { return my + ANCHOR_DY; }

// ---- HUD -------------------------------------------------------------------

function buildHud(scene) {
  counterText = scene.add.text(DESIGN_W - 60, 60, "", {
    fontFamily: "Baloo 2, Comic Sans MS, sans-serif",
    fontSize: "28px",
    fontStyle: "bold",
    color: "#6b5733",
  }).setOrigin(1, 0.5).setDepth(60);
  updateCounter();
}

function updateCounter() {
  counterText.setText(placedCount + " / " + LETTERS.length);
}

// ---- Interaction -----------------------------------------------------------

function tryGrab() {
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
    held.container.setDepth(40);
    held.container.scene.tweens.add({
      targets: held.container,
      scale: 1.08,
      duration: 120,
      ease: "Back.out",
    });
  }
}

function release(scene, p) {
  if (!held) {
    return;
  }
  const slot = slots[held.idx];
  // Measure the drop from where the magnet is aimed, not the laggy disc, so
  // releasing over a slot snaps even while the disc is still catching up.
  const ax = anchorX(p ? p.worldX : magnet.x);
  const ay = anchorY(p ? p.worldY : magnet.y);
  const dist = Phaser.Math.Distance.Between(slot.x, slot.y, ax, ay);
  const disc = held;
  held = null;

  if (dist < SNAP_R) {
    place(scene, disc, slot);
  } else {
    clearGlow(slot);
    disc.container.setDepth(1);
    scene.tweens.add({ targets: disc.container, scale: 1, duration: 150, ease: "Sine.out" });
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
    duration: 160,
    ease: "Back.out",
    onComplete: () => popSlot(scene, slot),
  });

  placedCount++;
  updateCounter();
  clearGlow(slot);

  if (placedCount === LETTERS.length) {
    scene.time.delayedCall(260, () => showWin(scene));
  }
}

function popSlot(scene, slot) {
  const ring = scene.add.graphics({ x: slot.x, y: slot.y }).setDepth(3);
  ring.lineStyle(5, slot.baseColor, 1);
  ring.strokeCircle(0, 0, SLOT_R);
  scene.tweens.add({
    targets: ring,
    scale: 1.9,
    alpha: 0,
    duration: 380,
    ease: "Cubic.out",
    onComplete: () => ring.destroy(),
  });
}

function clearGlow(slot) {
  slot.glow.clear();
  slot.glow.fillStyle(0x000000, 0.06);
  slot.glow.fillCircle(0, 4, SLOT_R);
  slot.glow.lineStyle(4, slot.baseColor, 0.35);
  slot.glow.strokeCircle(0, 0, SLOT_R);
}

function highlightSlot(slot, on) {
  slot.glow.clear();
  slot.glow.fillStyle(0x000000, 0.06);
  slot.glow.fillCircle(0, 4, SLOT_R);
  if (on) {
    slot.glow.fillStyle(slot.baseColor, 0.22);
    slot.glow.fillCircle(0, 0, SLOT_R);
    slot.glow.lineStyle(5, slot.baseColor, 0.9);
  } else {
    slot.glow.lineStyle(4, slot.baseColor, 0.35);
  }
  slot.glow.strokeCircle(0, 0, SLOT_R);
}

// ---- Main loop -------------------------------------------------------------

function update(time, delta) {
  const p = this.input.activePointer;
  if (p) {
    magnet.x = Phaser.Math.Linear(magnet.x, p.worldX, 0.5);
    magnet.y = Phaser.Math.Linear(magnet.y, p.worldY, 0.5);
  }

  // Magnet field visual.
  magnet.glow.clear();
  if (magnetOn) {
    magnet.glow.fillStyle(0x7fc4ff, 0.12);
    magnet.glow.fillCircle(0, 24, GRAB_R);
  }

  const px = anchorX(magnet.x);
  const py = anchorY(magnet.y);

  if (held && magnetOn) {
    // Held disc eases toward the anchor with a springy lag.
    held.container.x = Phaser.Math.Linear(held.container.x, px, 0.35);
    held.container.y = Phaser.Math.Linear(held.container.y, py, 0.35);

    const slot = slots[held.idx];
    const near = Phaser.Math.Distance.Between(slot.x, slot.y, px, py) < SNAP_R;
    highlightSlot(slot, near);
  }

  // Idle discs feel a faint tug when the active magnet drifts near, then ease
  // back to rest — a bit of magnetic "life" without disturbing placement.
  for (const d of discs) {
    if (d.placed || d === held) {
      continue;
    }
    const c = d.container;
    if (!d.homeX) {
      d.homeX = c.x;
      d.homeY = c.y;
    }
    if (magnetOn) {
      const dist = Phaser.Math.Distance.Between(px, py, c.x, c.y);
      if (dist < FIELD_R && dist > 1) {
        const pull = (1 - dist / FIELD_R) * 0.06;
        c.x += (px - c.x) * pull;
        c.y += (py - c.y) * pull;
      }
    }
    c.x = Phaser.Math.Linear(c.x, d.homeX, 0.04);
    c.y = Phaser.Math.Linear(c.y, d.homeY, 0.04);
  }
}

// ---- Win + restart ---------------------------------------------------------

function showWin(scene) {
  burst(scene);

  winLayer = scene.add.container(0, 0).setDepth(100);
  const veil = scene.add.graphics();
  veil.fillStyle(0x2b2114, 0.55);
  veil.fillRect(0, 0, DESIGN_W, DESIGN_H);

  const title = scene.add.text(DESIGN_W / 2, DESIGN_H / 2 - 24, "You did it! 🎉", {
    fontFamily: "Baloo 2, Comic Sans MS, sans-serif",
    fontSize: "64px",
    fontStyle: "bold",
    color: "#ffffff",
  }).setOrigin(0.5);

  const sub = scene.add.text(DESIGN_W / 2, DESIGN_H / 2 + 44, "Tap to play again", {
    fontFamily: "Baloo 2, Comic Sans MS, sans-serif",
    fontSize: "28px",
    color: "#ffe9b8",
  }).setOrigin(0.5);

  winLayer.add([veil, title, sub]);
  scene.tweens.add({ targets: sub, alpha: 0.3, yoyo: true, repeat: -1, duration: 700 });
}

function burst(scene) {
  for (let i = 0; i < 90; i++) {
    const x = Phaser.Math.Between(120, DESIGN_W - 120);
    const piece = scene.add.graphics({ x, y: -20 }).setDepth(99);
    piece.fillStyle(letterColor(Phaser.Math.Between(0, 25)), 1);
    piece.fillRect(-5, -5, 10, 10);
    scene.tweens.add({
      targets: piece,
      y: DESIGN_H + 40,
      x: x + Phaser.Math.Between(-80, 80),
      angle: Phaser.Math.Between(180, 540),
      duration: Phaser.Math.Between(1400, 2600),
      delay: Phaser.Math.Between(0, 500),
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
  slots.forEach((s) => s.glow.destroy());
  scene.children.list
    .filter((c) => c.depth >= 60 && c !== magnet && c !== counterText)
    .forEach((c) => c.destroy());
  if (winLayer) {
    winLayer.destroy();
    winLayer = null;
  }
  discs = [];
  slots = [];

  layoutSlots(scene);
  spawnDiscs(scene);
  magnet.setDepth(50);
  counterText.setDepth(60);
  updateCounter();
}
