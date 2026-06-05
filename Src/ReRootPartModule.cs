using System.Collections.Generic;
using System.Globalization;
using System.IO;
using KSP.Localization;
using UnityEngine;

namespace com.github.lhervier.ksp.reroot {

    /// <summary>
    /// Adds a "Set as root" action to the part context menu (Part Action Window).
    ///
    /// Injected on parts via ModuleManager (GameData/ReRootMod/ReRootMod.cfg).
    /// Shown on parts of the active vessel AND of another loaded vessel within range (guiActiveUnfocused,
    /// UNFOCUSED_RANGE). KSP's PAW has no disabled-button state, so instead of a greyed button we show
    /// the button XOR a read-only status line: valid root candidates get the clickable button; parts
    /// that can't be the root (the current root, physics-less parts) get a status line saying why.
    /// </summary>
    public class ReRootPartModule : PartModule {

        private const string LOG_PREFIX = "[ReRootMod]";

        // Dedicated save file: NOT "persistent", so a scene-exit autosave to "persistent" can't
        // clobber our placement patch before the reload reads it back.
        private const string SAVE_NAME = "ReRootReload";

        // Max distance (m) between the part and the active vessel for the action / status line to show
        // on a NON-active vessel (KSP checks |part - activeVessel| < range). Kept modest so you act on
        // nearby vessels (e.g. while on EVA next to them), not anything within the whole load bubble.
        private const float UNFOCUSED_RANGE = 200f;

        // Read-only status line shown (in place of the button) on parts that can't be the root. Same
        // unfocusedRange as the button so both behave consistently on other loaded vessels.
        [KSPField(guiActive = false, guiActiveUnfocused = false, unfocusedRange = UNFOCUSED_RANGE, guiName = "#LOC_ReRoot_statusLabel")]
        public string reRootStatus = "";

        // Cached PAW item handles + last computed state (to avoid re-formatting the status every frame).
        private BaseEvent setRootEvt;
        private BaseField statusFld;
        private int lastState = -1;

        // Shows on the active vessel AND on another loaded vessel within UNFOCUSED_RANGE (guiActiveUnfocused).
        // Visibility is toggled at runtime by UpdatePawItems (button only on valid candidates).
        [KSPEvent(guiActive = false, guiActiveUncommand = true, guiActiveUnfocused = false, unfocusedRange = UNFOCUSED_RANGE, guiName = "#LOC_ReRoot_setAsRoot")]
        public void SetAsRootEvent() {
            try {
                if (part == null || part.vessel == null) {
                    return;
                }
                // Defensive: the button is only shown on valid candidates (UpdatePawItems), but guard
                // anyway in case visibility lags a frame behind a tree change.
                if (!IsRootCandidate(part)) {
                    ScreenMessages.PostScreenMessage(Localizer.Format("#LOC_ReRoot_cannotSetAsRoot", part.partInfo?.title), 3f, ScreenMessageStyle.UPPER_CENTER);
                    return;
                }

                // Disruptive (save + reload) → ask for confirmation first.
                PopupDialog.SpawnPopupDialog(
                    new Vector2(0.5f, 0.5f),
                    new Vector2(0.5f, 0.5f),
                    new MultiOptionDialog(
                        "reRootConfirm",
                        Localizer.Format("#LOC_ReRoot_dialogMessage", part.partInfo?.title),
                        Localizer.Format("#LOC_ReRoot_dialogTitle"),
                        HighLogic.UISkin,
                        new DialogGUIButton(Localizer.Format("#LOC_ReRoot_dialogConfirm"), () => DoReRoot(part), true),
                        new DialogGUIButton(Localizer.Format("#LOC_ReRoot_dialogCancel"), () => { }, true)),
                    false,
                    HighLogic.UISkin);
            } catch (System.Exception ex) {
                Debug.LogError($"{LOG_PREFIX} SetAsRootEvent failed: {ex.Message}\n{ex.StackTrace}");
            }
        }

        public override void OnStart(StartState state) {
            base.OnStart(state);
            setRootEvt = Events["SetAsRootEvent"];
            if (setRootEvt != null) {
                setRootEvt.guiName = Localizer.Format("#LOC_ReRoot_setAsRoot");
            }
            statusFld = Fields["reRootStatus"];
            if (statusFld != null) {
                statusFld.guiName = Localizer.Format("#LOC_ReRoot_statusLabel");
            }
            UpdatePawItems();
        }

