# KSP-ReRoot — Set as root, in flight

A Kerbal Space Program mod that adds a **"Set as root"** action to the part context menu **in flight**. Its main purpose is **EVA Construction**: a Kerbal can never grab or detach the *root* part of a vessel, so this mod lets you move the root onto another part — freeing the old one for editing.

**Unable to grab that damn docking port ?**

![Unable to grab docking port ?](imgs/01%20-%20Unable%20to%20Grab%20Docking%20Port.png)


**Set the tank as a new root**

![Set the tank as new root](imgs/02%20-%20Set%20Tank%20as%20new%20root.png)

**This will save and reload the game**

![Save and Reload](imgs/03%20-%20Save%20and%20Reload.png)

**And now you can grab this nice little docking port...**

![And now you can](imgs/04%20-%20Now%20you%20can.png)

---

## Why this mod exists

In the **editor**, re-rooting is trivial: the vessel tree is just data. **In flight, KSP gives you no way to do it.** That's a real problem, especially for bases built with **EVA Construction**:

- A Kerbal **cannot detach the *root* part** of a vessel (just like in the editor, the root can't be "grabbed").
- After a creative landing or an in-place build, the part you actually want to remove — an ugly/heavy docking port, a cupola on top, etc. — often *is* the root, so it's stuck.

Interestingly, Squad clearly *intended* to support this: the EVA-construction code even defines an unused `ConstructionMode.Root` mode — but it was never implemented. **This mod fills that gap.**

Re-rooting is **not** a physical operation. Physics relies on the `PartJoint`s between adjacent parts, not on "who is the root". The root is only a reference point (transform, control, persistence). Re-rooting = rewriting pointers — so it is perfectly safe to do, KSP just never exposed it in flight.

---

## What it does / How to use

1. **Right-click** a compatible part → **"Set as root"**. The action works on the vessel you're controlling **and on any nearby loaded vessel (within ~200 m)** — including debris you can't even switch to (handy in EVA Construction, where the thing you want to dismantle often isn't the vessel you control).
2. Confirm in the dialog. The game is **saved and reloaded**.
3. You're returned to the **vessel you were controlling** (not the one you re-rooted, unless they're the same) — and the target vessel is now rooted on the part you chose, correctly placed and physically intact.

The clickable button only appears on **valid root candidates** (the same parts the editor would accept — e.g. structural/stack parts, not radial batteries or antennas). On parts that can't be the root — the current root, or a physics-less part — you instead get a small read-only status line (`Re-root: current root` / `Re-root: invalid root part`) in place of the button. (KSP can't grey out a context-menu button, so we swap the button for a status line rather than hide everything.)

> Tip: to free a stuck root part for EVA Construction, right-click an adjacent part of that vessel (it works even while you're on EVA next to it, since the vessel doesn't have to be the one you control) and "Set as root" — the old root is now a normal child you can detach.

---

## Requirements

- Kerbal Space Program **1.12.5**, French or English
- **ModuleManager** (used to inject the action onto parts)

## Installation

