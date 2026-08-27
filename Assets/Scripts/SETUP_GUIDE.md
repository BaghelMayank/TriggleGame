# Triggle — Scene Setup Guide

Unity 2022.3 LTS · URP · Legacy Input Manager · TextMeshPro

Everything below works with **zero imported art or audio assets**: pegs, tokens, bands, particles and
sound effects all have procedural fallbacks. Assign prefabs/clips later to replace them.

---

## 0. One-click setup (recommended)

**`Tools ▸ Triggle ▸ Build Play Scene`**

This generates the entire playable scene — assets, hierarchy, components and every wired reference —
and saves it to `Assets/Scenes/Triggle.unity`. Press **Play** immediately afterwards; nothing else is
required.

It creates:

| Asset | Path |
|---|---|
| Player palette | `Assets/Settings/Triggle/PlayerColorPalette.asset` |
| Fonts (TTF + OFL licences) | `Assets/Fonts/Triggle/` |
| Generated TMP font assets | `Assets/Fonts/Triggle/Generated/` |
| Materials (peg, token, band, preview, slab, rim, lines, socket, cell fill, burst) | `Assets/Materials/Triggle/` |
| Menu gradient texture | `Assets/Textures/Triggle/T_MenuGradient.png` |
| Peg prefab | `Assets/Prefabs/Triggle/Peg.prefab` |
| Claim token prefab | `Assets/Prefabs/Triggle/ClaimToken.prefab` |
| Claim burst prefab | `Assets/Prefabs/Triggle/ClaimBurst.prefab` |
| Token mesh | `Assets/Meshes/Triggle/ClaimTokenMesh.asset` |

Notes:

- **Existing assets are reused, never duplicated** — safe to re-run. If `Triggle.unity` already
  exists it asks whether to overwrite or save under a new name; it never overwrites silently.
- `Tools ▸ Triggle ▸ Create Assets Only` generates the assets without touching your scene.
- Board radius, player count and turn mode are constants at the top of
  `Assets/Scripts/Editor/TriggleSceneBuilder.cs` (`BoardRadius`, and the `settings.*` block in
  `BuildHierarchy`). Camera framing is not baked — `BoardCameraRig` fits the board at runtime, so the
  radius the player picks in Settings reframes automatically. See §1.
- The builder writes private `[SerializeField]` fields through `SerializedObject`. If you rename a
  field later, it logs a warning naming the unresolved property instead of producing a quietly
  half-wired scene.

Sections 1–6 below describe the same setup **by hand**, if you would rather build it yourself or need
to understand what the generator produced. Sections 7–10 are worth reading either way.

---

## 1. Create the scene

`File ▸ New Scene ▸ Basic (URP)`, then save as `Assets/Scenes/Triggle.unity`.

### Camera

Select **Main Camera** and set:

| Property | Value |
|---|---|
| Position | `(0, 13, -8.5)` |
| Rotation | `(56, 0, 0)` |
| Projection | Perspective, FOV `60` |
| Clear Flags / Background | Solid colour, something dark (`#141821`) |

That frames a radius-3 board with `pegSpacing = 1` — but it is only a **starting pose**, so the saved
scene looks right in the Scene view. Add **`BoardCameraRig`** to the camera and it recomputes the
position at runtime.

This matters because board size is a runtime choice (Settings ▸ Board ▸ Board Size, radius 3–5). A
baked position framed for radius 3 clips a radius 4 or 5 board off the edges of the screen. The rig
re-fits whenever the lattice is regenerated, and again whenever the window is resized, so the board
also survives a portrait phone aspect.

| Field | Default | Effect |
|---|---|---|
| Board / Board Visuals | auto-found | `BoardVisuals` supplies the slab radius, so the fit includes the slab and its rim, not just the pegs. |
| Pitch | `56` | Downward tilt. Shared with the scene generator's starting pose. |
| Side Padding | `0.04` | Viewport fraction left empty at the left and right edges. |
| Top / Bottom Margin | `0.145` / `0.143` | Viewport reserved for HUD chrome. Wired by the scene builder from the HUD's own geometry. |
| Content Height | `0.5` | How far the tallest thing on the board reaches — a peg post plus its head. |
| Track Viewport Changes | on | Re-fit on window resize. One integer compare per frame. |

**Fitting the distance.** Moving the camera back along its own forward axis leaves every point's
camera-space `x` and `y` alone and adds exactly that distance to `z`. A point is inside the band when
`|x| <= z·tanH·side` and `bottom <= y/(z·tanV) <= top`, so each constraint solves directly for the
distance it needs and the answer is the largest of them — no search, correct for any pitch or aspect.

