using System;
using Comfort.Common;
using EFT;
using EFT.Interactive;
using EFT.InventoryLogic;
using UnityEngine;

namespace Manimal.HackerMod.CustomEFTData
{
    // marker + interaction component for hackable ATMs. inheriting InteractableObject
    // means tarkovs existing Player.InteractionRaycast picks the ATM up via
    // GetComponentInParent<InteractableObject> — no per-frame raycast on our side.
    // we just patch the action menu dispatcher to expose a Hack action when targeted.
    //
    // attached to each ATM at raid start by AtmDiscoveryPatch.
    public sealed class HackableAtm : InteractableObject
    {
        // stable id for the strike/lockout dictionary, like KeycardDoor.Id
        public string AtmId { get; private set; }

        // direction cash flies out + offsets from the ATM root.
        // resolved per-instance at attach time by AtmDiscoveryPatch.
        private Vector3 _spitDirectionLocal = new Vector3(0f, 0f, -1f);
        private float   _spitHeightOffset   = 0.6f;
        private float   _spitForwardOffset  = 0.2f;

        public void Configure(string atmId, Vector3 spitDirectionLocal, float heightOffset, float forwardOffset)
        {
            AtmId               = atmId;
            _spitDirectionLocal = spitDirectionLocal.normalized;
            _spitHeightOffset   = heightOffset;
            _spitForwardOffset  = forwardOffset;
        }

        public Vector3 SpitOrigin
        {
            get
            {
                var fwd = transform.TransformDirection(_spitDirectionLocal);
                return transform.position + Vector3.up * _spitHeightOffset + fwd * _spitForwardOffset;
            }
        }

        public Vector3 SpitDirection
        {
            get { return transform.TransformDirection(_spitDirectionLocal).normalized; }
        }

        // tear down everything the player can interact with: destroy the trigger
        // collider, disable this component. cash already mid-stream finishes spawning.
        public void DisableInteraction()
        {
            var boxes = GetComponents<BoxCollider>();
            foreach (var bc in boxes)
            {
                if (bc != null && bc.isTrigger) UnityEngine.Object.Destroy(bc);
            }
            enabled = false;
        }

        // gap between consecutive stack spawns
        private const float StreamInterval = 0.10f;

        // streams out stackCount ruble stacks of rublesPerStack each,
        // one at a time, low forward velocity.
        public void SpawnRubleStacks(int stackCount, int rublesPerStack)
        {
            if (stackCount <= 0) return;
            StartCoroutine(StreamRubleStacks(stackCount, rublesPerStack));
        }

        private System.Collections.IEnumerator StreamRubleStacks(int stackCount, int rublesPerStack)
        {
            var gameWorld = Singleton<GameWorld>.Instance;
            if (gameWorld == null)
            {
                Plugin.LogSource?.LogWarning("[HackerMod] ATM payout: GameWorld singleton missing.");
                yield break;
            }
            var factory = Singleton<ItemFactoryClass>.Instance;
            if (factory == null)
            {
                Plugin.LogSource?.LogWarning("[HackerMod] ATM payout: ItemFactoryClass singleton missing.");
                yield break;
            }

            // ThrowItem needs an IPlayer for tracking. MainPlayer is fine — cash
            // isnt tied to them after spawn.
            var thrower = gameWorld.MainPlayer;
            if (thrower == null) yield break;

            int spawned = 0;
            for (int i = 0; i < stackCount; i++)
            {
                if (this == null) yield break; // ATM destroyed mid-stream

                var origin    = SpitOrigin;
                var direction = SpitDirection;
                var rotation  = Quaternion.LookRotation(direction);

                Item item = null;
                try
                {
                    string id = MongoID.Generate(true).ToString();
                    item = factory.CreateItem(id, HackerConstants.RublesTpl, null);
                    if (item != null) TrySetStackCount(item, rublesPerStack);
                }
                catch (Exception ex)
                {
                    Plugin.LogSource?.LogWarning($"[HackerMod] ATM payout CreateItem failed: {ex.GetType().Name}: {ex.Message}");
                }

                if (item != null)
                {
                    try
                    {
                        // low force — stacks pop out and tumble onto the floor
                        // instead of flying across the room
                        var jitterDir = Quaternion.Euler(
                            UnityEngine.Random.Range(-10f, 10f),
                            UnityEngine.Random.Range(-15f, 15f),
                            0f) * direction;
                        var velocity = jitterDir * UnityEngine.Random.Range(1.0f, 1.6f)
                                     + Vector3.up * UnityEngine.Random.Range(0.4f, 0.8f);
                        var angVel = new Vector3(
                            UnityEngine.Random.Range(-3f, 3f),
                            UnityEngine.Random.Range(-3f, 3f),
                            UnityEngine.Random.Range(-3f, 3f));
                        var spawnPos = origin + UnityEngine.Random.insideUnitSphere * 0.05f;

                        gameWorld.ThrowItem(
                            item, thrower, spawnPos, rotation,
                            velocity, angVel,
                            syncable: true,
                            performPickUpValidation: false,
                            makeVisibleAfterDelay: 0f);
                        spawned++;
                    }
                    catch (Exception ex)
                    {
                        Plugin.LogSource?.LogWarning($"[HackerMod] ATM payout ThrowItem failed: {ex.GetType().Name}: {ex.Message}");
                    }
                }

                if (i < stackCount - 1)
                    yield return new UnityEngine.WaitForSeconds(StreamInterval);
            }

            Plugin.LogSource?.LogInfo(
                $"[HackerMod] ATM payout: streamed {spawned}/{stackCount} stacks of {rublesPerStack} from '{name}'.");
        }

        // StackObjectsCount is a public field on Item — earlier GetProperty
        // attempt silently failed and every stack came out as 1.
        private static void TrySetStackCount(Item item, int count)
        {
            try
            {
                if (count <= 1) return;
                item.StackObjectsCount = count;
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning($"[HackerMod] TrySetStackCount: {ex.Message}");
            }
        }
    }
}