        public override void OnUpdate() {
            base.OnUpdate();
            UpdatePawItems();
        }

        // Button XOR status line. KSP has no disabled-button state, so for parts that can't be the root
        // we hide the button and show a read-only status line ("Re-root: …") explaining why instead.
        // For a field to show on a non-active vessel KSP needs BOTH guiActive AND guiActiveUnfocused.
        private void UpdatePawItems() {
            if (part == null) {
                return;
            }
            bool isRoot = (part == part.localRoot);
            bool candidate = IsRootCandidate(part);   // already false for the root and physics-less parts
            if (setRootEvt != null) {
                setRootEvt.guiActive = candidate;
                setRootEvt.guiActiveUnfocused = candidate;
            }
            if (statusFld != null) {
                statusFld.guiActive = !candidate;
                statusFld.guiActiveUnfocused = !candidate;
            }
            // OnUpdate runs every frame on every loaded part — only re-format the text on state change.
            int state = candidate ? 0 : (isRoot ? 1 : 2);
            if (state != lastState) {
                lastState = state;
                if (state == 1) {
                    reRootStatus = Localizer.Format("#LOC_ReRoot_statusIsRoot");
                } else if (state == 2) {
                    reRootStatus = Localizer.Format("#LOC_ReRoot_statusInvalid");
                }
            }
        }

        // ============================================================================================

        private static void DoReRoot(Part newRoot) {
            try {
                Vessel v = newRoot.vessel;
                Part oldRoot = v.rootPart;
                if (newRoot == oldRoot) {
                    return;
                }

                Debug.Log($"{LOG_PREFIX} Re-rooting vessel '{v.vesselName}' (id={v.id}) from " +
                          $"'{oldRoot?.partInfo?.name}' to '{newRoot.partInfo?.name}' (uid={newRoot.flightID}).");

                // 1. In-memory re-root: correct logical tree + geometry for the save. The flight joint
                //    topology SetHierarchyRoot leaves is irrelevant — KSP rebuilds all joints on reload.
                newRoot.SetHierarchyRoot(newRoot);
                EditorReRootUtil.StripInvalidSymmetrySets(newRoot);
                v.rootPart = newRoot;

                // KSP saves root as parts[0] (rootIndex stays 0) and resolves parents sequentially on
                // load, so vessel.parts MUST be topological (root first, parent before children).
                List<Part> ordered = new List<Part>();
                CollectTopo(newRoot, ordered);
                if (ordered.Count != v.parts.Count) {
                    Debug.LogError($"{LOG_PREFIX} Topo traversal reached {ordered.Count}/{v.parts.Count} parts — ABORT.");
                    ScreenMessages.PostScreenMessage(Localizer.Format("#LOC_ReRoot_abortedOrphan"), 4f, ScreenMessageStyle.UPPER_CENTER);
                    return;
                }
                v.parts.Clear();
                v.parts.AddRange(ordered);
                foreach (Part p in v.parts) {
                    p.UpdateOrgPosAndRot(newRoot);
                }

                // 2. New root's true surface-relative placement (transforms haven't moved this frame).
                CelestialBody body = v.mainBody;
                Vector3 rootWorldPos = newRoot.transform.position;
                double lat = body.GetLatitude(rootWorldPos);
                double lon = body.GetLongitude(rootWorldPos);
                double alt = body.GetAltitude(rootWorldPos);
                double hgt = alt - body.TerrainAltitude(lat, lon, true);
                Quaternion srfRot = Quaternion.Inverse(body.bodyTransform.rotation) * newRoot.transform.rotation;
                uint rootUid = newRoot.flightID;
                // Refocus the CURRENT (active) vessel after reload, NOT the re-rooted one — the user may
                // be re-rooting a nearby vessel (e.g. debris) they don't control. If the active vessel
                // IS the one we re-rooted, this is the same index, so the "unless it's the current
                // vessel" case is handled for free. (Index into FlightGlobals.Vessels == protoVessels
                // index in the save; same pattern as the sibling mod's VesselNavigator.)
                Vessel focusVessel = FlightGlobals.ActiveVessel != null ? FlightGlobals.ActiveVessel : v;
                int focusIdx = FlightGlobals.Vessels.IndexOf(focusVessel);

                // 3. Save to a dedicated file.
                Game g = HighLogic.CurrentGame.Updated();
                GamePersistence.SaveGame(g, SAVE_NAME, HighLogic.SaveFolder, SaveMode.OVERWRITE);

                // 4. Patch the target vessel's placement scalars.
                string path = Path.Combine(
                    Path.Combine(Path.Combine(KSPUtil.ApplicationRootPath, "saves"), HighLogic.SaveFolder),
                    SAVE_NAME + ".sfs");
                ConfigNode root = ConfigNode.Load(path);
                ConfigNode target = FindVesselNodeByPartUid(root?.GetNode("GAME")?.GetNode("FLIGHTSTATE"), rootUid);
                if (target == null) {
                    Debug.LogError($"{LOG_PREFIX} Saved VESSEL with part uid={rootUid} not found — ABORT (no reload).");
                    ScreenMessages.PostScreenMessage(Localizer.Format("#LOC_ReRoot_abortedNotFound"), 4f, ScreenMessageStyle.UPPER_CENTER);
                    return;
                }
                target.SetValue("lat", lat.ToString("G17", CultureInfo.InvariantCulture), false);
                target.SetValue("lon", lon.ToString("G17", CultureInfo.InvariantCulture), false);
                target.SetValue("alt", alt.ToString("G17", CultureInfo.InvariantCulture), false);
                target.SetValue("hgt", hgt.ToString("G9", CultureInfo.InvariantCulture), false);
                target.SetValue("rot", KSPUtil.WriteQuaternion(srfRot), false);
                // Skip the landed ground-snap on load (the new root may not be the lowest point);
                // trust the exact coords (the vessel hasn't moved). One-load flag, reset after settling.
                target.SetValue("skipGroundPositioning", "True", false);
                root.Save(path);

                // 5. Reload, refocusing the current (active) vessel. KSP rebuilds all physics correctly.
                Debug.Log($"{LOG_PREFIX} Reloading '{SAVE_NAME}', focusing current vessel " +
                          $"'{focusVessel?.vesselName}' idx={focusIdx}.");
                FlightDriver.StartAndFocusVessel(SAVE_NAME, focusIdx);
            } catch (System.Exception ex) {
                Debug.LogError($"{LOG_PREFIX} DoReRoot failed: {ex.Message}\n{ex.StackTrace}");
                ScreenMessages.PostScreenMessage(Localizer.Format("#LOC_ReRoot_failed"), 4f, ScreenMessageStyle.UPPER_CENTER);
            }
        }