**Centring between the HUD bars.** The board is fitted into the strip *between* the top chrome and the
turn banner, with the same gap at each end, rather than centred in the whole screen. Centring in the
whole screen looked wrong: a camera pitched 56° projects the near edge of the board further from centre
than the far edge, so a symmetric fit is driven by the near edge and leaves a wide gap under the TRIGGLE
chip while the turn banner sits right on the board.

The rise that equalises the two gaps is **solved, not nudged**. A camera shift moves every point's
camera-space `y` equally but its *viewport* position by `1/depth` of that, so the near edge travels much
further up the screen than the far edge; correcting by an averaged depth overshoots. Depth does not
change when the camera moves along its up axis, so for a fixed pair of extremes it is a linear equation
with a closed-form answer.

**What the board is, for fitting purposes:** the slab hexagon at ground level, plus a *smaller* hexagon
at peg-top height. Two radii, because nothing tall stands at the slab's rim — the outermost pegs are a
slab-padding further in. Lifting the rim corners invents headroom no geometry occupies, and the fit then
reserves screen space for it; that alone accounted for a 3.5% skew between the gaps.

Six points per ring rather than a ring of circle samples: a circle through a hexagon's corners bulges up
to 13% past its flat sides, and fitting to one threw that much screen away. Perspective maps a convex
polygon to a convex polygon with vertices going to vertices, so twelve points bound the board exactly.

Net effect: the board occupies **0.709** of screen height, up from 0.496 before this was reworked.

### Light

Keep the default **Directional Light**; rotate it to `(50, -30, 0)` so the peg posts and tokens cast
readable shadows.

---

## 2. Build the object hierarchy

Create these empty GameObjects at the scene root (`GameObject ▸ Create Empty`) and add the listed
components (`Add Component`, search by class name):

```
Triggle (scene)
├── Main Camera          → BoardCameraRig
├── Directional Light
├── Board                → BoardManager
├── GameSystems          → GameFlowController, ScoreManager, SoundManager, MatchController, AiController
├── Interaction          → InputController, BandPlacementPreview
├── Visuals              → RubberBandRenderer, TokenSpawner, PegHighlighter
└── UI (Canvas)          → GameUIController
```

Leave `Board` at position `(0, 0, 0)` — the lattice is generated around its origin, and moving the
transform moves the whole board.

> **Wiring note.** Every component resolves its dependencies through `FindObjectOfType` in `Awake`,
> so the references above fill themselves in when there is exactly one of each in the scene. The
> inspector fields exist for explicit wiring and for scenes with more than one board.

---

## 3. Configure the board

Select **Board** and set `BoardManager`:

| Field | Suggested | Notes |
|---|---|---|
| Radius | `3` | `4` for a longer match. Minimum `2`. |
| Peg Spacing | `1` | World edge length becomes `√3 × spacing ≈ 1.73`. |
| Build On Awake | **off** | Leave off — `GameFlowController` builds the board in `Start` so every listener has subscribed first. |
| Peg Prefab | *(empty)* | Generates a sphere-and-post peg at runtime. |
| Peg Scale / Peg Height | `0.3` / `0.35` | |
| Draw Gizmos | on | See §7. |

Board size reference:

| Radius | Pegs | Edges | Triangles | Band slots (4-peg) |
|---|---|---|---|---|
| 2 | 19 | 42 | 24 | 12 — *degenerate, see §8* |
| 3 | 37 | 90 | 54 | 48 |
| 4 | 61 | 156 | 96 | 102 |
| 5 | 91 | 240 | 150 | 174 |

---

## 4. Create the palette asset

`Assets ▸ Create ▸ Triggle ▸ Player Color Palette` → save as
`Assets/Settings/PlayerColorPalette.asset`.

It ships with four seats configured (Crimson / Azure / Verdant / Amber). Drag the asset into the
`Palette` field of **RubberBandRenderer**, **TokenSpawner** and **GameUIController**.

Leave `Token Material` / `Band Material` empty on each seat and the palette generates tinted
URP materials at runtime. Assign your own materials to override.

---

## 5. Set the rules

Select **GameSystems** and open `GameFlowController ▸ Rules`:

| Field | Default | Effect |
|---|---|---|
| Player Count | `2` | 2–4 seats. |
| *(no turn-flow setting)* | — | The turn **always** passes after a placement, scoring or not. See §8. |
| Require At Least One New Edge | `on` | Blocks bands that would add no uncovered segment. Keep on: it stops wasted turns and guarantees the match terminates. |
| Band Placement Duration / Claim Resolve Delay / Turn Handover Delay | `0.28` / `0.12` / `0.15` | Animation pacing, in seconds. |
| Verbose Logging | off | Turn on to trace every transition and claim in the Console. |

