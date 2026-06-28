# Magnet Alphabet

A pointer/touch toy inspired by a wooden magnetic letter board. Drag the magnet
near a colored letter disc to pull it from the tray, then drop it on the slot
with the matching letter. Fill all 26 slots to win, then tap to play again.

Built with [Phaser 3](https://phaser.io/). It has a soft, cartoony storybook
look: glossy candy-button letters, a wooden board with a sunny sky, drifting
clouds, rolling hills, sparkles on pickup, and a confetti win screen. Phaser and
the rounded Baloo 2 font are vendored under `vendor/`, so the whole thing runs
offline — no CDN or build step.

## Play

Open `index.html` in a browser, or serve the folder over HTTP:

```bash
cd game
python3 -m http.server 8099
# then visit http://localhost:8099/
```

Works with mouse, touch, or any pointer — press and hold to magnetize a disc,
move to carry it, release over its slot to lock it in.

## Play on your phone (same Wi-Fi)

Run the helper on a computer that's on the same Wi-Fi as the phone:

```bash
cd game
python3 serve.py        # or: python3 serve.py 9000 to pick a port
```

It prints a `http://<your-computer-ip>:<port>/` link — open that on the phone.
The phone must be on the same network (not a guest/isolated Wi-Fi), and the
computer's firewall has to allow the port. The plain
`python3 -m http.server 8099 --bind 0.0.0.0` works too; `serve.py` just detects
and prints the right address for you.

## How it feels magnetic

- A disc is grabbed if the magnet's pole comes within range, so you don't have
  to land exactly on it.
- A held disc trails the magnet with a springy lag, like it's stuck to the poles.
- Idle discs feel a faint tug when the magnet drifts past, then settle back.
- A matching slot glows while a held disc hovers over it, and the disc snaps and
  pops into place on release.

## Files

- `index.html` — page shell, loads Phaser and the game.
- `main.js` — all game logic (board, discs, magnet, win/restart).
- `style.css` — page backdrop, layout, and the board's drop shadow.
- `vendor/phaser.min.js` — Phaser 3.80.1 (MIT licensed).
- `vendor/fonts/baloo2-*.woff2` — Baloo 2 font (SIL Open Font License).