        // Flight-safe replica of EditorReRootUtil.isRootCandidate (private + uses editor statics).
        // In flight every part belongs to its vessel, so the editor's "ship.Contains(p)" stack branch
        // reduces to "has at least one stack node".
        private static bool IsRootCandidate(Part p) {
            if (p == null) return false;
            if (p == p.localRoot) return false;                                  // already the root
            if (!p.attachRules.allowRoot) return false;
            if (!p.attachRules.allowSrfAttach && !p.attachRules.allowStack) return false;
            if (p.attachRules.srfAttach && p.srfAttachNode != null && p.srfAttachNode.attachedPart == null) {
                return true;                                                     // free surface node
            }
            if (p.attachRules.stack) {
                return p.attachNodes.Count > 0;
            }
            return false;
        }

        // Pre-order DFS from a part: parent always precedes its children (topological order).
        private static void CollectTopo(Part p, List<Part> acc) {
            acc.Add(p);
            for (int i = 0; i < p.children.Count; i++) {
                CollectTopo(p.children[i], acc);
            }
        }

        // Finds the VESSEL ConfigNode containing a PART with the given uid (= Part.flightID, unique).
        private static ConfigNode FindVesselNodeByPartUid(ConfigNode flightState, uint uid) {
            if (flightState == null) return null;
            string wanted = uid.ToString();
            foreach (ConfigNode vnode in flightState.GetNodes("VESSEL")) {
                foreach (ConfigNode pnode in vnode.GetNodes("PART")) {
                    if (pnode.GetValue("uid") == wanted) {
                        return vnode;
                    }
                }
            }
            return null;
        }
    }
}