**Band length lives on the board, not here.** How many pegs a band spans drives lattice generation, so
it is a single field on `BoardManager` rather than a duplicated rules setting — `MoveValidator` reads
`BoardManager.PegsPerBand` directly:

| Field (on `BoardManager`) | Default | Effect |
|---|---|---|
| Pegs Per Band | `4` | Collinear pegs the player must click; the band covers `n − 1` edges. `3` gives shorter bands (69 at R=3), `5` longer ones — but mind the radius constraint in §8. |

---

## 6. Build the UI

The generated scene has one `Canvas` (`Screen Space - Overlay`, `Scale With Screen Size`,
reference `1920 × 1080`, match `0.5`) holding seven full-screen panels, each with its own
`CanvasGroup` so it can be cross-faded independently:

```
UI (Canvas)  → MainMenuController, LobbyController, SettingsPanelController, GameUIController
├── HUD                 4 corner player cards, TRIGGLE chip, round chip, pause, turn banner, toast
├── RoundSummaryPanel   between rounds: who won it, standings, NEXT ROUND
├── MatchPanel          final result: Win / Lose / Match Tied, standings, REMATCH, MAIN MENU
├── RootMenu            neon title + Play Local / Play vs AI / How to Play / Settings / Quit
├── LobbyPanel          player count, per-seat avatar + name + colour + HUMAN/CPU, rounds and
│                       difficulty steppers, START GAME
├── HowToPlayPanel      the rules, generated from the live band length
└── SettingsPanel       Audio + Board tabs
```

Sibling order is the draw order — HUD at the back. **All four controllers live on the Canvas root,
not inside their panels**, so they keep receiving events and running coroutines while hidden.

### Fitting the screen (landscape only)

The build is **landscape-only** — run `Tools ▸ Triggle ▸ Configure Player Settings (Landscape)` once to
apply it. That decision does most of the responsive work by itself: every panel is authored at a
1080-unit height for a wide screen, so with the canvas scaler set to **match height** the authored
design fills the screen exactly on everything from a 4:3 monitor to a 21:9 phone. The surplus shows up
as width, never as a crop.

The scaler used to blend at `matchWidthOrHeight = 0.5`, which is only correct at exactly 16:9. On a
2340×1080 phone that produced a canvas only 978 units tall, so the 940-tall lobby card had nowhere to go
and the tall panels were clipped top and bottom.

**`CanvasSafeArea`** insets each panel's content past the notch and gesture bar, in normalised anchors
so it is correct at any resolution. Held sideways, a phone's cutout eats into the *side* of the screen —
which is exactly where the HUD's corner player cards and the pause button live. Every panel is two rects:

```
LobbyPanel          full screen: CanvasGroup + Scrim
└── SafeArea        CanvasSafeArea — all content lives here
    └── Card ...
```

> **The scrim is pinned to sibling index 0.** uGUI draws in hierarchy order and hit-tests in reverse, so
> a full-screen raycast target sitting *after* the content covers the panel and eats every tap — buttons
> render perfectly and nothing responds. `AddScrim` forces it to the back so no future change to build
> order can reintroduce that.

Only one element was authored wider than the narrowest landscape canvas (1350 units at 5:4): the HUD
status toast, which now stretches with an 80-unit margin instead of carrying a fixed 1400 width.

### Frosted glass (glassmorphism)

Panels use `Assets/Shaders/Triggle/TriggleUIGlass.shader`, which samples the scene behind the UI and
blurs it — real frosted glass, not a flat translucent fill. Two things must be true for it to work,
and the builder sets both automatically:

1. **Opaque Texture enabled** on the active URP Asset, or `_CameraOpaqueTexture` is empty.
   `Tools ▸ Triggle ▸ Enable Glass UI (URP Opaque Texture)` does this on demand.
2. **Canvas render mode is Screen Space – Camera.** A Screen Space – Overlay canvas is composited
   *outside* the camera render loop and cannot read scene textures at all. The builder switches the
   canvas to Camera mode (plane distance 1.2, sorting order 100) when the glass shader is present, and
   falls back to Overlay when it isn't.

Three material tiers, in `Assets/Materials/Triggle/`:

| Material | Blur | Used for |
|---|---|---|
| `M_UIGlassBackdrop` | 40px, heavy tint | Full-screen backdrop behind menus — turns the board into an unreadable wash |
| `M_UIGlassPanel` | 20px | Cards, where text has to stay legible |
| `M_UIGlassControl` | 10px, light tint | Buttons and chips, so the board still reads through them in-game |

