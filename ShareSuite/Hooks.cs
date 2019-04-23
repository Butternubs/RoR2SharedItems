using System.Linq;
using System.Reflection;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using RoR2;
using UnityEngine;
using UnityEngine.Networking;

namespace ShareSuite
{
    public static class Hooks
    {
        static bool sendPickup = true;
        static MethodInfo sendPickupMessage =
            typeof(GenericPickupController).GetMethod("SendPickupMessage",
                BindingFlags.NonPublic | BindingFlags.Static);

        public static void SplitTpMoney()
        {
            On.RoR2.TeleporterInteraction.OnInteractionBegin += (orig, self, activator) =>
            {
                if (self.isCharged && ShareSuite.WrapMoneyIsShared.Value)
                {
                    foreach (var player in PlayerCharacterMasterController.instances)
                    {
                        player.master.money = (uint)
                            Mathf.FloorToInt(player.master.money / PlayerCharacterMasterController.instances.Count);
                    }
                }

                orig(self, activator);
            };
        }

        public static void BrittleCrownHook()
        {
            On.RoR2.HealthComponent.TakeDamage += (orig, self, info) =>
            {
                if (!ShareSuite.WrapMoneyIsShared.Value 
                    || !(bool) self.body 
                    || !(bool) self.body.inventory) {
                    orig(self, info);
                    return;
                }
            
                var body = self.body;
                
                var preDamageMoney = self.body.master.money;
                
                orig(self, info);

                if (body.inventory.GetItemCount(ItemIndex.GoldOnHit) <= 0) return;
                foreach (var player in PlayerCharacterMasterController.instances)
                {
                    if (!(bool) player.master.GetBody() || player.master.GetBody() == body) continue;
                    player.master.money -= preDamageMoney - self.body.master.money;
                    EffectManager.instance.SimpleImpactEffect(Resources.Load<GameObject>(
                            "Prefabs/Effects/ImpactEffects/CoinImpact"),
                        player.master.GetBody().corePosition, Vector3.up, true);
                }
            };

            On.RoR2.GlobalEventManager.OnHitEnemy += (orig, self, info, victim) =>
            {
                if (!ShareSuite.WrapMoneyIsShared.Value 
                    || !info.attacker 
                    || !info.attacker.GetComponent<CharacterBody>()) {
                    orig(self, info, victim);
                    return;
                }

                var body = info.attacker.GetComponent<CharacterBody>();
                
                var preDamageMoney = body.master.money;
                
                orig(self, info, victim);
                
                if (!body.inventory) return;

                if (body.inventory.GetItemCount(ItemIndex.GoldOnHit) <= 0) return;
                foreach (var player in PlayerCharacterMasterController.instances)
                {
                    if (!(bool) player.master.GetBody() || player.master.GetBody() == body) continue;
                    player.master.money += body.master.money - preDamageMoney;
                }
            };
        }

        public static void ModifyGoldReward()
        {
            On.RoR2.DeathRewards.OnKilled += (orig, self, info) =>
            {
                orig(self, info);
                if (!ShareSuite.WrapModIsEnabled.Value
                    || !ShareSuite.WrapMoneyIsShared.Value
                    || !NetworkServer.active) return;

                GiveAllScaledMoney(self.goldReward);
            };

            On.RoR2.BarrelInteraction.OnInteractionBegin += (orig, self, activator) =>
            {
                orig(self, activator);
                if (!ShareSuite.WrapModIsEnabled.Value
                    || !ShareSuite.WrapMoneyIsShared.Value
                    || !NetworkServer.active) return;

                GiveAllScaledMoney(self.goldReward);
            };
        }

        public static void PickupFix()
        {
            On.RoR2.Chat.AddPickupMessage += (orig, body, pickupToken, pickupColor, pickupQuantity) =>
            {
                if (sendPickup)
                    orig(body, pickupToken, pickupColor, pickupQuantity);
            };
        }

        private static void GiveAllScaledMoney(float goldReward)
        {
            foreach (var player in PlayerCharacterMasterController.instances.Select(p => p.master))
            {
                player.GiveMoney(
                    (uint) Mathf.Floor(goldReward * ShareSuite.WrapMoneyScalar.Value - goldReward));
            }
        }

