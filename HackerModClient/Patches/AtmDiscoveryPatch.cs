using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using EFT;
using HarmonyLib;
using Manimal.HackerMod.CustomEFTData;
using SPT.Reflection.Patching;
using UnityEngine;

namespace Manimal.HackerMod.Patches
{
    // walks the loaded scene at raid start, finds ATM GameObjects (via their
    // atm_LOD0 child mesh), attaches HackableAtm. without this they're just
    // decorative geometry — theres no native dispatcher for them.
    //
    // hooks GameWorld.OnGameStarted (fires once per raid after location load).
    // idempotent — destroyed scene takes the previous components with it.
    internal sealed class AtmDiscoveryPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(GameWorld), nameof(GameWorld.OnGameStarted));
        }

        [PatchPostfix]
        private static void Postfix(GameWorld __instance)
        {
            try
            {
                var atmRoots = FindAtmRoots();
                int attached = 0;
                int interactiveLayer = LayerMask.NameToLayer("Interactive");
                foreach (var root in atmRoots)
                {
                    if (root == null) continue;
                    if (root.GetComponent<HackableAtm>() != null) continue;

                    var component = root.gameObject.AddComponent<HackableAtm>();
                    var (dirLocal, height, forward) = ResolveSpitGeometry(root);
                    string atmId = $"atm:{root.gameObject.GetInstanceID()}";
                    component.Configure(atmId, dirLocal, height, forward);

                    // tarkovs interaction raycast queries a specific layer mask —
                    // static map geometry isnt on it, so the existing atm_COLLIDER
                    // doesnt get hit. switch the root to "Interactive" + add a
                    // trigger collider so the raycast has something to land on.
                    // existing atm_COLLIDER stays untouched (bullets etc still hit it).
                    if (interactiveLayer >= 0) root.gameObject.layer = interactiveLayer;
                    EnsureInteractionTrigger(root);

                    attached++;
                }
                Plugin.LogSource?.LogInfo(
                    $"[HackerMod] AtmDiscovery: found {atmRoots.Count} ATM root(s), attached {attached} HackableAtm component(s). InteractiveLayer={interactiveLayer}");
            }
            catch (System.Exception ex)
            {
                Plugin.LogSource?.LogError(
                    $"[HackerMod] AtmDiscovery threw: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // adds a trigger BoxCollider sized to the visible mesh bounds.
        // tarkovs interactable raycast hits triggers — this is what makes
        // the ATM clickable. atm_COLLIDER stays as the physics collider.
        private static void EnsureInteractionTrigger(Transform root)
        {
            var existingBoxes = root.GetComponents<BoxCollider>();
            foreach (var bc in existingBoxes)
            {
                if (bc != null && bc.isTrigger) return; // already added
            }

            var renderers = root.GetComponentsInChildren<MeshRenderer>(includeInactive: true);
            if (renderers.Length == 0) return;

            Bounds worldBounds = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++)
            {
                worldBounds.Encapsulate(renderers[i].bounds);
            }

            var box = root.gameObject.AddComponent<BoxCollider>();
            box.isTrigger = true;
            // world-space size -> local: divide by lossyScale.
            // world-space center -> local: InverseTransformPoint.
            Vector3 lossy = root.lossyScale;
            box.size = new Vector3(
                worldBounds.size.x / Mathf.Max(0.0001f, lossy.x),
                worldBounds.size.y / Mathf.Max(0.0001f, lossy.y),
                worldBounds.size.z / Mathf.Max(0.0001f, lossy.z));
            box.center = root.InverseTransformPoint(worldBounds.center);
        }

        // find ATMs by their atm_LOD0 MeshFilter, walk up to the parent
        // (the "atm (1)" / "atm" node).
        private static List<Transform> FindAtmRoots()
        {
            var roots = new HashSet<Transform>();
            var allMeshFilters = Object.FindObjectsOfType<MeshFilter>();
            foreach (var mf in allMeshFilters)
            {
                if (mf == null) continue;
                if (!mf.name.StartsWith("atm_LOD0", System.StringComparison.OrdinalIgnoreCase)) continue;
                var parent = mf.transform.parent;
                if (parent == null) continue;
                roots.Add(parent);
            }
            return roots.ToList();
        }

        // pick the direction cash flies + height/forward offsets.
        // no per-prefab annotation so we cast short rays along ±X / ±Z and
        // pick whichever direction has the most open space (i.e. away from
        // the wall the ATM is mounted on).
        private static (Vector3 dirLocal, float heightOffset, float forwardOffset) ResolveSpitGeometry(Transform root)
        {
            var candidates = new[]
            {
                new Vector3( 0f, 0f, -1f),
                new Vector3( 0f, 0f,  1f),
                new Vector3(-1f, 0f,  0f),
                new Vector3( 1f, 0f,  0f),
            };

            Vector3 best = candidates[0];
            float   bestClearance = -1f;
            var renderer = root.GetComponentInChildren<MeshRenderer>();
            Vector3 origin = renderer != null ? renderer.bounds.center : (root.position + Vector3.up * 0.6f);

            const float Probe = 0.6f;
            foreach (var c in candidates)
            {
                Vector3 worldDir = root.TransformDirection(c).normalized;
                if (Physics.Raycast(origin, worldDir, out var hit, Probe))
                {
                    if (hit.distance > bestClearance)
                    {
                        bestClearance = hit.distance;
                        best = c;
                    }
                }
                else
                {
                    // nothing hit -> fully open, take it
                    bestClearance = Probe + 1f;
                    best = c;
                    break;
                }
            }

            // height: ~55% up the bounds (above the card slot).
            // forward: just outside the collider so items dont intersect it.
            float h = renderer != null
                ? renderer.bounds.size.y * 0.55f
                : 0.6f;
            return (best, h, 0.25f);
        }
    }
}