**Known limit:** `_CameraOpaqueTexture` contains *opaque geometry only*. The board slab, pegs, lattice
lines and sockets blur correctly; rubber bands and claimed-cell fills are in the transparent queue and
so do not appear in the blur. That's why every glass material carries a tint rather than relying on
the backdrop alone.

If the shader fails to compile on your platform the declared `Fallback` keeps panels readable instead
of rendering magenta — check the Console for shader errors after the first build.

### Pause

`PausePanelController` owns the HUD pause button. It offers **Resume / Restart Match / Settings /
Main Menu**, with Main Menu behind a confirmation step, and `Escape` toggles it while a match is
running. Pausing calls `GameFlowController.SetPaused(true)`, which refuses board input but leaves the
match state and any in-flight resolve animation untouched, so resuming carries straight on.

### The neon look

There is no custom shader. Each control is composed from three generated 9-slice sprites
(`Tools ▸ Triggle ▸ Rebuild UI Sprites`):

1. a soft outer **glow**, inset negatively so it bleeds past the control
2. two **outline** copies offset ±2.5px in opposite directions, tinted cyan and coral — this is what
   produces the two-tone rim light
3. a translucent **fill** on top, which is also the click target

Sprites are drawn from a signed distance field at 96×96 with a 47px 9-slice border, so corners stay
crisp at any size. `UIButtonFeedback` adds hover lift, press-in and the click sound.

### Choosing who plays each seat

`Play Local` and `Play vs AI` both open the same lobby — they only pick a different starting lineup.
Local sets every seat to a person; vs AI sets seat 1 to a person and every other seat to the computer.
Each seat row then has a **HUMAN / CPU** toggle, so a three-player game can be you, a friend and one
computer opponent without going back to the root menu.

Seat 1 is the human seat in the vs-AI lineup specifically because `MatchController.localPlayer` writes
the Win / Lose panel from that seat's point of view. Handing seat 1 to the computer is allowed — every
seat can be a CPU, which makes a usable attract mode — but the result panel then reads from the
computer's side. `Escape` still opens the pause menu in that case, because pausing is gated on the
match running rather than on board input.

A computer seat's name field is emptied and locked: it is named after its colour with a `(CPU)` tag in
the HUD and standings, so you can tell at a glance which score is yours. A name typed there before the
seat was handed to the CPU is kept, not overwritten, and comes back if you toggle the seat to human.

### Player colours and avatars

Each lobby row has four colour swatches. Picking a colour another seat holds **swaps the two**
(`PlayerProfiles.SetColorIndex`), so colours are always unique and no seat is left without one.
`PlayerColorPalette.GetColor` reads the choice, and its material cache is keyed by colour *slot* — not
by seat — so a swap can't leave a stale material behind.

Avatars are four generated geometric emblems (triangle, diamond, hexagon, chevron), drawn white and
tinted per player. They stand in for character portraits; drop real sprites into the seat rows and HUD
cards to replace them.

### Fonts

Three typefaces are bundled as TTFs under `Assets/Fonts/Triggle`, chosen to suit a geometric board
game. `TriggleFontSetup` converts them into TextMeshPro font assets in
`Assets/Fonts/Triggle/Generated`:

| Role | Typeface | Used for |
|---|---|---|
| Display | **Archivo Black** | Title, winner banner, PLAY |
| Heading | **Chakra Petch Bold** | Turn label, scores, standings, score popups |
| Body | **Poppins SemiBold** | Buttons, player names, input fields |
| Body (light) | **Poppins Medium** | Round counter, hints, subtitles, version |

Chakra Petch is the deliberate pick for headings — its chamfered, angular letterforms echo the
triangular lattice, and its numerals read cleanly at scoreboard size.

**Licensing.** All three are under the SIL Open Font License, which permits commercial use and
redistribution inside an app. The license text sits beside the TTFs as `LICENSE-*-OFL.txt` — keep
those files in the project.

Notes:

- Font assets use TMP's **Dynamic** atlas mode, so glyphs rasterise on demand and every character
  stays available at any size. That requires the source TTF in the build, so the importer's
  `includeFontData` flag is forced on automatically.
- `Tools ▸ Triggle ▸ Rebuild Font Assets` regenerates them (e.g. after swapping a TTF).
- If the TTFs are missing, the builder falls back to TMP's bundled example fonts, then to
  `TMP_Settings.defaultFontAsset` — the build always succeeds, and the console logs which fonts it
  actually resolved.

### Main menu