        public static void DisableInteractablesScaling()
        {
            On.RoR2.SceneDirector.PlaceTeleporter += (orig, self) => //Replace 1 player values
            {
                orig(self);
                FixBoss();
                SyncMoney();
                if (!ShareSuite.WrapModIsEnabled.Value || !ShareSuite.WrapOverridePlayerScalingEnabled.Value)

                {
                    orig(self);
                    return;
                }

                // Set interactables budget to 200 * config player count (normal calculation)
                Reflection.SetFieldValue(self, "interactableCredit", 200 * ShareSuite.WrapInteractablesCredit.Value);
            };
        }

        private static void SyncMoney()
        {
            if (!ShareSuite.WrapMoneyIsShared.Value) return;
            foreach (var player in PlayerCharacterMasterController.instances)
            {
                player.master.money = NetworkUser.readOnlyInstancesList[0].master.money;
            }
        }

        public static void FixBoss()
        {
            IL.RoR2.BossGroup.OnCharacterDeathCallback += il => // Replace boss drops
            {
                var c = new ILCursor(il).Goto(99);
                c.Remove();
                if (ShareSuite.WrapModIsEnabled.Value && ShareSuite.WrapOverrideBossLootScalingEnabled.Value)
                {
                    c.Emit(OpCodes.Ldc_I4, ShareSuite.WrapBossLootCredit.Value); // only works when it's a value
                }
                else
                {
                    c.Emit(OpCodes.Ldc_I4,
                        Run.instance.participatingPlayerCount); // standard, runs on every level start
                }
            };
        }


        public static void OnGrantItem()
        {
            On.RoR2.GenericPickupController.GrantItem += (orig, self, body, inventory) =>
            {
                // Item to share
                var item = self.pickupIndex.itemIndex;

                if (!ShareSuite.GetItemBlackList().Contains((int) item)
                    && NetworkServer.active
                    && IsValidPickup(self.pickupIndex)
                    && IsMultiplayer()
                    && ShareSuite.WrapModIsEnabled.Value)
                    foreach (var player in PlayerCharacterMasterController.instances.Select(p => p.master))
                    {
                        // Ensure character is not original player that picked up item
                        if (player.inventory == inventory) continue;
                        if (player.alive || ShareSuite.WrapDeadPlayersGetItems.Value)
                        {
                            player.inventory.GiveItem(item);
                            sendPickup = false;
                            sendPickupMessage.Invoke(null, new object[] { player, self.pickupIndex });
                            sendPickup = true;
                        }
                    }

                orig(self, body, inventory);
            };
        }