1. Build (see below) or download a release.
1. Copy the `ReRootMod` folder into your KSP `GameData/` folder.
1. Make sure [`ModuleManager`](https://github.com/sarbian/ModuleManager) is also in `GameData/`.

---

## How it works (the interesting part)

Re-rooting a *flying* vessel turned out to be a minefield of KSP physics invariants. The approach that finally proved robust on everything from a tiny probe to a huge Eve base is a **hybrid**: do the tree/geometry work in memory with native KSP calls, then let KSP's own save/load rebuild the physics.

### The pipeline (per "Set as root")

1. **In-memory re-root** of the live vessel:
   - `Part.SetHierarchyRoot(newRoot)` — inverts the parent chain (native KSP).
   - `EditorReRootUtil.StripInvalidSymmetrySets(newRoot)` — drops symmetry groups that are no longer valid after the root change.
   - `vessel.rootPart = newRoot`.
   - **Reorder `vessel.parts` topologically** (root first, every parent before its children) via a pre-order DFS.
   - `part.UpdateOrgPosAndRot(newRoot)` on every part — re-express each part's `orgPos`/`orgRot` relative to the new root.
2. **Compute the new root's true surface placement** from its live transform
   (`lat`/`lon`/`alt`, and `srfRelRotation = Inverse(body.bodyTransform.rotation) * newRoot.rotation`).
3. **`SaveGame`** to a **dedicated file** (`ReRootReload`, *not* `persistent`).
4. **Patch** that save's target `VESSEL` node: overwrite `lat`/`lon`/`alt`/`hgt`/`rot` + set `skipGroundPositioning = True`. The vessel is located by the new root's part `uid` (= `flightID`).
5. **`FlightDriver.StartAndFocusVessel("ReRootReload", idx)`** — reload, refocusing the same vessel.

The **reload is what makes it robust**: KSP rebuilds *all* joints, rigidbodies and physical-significance from scratch off the (now correct) tree, so we never have to get the live flight physics right ourselves.

### Hard-won findings (why each step is there)

These are the non-obvious things that each cost a debugging round:

- **Topological order is mandatory.** KSP always writes `root = 0` and resolves each part's parent **by sequential index** on load. If a child appears before its parent in the `PART` list, the load throws and **drops the part** (and the vessel deforms). Hence the in-memory reorder so the new root is `parts[0]` and parents precede children.
- **Placement must be re-expressed for the new root.** A landed vessel's `lat`/`lon`/`alt`/`srfRelRotation` describe the *root*. After re-rooting they still describe the *old* root, so on reload the (new) root is dropped at the old root's pose and the whole vessel ends up rotated/offset. We recompute and patch them.
- **`skipGroundPositioning = True`** on reload. KSP re-snaps a landed vessel to the terrain by raycasting from the *root*; with a new root that isn't the lowest point, that snap shoves parts underground. Skipping it makes KSP trust our exact coordinates (the vessel never actually moved). It's a one-load flag that flight logic resets after the vessel settles.
- **Use a dedicated save file, not `persistent`.** A scene-exit autosave to `persistent` clobbers the placement patch before the reload reads it. Saving and reloading our own `ReRootReload` file sidesteps that entirely.
- **Only valid root candidates.** A physics-less part (radial battery, antenna — `physicalSignificance = NONE`, no rigidbody) cannot be a flight root: children would joint to a mass-less anchor and the vessel drifts away. We reproduce the editor's `isRootCandidate` rules (flight-safe) and simply refuse the rest — which is exactly what the editor does.
- **Don't fight the live joints.** Doing the re-root purely in memory (no reload) scrambles the joint topology in flight — the same fragility that makes the stock Klaw buggy. Since the reload rebuilds joints from the tree, we just don't care about the transient live joint state.

---

## Limitations / notes

- Works on the **active vessel and any nearby loaded vessel** (right-click a part on it — within ~200 m of your active vessel). After the reload you're refocused on the vessel you were controlling, so the operation never steals control away from you.
- For the action to appear on a *non-active* vessel, your **active vessel must be controllable** (KSP's rule for unfocused part actions) — any controllable vessel works (a Kerbal on EVA, a rover, a probe…).
- The operation **saves and reloads** the game (brief loading screen). That's the price of letting KSP rebuild a clean physics state.
- Landed/splashed vessels are the primary target and are fully validated. Orbital vessels appear to work too.
- The action is independent of EVA Construction Mode — it's a standalone, technical vessel operation.

---

## Building

Set the `KSPDIR` environment variable to your KSP install, then:

- **Windows:** `build.bat` then `install.bat`
- **Linux:** `./build.sh` then `./install.sh`

`build` compiles `ReRootMod.dll`, bundles the ModuleManager patch and the
localization files, and produces `Release/ReRootMod.zip`. `install` unpacks it into `$KSPDIR/GameData/ReRootMod`.

## Localization

English (`en-us`) and French (`fr-fr`) are included, under `GameData/ReRootMod/Localization/`.

## License

MIT (see LICENSE).