`MainMenuController` handles player count (2/3/4), name entry and the transition into the match:

- The selected count button is tinted with the accent colour; exactly that many name rows are shown.
- Each row has a colour chip, a `Player N` label and a `TMP_InputField` whose **placeholder** is the
  palette's default name — so leaving a field blank is clearly optional, not broken.
- Names are saved by `PlayerProfiles` via `PlayerPrefs` and **pre-filled on the next launch**.
- `PLAY` commits the names, calls `ConfigurePlayerCount` then `StartNewGame`, and cross-fades to the HUD.

`GameFlowController.autoStart` is **off** in the generated scene, because the menu starts the match.
The board is still generated at `Start`, so the lattice sits behind the menu as a backdrop.

### Interactivity

`UIButtonFeedback` is attached to every button and input field: it lifts on hover, presses in on
click, brightens the target graphic, and raises `GameEvents.OnUiClick` so `SoundManager` plays a click
without needing a direct reference. `UITween` provides the panel fades (fade + rise) and the scale
punches on turn changes and score updates.

Every serialized field on both controllers is optional — leave any of them empty and that widget is
simply skipped, which keeps them usable for custom layouts.

---

## 6b. Board presentation and score VFX

Three components under `Visuals/` handle how the board and scoring read on screen. All geometry is
generated procedurally from the board data, so it tracks any radius or spacing change.

### `BoardVisuals` (on the **Board** object)

Replaces the old placeholder ground plane with:

- a **bevelled hexagonal slab** whose corners align with the lattice's own hexagonal outline
- a thin **accent rim** in the same colour as the UI accent
- **lattice lines** — a thin quad along every unit edge, all in one mesh so the whole grid is a single
  draw call
- a dark **socket disc** under every peg, so the posts read as seated in the board

The lattice lines are not decoration. Without them the player has to infer the grid from peg positions
alone, which makes it genuinely hard to see which triangles are one edge away from closing. The slab
also carries the `MeshCollider` that makes "click the board to cancel" work.

### `CellFillRenderer` (on **Visuals**)

Fills each claimed triangle with a translucent, player-coloured plate that pops in with an overshoot
and a short spin. This is what lets you read territory across the whole board at a glance — the token
alone only marks a point.

Only **two meshes** are ever built: every up-pointing unit triangle is congruent with every other, and
likewise for down-pointing ones. Vertices are stored relative to the centroid in a canonical
angle-sorted order, which is what makes that sharing valid (verified: all 96 cells at R=4 match their
shared mesh to within 1e-15).

### `ClaimVfx` (on **Visuals**)

The scoring moment:

- an expanding **shockwave ring** on the claimed triangle, fading as it grows
- a floating **"+1" popup** that rises, scales in and fades, billboarded to the camera
- a small **camera kick**

Claims are counted per band placement, so a move that closes several triangles escalates: popups read
`+1 x2`, `+1 x3`, grow with the combo, and the camera kick scales with it (capped at 4). The kick
always returns to a cached base position, so repeated kicks can never accumulate drift.

> **Transparency note.** URP's Unlit and Lit shaders — and Built-in's Standard — default to *opaque*,
> so writing an alpha into a colour does nothing on its own. `MaterialUtility.MakeTransparent` sets the
> surface type, blend factors, ZWrite and render queue together, and every translucent visual (cell
> fills, preview ghost, shockwave, particles) routes through it.

---

## 6c. The computer opponent

Three scripts, none of which the board knows about:

| Script | Role |
|---|---|
| `Core/SeatRoster.cs` | Which seats are CPU, and the match-wide difficulty. Persisted in `PlayerPrefs`. |
| `Gameplay/BandEvaluator.cs` | Picks a band. Pure C# — no scene, no coroutines. |
| `Gameplay/AiController.cs` | Waits, reveals the picks, and submits the move. |

`AiController` plays through `GameFlowController.SubmitBandSelection` — the same entry point the mouse
uses. The AI is therefore held to exactly the same rules as you, and a move the validator would refuse
surfaces as a logged error rather than as a quietly illegal board.

Board input is closed during a computer turn by `GameFlowController.AcceptsInput`, which now also
returns false when the seat on the clock is a CPU. The **phase** is deliberately left alone, because
`SubmitBandSelection` gates on the phase — so the pointer is locked out while the AI can still play.

The move is revealed one peg at a time through `GameEvents.OnSelectionChanged`, the same event the
human selection raises, so the peg highlighting you already know shows the computer building its band
instead of a band simply appearing.

### How it decides