        public static void OnShopPurchase()
        {
            On.RoR2.PurchaseInteraction.OnInteractionBegin += (orig, self, activator) =>
            {
                if (!ShareSuite.WrapModIsEnabled.Value)
                {
                    orig(self, activator);
                    return;
                }

                // Return if you can't afford the item
                if (!self.CanBeAffordedByInteractor(activator)) return;

                var characterBody = activator.GetComponent<CharacterBody>();
                var inventory = characterBody.inventory;

                if (ShareSuite.WrapMoneyIsShared.Value)
                {
                    //TODO add comments on what this does
                    switch (self.costType)
                    {
                        case CostType.Money:
                        {
                            orig(self, activator);
                            foreach (var playerCharacterMasterController in PlayerCharacterMasterController.instances)
                            {
                                if (playerCharacterMasterController.master.alive &&
                                    playerCharacterMasterController.master.GetBody() != characterBody)
                                {
                                    playerCharacterMasterController.master.money -= (uint) self.cost;
                                }
                            }

                            return;
                        }

                        case CostType.PercentHealth:
                        {
                            orig(self, activator);
                            var teamMaxHealth = 0;
                            foreach (var playerCharacterMasterController in PlayerCharacterMasterController.instances)
                            {
                                var charMaxHealth = playerCharacterMasterController.master.GetBody().maxHealth;
                                if (charMaxHealth > teamMaxHealth)
                                {
                                    teamMaxHealth = (int) charMaxHealth;
                                }
                            }

                            var purchaseInteraction = self.GetComponent<PurchaseInteraction>();
                            var amount = (uint) (teamMaxHealth * purchaseInteraction.cost / 100.0 * 0.5f *
                                                 ShareSuite.WrapMoneyScalar.Value);
                            var purchaseDiff =
                                amount - (uint) ((double) characterBody.maxHealth * purchaseInteraction.cost / 100.0 *
                                                 0.5f);

                            foreach (var playerCharacterMasterController in PlayerCharacterMasterController.instances)
                            {
                                if (!playerCharacterMasterController.master.alive) continue;
                                playerCharacterMasterController.master.GiveMoney(
                                    playerCharacterMasterController.master.GetBody() != characterBody
                                        ? amount
                                        : purchaseDiff);
                            }

                            return;
                        }
                    }
                }

                // If this is not a multi-player server or the fix is disabled, do the normal drop action
                if (!IsMultiplayer() || !ShareSuite.WrapPrinterCauldronFixEnabled.Value)
                {
                    orig(self, activator);
                    return;
                }

                var shop = self.GetComponent<ShopTerminalBehavior>();

                // If the cost type is an item, give the user the item directly and send the pickup message
                if (self.costType == CostType.WhiteItem
                    || self.costType == CostType.GreenItem
                    || self.costType == CostType.RedItem)
                {
                    var item = shop.CurrentPickupIndex().itemIndex;
                    inventory.GiveItem(item);
                    sendPickupMessage.Invoke(null,
                        new object[] {inventory.GetComponent<CharacterMaster>(), shop.CurrentPickupIndex()});
                }

                orig(self, activator);
            };
        }

        public static void OnPurchaseDrop()
        {
            On.RoR2.ShopTerminalBehavior.DropPickup += (orig, self) =>
            {
                if (!ShareSuite.WrapModIsEnabled.Value)
                {
                    orig(self);
                    return;
                }

                if (!NetworkServer.active) return;
                var costType = self.GetComponent<PurchaseInteraction>().costType;
                Debug.Log("Cost type: " + costType);
                // If this is a multi-player lobby and the fix is enabled and it's not a lunar item, don't drop an item
                if (!IsMultiplayer()
                    || !IsValidPickup(self.CurrentPickupIndex())
                    || !ShareSuite.WrapPrinterCauldronFixEnabled.Value
                    || self.itemTier == ItemTier.Lunar
                    || costType == CostType.Money)
                {
                    // Else drop the item
                    orig(self);
                }
            };
        }

        private static bool IsValidPickup(PickupIndex pickup)
        {
            var item = pickup.itemIndex;
            return IsWhiteItem(item) && ShareSuite.WrapWhiteItemsShared.Value
                   || IsGreenItem(item) && ShareSuite.WrapGreenItemsShared.Value
                   || IsRedItem(item) && ShareSuite.WrapRedItemsShared.Value
                   || pickup.IsLunar() && ShareSuite.WrapLunarItemsShared.Value
                   || IsBossItem(item) && ShareSuite.WrapBossItemsShared.Value
                   || IsQueensGland(item) && ShareSuite.WrapQueensGlandsShared.Value;
        }

        private static bool IsMultiplayer()
        {
            // Check if there are more then 1 players in the lobby
            return PlayerCharacterMasterController.instances.Count > 1;
        }

        public static bool IsWhiteItem(ItemIndex index)
        {
            return ItemCatalog.tier1ItemList.Contains(index);
        }

        public static bool IsGreenItem(ItemIndex index)
        {
            return ItemCatalog.tier2ItemList.Contains(index);
        }

        public static bool IsRedItem(ItemIndex index)
        {
            return ItemCatalog.tier3ItemList.Contains(index);
        }

        public static bool IsBossItem(ItemIndex index)
        {
            return index == ItemIndex.Knurl;
        }

        public static bool IsQueensGland(ItemIndex index)
        {
            return index == ItemIndex.BeetleGland;
        }
    }
}