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
    /// Shown on valid root candidates of the active vessel AND of any other loaded vessel
    /// (guiActiveUnfocused, with no distance limit).
    /// </summary>
    public class ReRootPartModule : PartModule {

        private const string LOG_PREFIX = "[ReRootMod]";
        
        // Dedicated save file: NOT "persistent", so a scene-exit autosave to "persistent" can't
        // clobber our placement patch before the reload reads it back.
        private const string SAVE_NAME = "ReRootReload";
        
        // KSP gates the action on a NON-active vessel by distance: it only shows when
        // |part - activeVessel| < unfocusedRange. We don't want a distance limit at all — if you can
        // right-click a part, its vessel is loaded, and that's the only gate that should matter. So we
        // set the range to float.MaxValue (its square overflows to +Infinity, so every finite distance
        // passes). An unloaded vessel has no part GameObject to click anyway.
        [KSPEvent(guiActive = true, guiActiveUncommand = true, guiActiveUnfocused = true, unfocusedRange = float.MaxValue, guiName = "#LOC_ReRoot_setAsRoot")]
        public void SetAsRootEvent() {
            try {
                if (part == null || part.vessel == null) {
                    return;
                }
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
            BaseEvent ev = Events["SetAsRootEvent"];
            if (ev != null) {
                ev.guiName = Localizer.Format("#LOC_ReRoot_setAsRoot");
            }
            UpdateEventVisibility();
        }

        public override void OnUpdate() {
            base.OnUpdate();
            UpdateEventVisibility();
        }

        // Show the action only on valid root candidates. guiActive gates the focused (active-vessel)
        // case; guiActiveUnfocused gates the case where the part is on another loaded vessel within
        // range — toggle both so non-candidates stay hidden either way.
        private void UpdateEventVisibility() {
            BaseEvent ev = Events["SetAsRootEvent"];
            if (ev == null) {
                return;
            }
            bool candidate = IsRootCandidate(part);
            ev.guiActive = candidate;
            ev.guiActiveUnfocused = candidate;
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