A triangle's three edges each run in a different lattice direction, so one straight band can only ever
cover one of them (§8). Every triangle needs three separate bands, and the third one scores. Triggle is
therefore a game about **who is forced to play the second edge**: covering a triangle's second edge
leaves it one edge from closing, and the next player takes it for free.

So for each legal band the evaluator counts what it would do:

- **gain** — triangles it closes now
- **gifts** — triangles it leaves with exactly one edge open, for the opponent
- **setups** — triangles it leaves two edges from closing

| Difficulty | Method |
|---|---|
| Easy | Plays at random 60% of the time; runs the analysis below otherwise, so it still takes a triangle sitting in front of it rather than looking broken. |
| Normal | One ply: `10·gain − 3·gifts − 0.4·setups`. |
| Hard | Re-scores the ten strongest one-ply candidates by the opponent's best possible reply: `10·gain − 9·reply − 0.5·gifts`. |

Taking is weighted slightly above conceding on purpose. Late in a round *every* remaining move gives
something away, and an AI that valued the two equally would dither instead of cashing in.

Candidates are simulated **without mutating the board**: a scratch set of "virtually covered" edges is
layered over the real occupancy and every query consults both. Nothing to undo, so an exception
unwinding mid-search cannot leave the live board corrupted.

### Verification

`Tools ▸ Triggle ▸ Verify AI (self-play)` plays whole rounds headless — no scene, no coroutines — and
prints the table below. Run it after changing any weight in `BandEvaluator`.

At R=3, 60 games per matchup, seats swapped every other game. Tie-breaks are randomised, so the exact
figures move a little between runs — a second run gave Hard 52 / 6 / 2:

| Matchup | Wins | Draws | Losses | Avg triangles (of 54) |
|---|---|---|---|---|
| Normal vs Random | 60 | 0 | 0 | 41.9 |
| Normal vs Easy | 60 | 0 | 0 | 35.0 |
| Hard vs Normal | 49 | 8 | 3 | 29.0 |

Zero illegal moves across roughly 7,000 chosen bands — every one was re-checked against the real
`MoveValidator` — and zero rounds ended with a triangle unclaimed.

The ordering is the point. A difficulty setting that does not change the outcome is worse than no
setting at all: the player changes it, loses anyway, and concludes the game is broken.

---

## 6d. Multiplayer spine

Three files under `Assets/Scripts/Net/`, plus one method on the flow controller. No packages, no service
account, no sockets yet — this is the layer everything else will sit on.

**A turn is one integer.** `BoardManager` enumerates every legal band once at build time, so a move is
an index into that catalogue rather than four peg coordinates. The catalogue is a pure function of
radius and band length, generated in a fixed order, so index 47 is the same three edges on every device.
Both sides then run their own deterministic rules engine over the same index sequence — there is no
state replication, no authority server and nothing to reconcile. **19 bytes per move.**

| File | Role |
|---|---|
| `NetMessage.cs` | The wire format. Six message kinds, hand-serialised so the transport can be swapped without touching the protocol. |
| `ISessionTransport.cs` | The seam. "Ship these bytes, tell me when bytes arrive" — and nothing else. |
| `LoopbackTransport.cs` | In-process peers. Runs the whole path headlessly, and is the reference implementation. |
| `NetworkMatch.cs` | Binds transport to `GameFlowController`: broadcasts local moves, applies remote ones. |

`GameFlowController.SubmitBandById` is the entry point a remote move uses. It validates the index rather
than trusting it — the value arrives from another machine.

`SeatKind` gained **`Remote`**, and board input is now gated on `SeatRoster.IsLocalHuman` rather than
"not a computer": a computer seat and a seat belonging to a player on another device are both "not yours
to click".

> **The one unrecoverable failure.** A lost, duplicated or reordered `PlaceBand` diverges the two boards
> and neither side can tell — it looks like a working game until the scores disagree. So the transport
> must provide a reliable ordered channel, and `NetworkMatch` checks the move number in every packet as
> a tripwire on that promise. It is not a repair mechanism; on mismatch the match stops and says so.

`NetworkMatch` broadcasts from `GameEvents.OnBandPlaced` rather than from the input layer, so every path
that can place a band is covered — mouse, touch, and the AI when a computer seat shares the device.

### Chat

A slim tab on the left edge of the HUD opens a message log and a grid of six quick-chat phrases, each
with a generated emote glyph. `Assets/Scripts/UI/ChatPanelController.cs` sends and receives through
`NetworkMatch`, so it holds no reference to a transport and does not care which one is in use.

**Quick-chat, not free text.** It is playable one-handed on a phone, it travels as a single integer so a
message costs the same as a move and cannot smuggle anything, and it needs no moderation, filtering or
reporting flow — which open text chat between strangers does need, and which is far more work than the
panel itself. Phrase ids are the wire format, so `ChatPhrases` entries may be appended but never
reordered or removed.

**Collapsed by default, and it must stay that way.** The board fills most of the screen now, so an
always-open panel would cover it — and being a raycast target, it would swallow peg clicks in that area.
The tab sits in the gap between the top-left and bottom-left player cards (which occupy the corners down
to y=208 and up from y=818 in canvas units); the panel spans roughly 290–790, so it touches neither.

Your own messages are echoed locally rather than waiting for them to come back, because they never do —
every transport delivers to the *other* peers only. With no session running the panel still works and
says so, so it can be tried before the transport lands.

> **Name collisions with the TMP examples.** This class is `ChatPanelController`, not `ChatController`,
> because `Assets/TextMesh Pro/Examples & Extras/Scripts/ChatController.cs` declares a
> `ChatController` in the **global** namespace — and in C# a type declared in the global namespace beats
> one imported through a `using`. `AddComponent<ChatController>()` therefore bound to TMP's example
> component with no compile error and no warning; the only symptom was every serialized field failing to
> wire. Prefix or suffix any new class whose bare name is generic.

### Verification

`Tools ▸ Triggle ▸ Verify Multiplayer Spine`, with no transport and no network:

| Check | Result |
|---|---|
| Wire format round-trip | 6/6 message kinds unchanged |
| Malformed packets (null, empty, truncated, garbage) | 5/5 refused without throwing |
| Loopback ordering | 8/8 in order, none echoed to the sender |
| Catalogue determinism (R=3/4/5) | Hash identical across independent boards and rebuilds |
| Replay convergence | **11,930 moves over 120 games, boards identical every time** |

Corrupting the relayed index by one reproduces **80 mismatches per radius**, so the convergence check is
known to detect divergence rather than merely to pass.

**Not covered:** `NetworkMatch` itself. It drives `GameFlowController`, whose move resolution is a
coroutine, and coroutines do not run in edit mode — that binding needs a play-mode test.

---

## 7. Play, and read the gizmos

Press **Play**. The main menu comes up first. **Play Local** for hot-seat, **Play vs AI** to face the
computer; either lands in the lobby, where you pick 2–4 players, set each seat to HUMAN or CPU, type
names and hit **START GAME**.

Hover a peg: legal picks glow cyan, the hovered peg turns pale yellow, picked pegs
turn orange. A solid preview line follows your picks and a pulsing translucent loop appears as soon
as your partial selection resolves to a single legal band. Click the 4th peg of a straight line and
the band stretches open over the pegs.

With **Board** selected, the Scene view shows:

- **grey wire spheres** — pegs
- **thin grey lines** — free unit edges
- **thick amber lines** — edges covered by at least one band
- **coloured spheres** — claimed triangles, tinted by owner
- **white wire spheres** — triangles fully enclosed but not yet claimed (should never persist)
- `Draw Coordinate Labels` overlays each peg's `(q, r)` axial coordinate

---

## 8. How the rules work

- **Board.** Triangular lattice inside a hexagon of radius `R`. Axial coordinates `(q, r)` map to
  `X = spacing·√3·(q + r/2)`, `Z = spacing·1.5·r`, `Y = 0`. All six neighbour directions are the same
  world distance apart, which is what makes the lattice equilateral.
- **A band is straight.** A real rubber band cannot be bent, so a legal placement is a run of
  **4 collinear pegs** along one of the three lattice line directions, covering the **3 unit edges**
  between them. The six neighbour directions form three opposite pairs, and a run along `d` is the
  same run as along `−d`, so there are exactly three line directions.
- **Claiming.** A triangle is claimed the moment all 3 of its edges are covered. Because a triangle's
  three edges each run in a *different* direction, one straight band can cover **at most one edge of
  any triangle** — so every triangle needs **three separate bands**. That is where the whole game
  lives: you spend two bands setting a triangle up and your opponent takes it with the third.
- **Scoring.** +1 point per claimed triangle. **The turn always passes**, scoring or not — there is no
  bonus or extra-turn rule, deliberately. Because one band can complete several triangles at once, a
  bonus-on-claim rule let a single player chain move after move: measured over 60 random 2-player
  games it produced streaks of up to **26 consecutive moves** and gave one player **82% of all turns**.
  With the turn always passing, the turn count between players never differs by more than 1.
- **End.** The match ends when no band is legally playable or every triangle is claimed. Highest
  score wins; equal top scores are reported as a draw.

### Board size vs band length

A band needs a lattice line long enough to hold it. The shortest lines inside a radius-`R` hexagon
hold `R+1` pegs, so **every edge is reachable only when `radius >= pegsPerBand − 1`**:

| Radius | Bands (4-peg) | Triangles | Fully claimable? |
|---|---|---|---|
| 2 | 12 | 24 | **No** — 12 edges lie on no band, so 12 triangles are unreachable |
| 3 | 48 | 54 | Yes |
| 4 | 102 | 96 | Yes |
| 5 | 174 | 150 | Yes |

`BoardManager` checks this at build time and logs a warning naming the exact counts if the board is
too small, so a degenerate setup can't pass unnoticed. **Radius 3 is the minimum for the standard
4-peg rule.** Likewise `pegsPerBand = 5` needs radius 4 or more.

### Verification

- **UI layout.** `Tools ▸ Triggle ▸ Verify UI Layout` drives all eight panels through eight landscape
  shapes (5:4 through 21:9, including three phones with side cutouts) and checks 165 controls twice
  over: **clipping** with every hidden child force-shown, so seat rows 3–4 and the difficulty stepper
  are measured too, and **reachability** with authored visibility, so "what is on top of this button"
  means something. **0 clipped, 0 blocked.**

  The reachability half exists because containment alone is not enough — an earlier version measured
  only rectangles, reported zero violations, and passed a build whose scrim swallowed every tap.
  Removing the scrim's `SetSiblingIndex(0)` reproduces **416 blocked controls**, so the check is known
  to detect that fault rather than merely to pass.
- **Camera framing.** `Tools ▸ Triggle ▸ Verify Camera Framing` projects every peg and the slab outline,
  walked edge by edge, to viewport space at radius 3, 4 and 5 across six window shapes. All 18
  configurations fit with **0 clipped and 0 skewed** — the gap above the board equals the gap below it
  to three decimal places, at every radius and aspect. The board occupies 0.709 of screen height.

  It measures the real content rather than the twelve corners the rig fits to; measuring the rig's own
  optimisation targets would only prove it can do arithmetic. That independence paid: it caught the rig
  reserving headroom at the slab rim where no geometry stands.
- **Exhaustive validator test.** All 66,045 four-peg subsets at R=3 were checked against the band
  catalogue: zero false accepts, zero false rejects. Collinear-but-gapped selections and bent
  selections are both correctly refused, in any click order.
- **Structural.** Across R=2…5, no band covers more than one edge of any triangle, so no band can ever
  claim a triangle on its own.
- **Simulation.** 480 random full games (R=3/4 × 2–4 players × both turn modes): play always
  terminates, scores always sum to triangles claimed, and the whole board is claimed (54/54 at R=3,
  96/96 at R=4).

---

## 9. Controls

| Input | Action |
|---|---|
| Left click / tap on peg | Add peg to selection (or remove it if already picked) |
| Left click on empty space | Cancel the selection |
| Right click | Remove the last picked peg |
| Escape | Cancel the selection |
| `MENU` (top-left) | Abandon the match and return to the main menu |

Touch input is handled alongside mouse, so the same flow works on a phone.

---

## 10. Optional polish

- **Peg prefab** — assign to `BoardManager ▸ Peg Prefab`. It needs a `SphereCollider`; a
  `PegComponent` is added automatically if absent.
- **Token prefab** — assign to `TokenSpawner ▸ Token Prefab`. Its renderers are tinted per seat.
  Leave empty for the generated 3-sided pyramid.
- **Particle prefab** — assign to `TokenSpawner ▸ Burst Prefab` to replace the procedural spark burst.
- **Audio** — drop clips into the six `SoundManager` fields (peg snap, band place, cell claim, invalid
  move, UI click, win fanfare). Any field left empty is filled with a synthesised tone, so audio works
  out of the box. Consecutive claims in one move step the pitch up per triangle.
- **Player names** — cleared with `PlayerProfiles.Clear()`. Names are sanitised on the way in: trimmed,
  whitespace collapsed, capped at 14 characters, and `<` / `>` stripped, since names are echoed into
  rich-text TMP labels where a typed `<color=...>` tag would otherwise corrupt the label.
- **Store build** — set the product name, icon and package ID in Player Settings; the UI is authored at
  1920×1080 with `matchWidthOrHeight = 0.5`, so it scales to phone aspect ratios without reflowing.
- **Enter Play Mode Options** — if you disable domain reload, call
  `GameEvents.ClearAllSubscribers()` from a bootstrap `[RuntimeInitializeOnLoadMethod]`, since the
  event bus is static.
