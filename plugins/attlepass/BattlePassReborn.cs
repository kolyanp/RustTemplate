using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Microsoft.VisualBasic;
using Network;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Libraries;
using Oxide.Game.Rust.Cui;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UI;
using Oxide.Core.Plugins;
using Oxide.Plugins.BattlePassRebornExtensionMethods;
using HarmonyLib;

namespace Oxide.Plugins
{
    [Info("BattlePassReborn", "kolyan", "1.1.4")]
    partial class BattlePassReborn : RustPlugin
    {
        private readonly System.Random _rand = new System.Random();
        private Configuration _config;
        private const bool isEn = false;
        private readonly List<BaseEntity> IgnoredContainers = new List<BaseEntity>();
        private readonly Dictionary<ulong, ulong> LastHeliHit = new Dictionary<ulong, ulong>();
        private readonly Dictionary<ulong, ulong> LastBradleyHit = new Dictionary<ulong, ulong>();
        private List<LevelInfo> _allLevels = new List<LevelInfo>();
        private List<MissionTask> _allMissions = new List<MissionTask>();
        private UISettings _UISettings = new UISettings();
        private readonly Dictionary<string, int> _shortNameToItemID = new();
        private Dictionary<ulong, PlayerData> _dataPlayers = new Dictionary<ulong, PlayerData>();
        private Dictionary<ulong, Storage> _dataStorages = new Dictionary<ulong, Storage>();
        private static Double CurrentTime => DateTime.UtcNow.Subtract(Epoch).TotalSeconds;
        private static Double LastWipe;
        private static DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0);
        private bool _preloadScroll = false;

        #region Config

        internal class Configuration
        {
            [JsonProperty(PropertyName = isEn ? "Command list to open UI" : "Список команд для открытия UI",
                ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public string[] CommandList = new[] { "bp", "pass", "rpass" };

            [JsonProperty(PropertyName = isEn ? "General settings" : "Общие настройки",
                ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public GeneralSettings General = new GeneralSettings();

            [JsonProperty(PropertyName = isEn ? "Task settings" : "Настройки заданий",
                ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public TaskSettings Tasks = new TaskSettings();

            [JsonProperty(PropertyName = isEn ? "Wipe settings" : "Настройки вайпа",
                ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public WipeSettings Wipe = new WipeSettings();

            [JsonProperty(PropertyName = isEn ? "Storage settings" : "Настройки хранилища",
                ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public StorageSettings StorageS = new StorageSettings();

            [JsonProperty(PropertyName = isEn ? "Premium settings" : "Настройки премиум игроков",
                ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public PremiumSettings Premium = new PremiumSettings();

            [JsonProperty(PropertyName = isEn ? "Discord settings" : "Настройки дискорда",
                ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public DiscordSettings Discord = new DiscordSettings();

            [JsonProperty(PropertyName = isEn ? "Player settings" : "Настройки игроков",
                ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public PlayerSettings Players = new PlayerSettings();

            [JsonProperty(PropertyName = isEn ? "Integration settings with the SkillTree plugin" : "Настройки интеграции с плагином SkillTree",
                ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public IntegrationSkillTree IntegrationST = new IntegrationSkillTree();
        }

        internal class GeneralSettings
        {
            [JsonProperty(isEn ? "Total number of levels" : "Общее количество уровней")]
            public int TotalLevels = 50;

            [JsonProperty(isEn ? "EXP required per level (Default)" : "Сколько очков нужно на один уровень (дефолтно)")]
            public int ExpPerLevel = 100;

            [JsonProperty(isEn ? "Permission to access PREMIUM rewards" : "Пермишн для доступа к PREMIUM наградам")]
            public string PREMIUMRewardPermission = "battlepassreborn.premium";

            [JsonProperty(isEn ? "Is BattlePass available for permission?" : "BattlePass доступен по пермишну?")]
            public readonly bool AvailableBPPermission = false;

            [JsonProperty(isEn ? "Permission to access BattlePass" : "Пермишн для доступа к BattlePass")]
            public readonly string BPPermission = "battlepassreborn.use";
        }

        internal class TaskSettings
        {
            [JsonProperty(isEn ? "EXP for tasks" : "Настройка очков за тип задач")]
            public ExpForTasks ExpForTasks = new ExpForTasks();

            [JsonProperty(isEn ? "Daily tasks for normal players" : "Сколько заданий дается в день")]
            public int DailyTasks = 5;

            [JsonProperty(isEn ? "Daily tasks for premium players" : "Сколько заданий дается в день премиум игрокам")]
            public int DailyTasksPremium = 8;

            [JsonProperty(isEn ? "Add new task after complete" : "После выполнения добавляется новое задание")]
            public bool AddNewTaskAfterComplete = true;

            [JsonProperty(isEn ? "The name of the preset file" : "Название файла с пресетом")]
            public string fileName = "DefaultMissions";

            [JsonProperty(isEn ? "Is the task replacement system enabled?" : "Включена ли система замены заданий?")]
            public bool RefreshTask = true;

            [JsonProperty(isEn
                ? "The number of refreshes task for the normal player"
                : "Количество обновлений заданий для обычных игроков")]
            public int TaskRefreshCount = 3;

            [JsonProperty(isEn
                ? "The number of refreshes task for the PREMIUM player"
                : "Количество обновлений заданий для ПРЕМИУМ игроков")]
            public int TaskRefreshPremiumCount = 5;
        }

        internal class ExpForTasks
        {
            [JsonProperty("Easy")] public int Easy = 10;
            [JsonProperty("Medium")] public int Medium = 25;
            [JsonProperty("Hard")] public int Hard = 50;
        }

        internal class WipeSettings
        {
            [JsonProperty(isEn ? "Reset progress on wipe" : "Сброс прогресса при вайпе")]
            public bool ResetProgressOnWipe = false;

            [JsonProperty(isEn
                ? "Resetting the BP on a specific date (works if reset progress on wipe = false). When null is not reset"
                : "Сброс БП в определенную дату (работает, если сброс прогресса при вайпе = false). При null не сбрасывается.")]
            public DateTime? ResetProgressOnDate = new DateTime(2026, 1, 1);
        }

        internal class StorageSettings
        {
            [JsonProperty(isEn
                ? "Is clearing player inventories after a wipe included?"
                : "Включено ли очищение складов игроков после вайпа?")]
            public bool EnableClearingRewardsStorage = false;

            [JsonProperty(isEn
                ? "Should unclaimed rewards be sent to the storage after a wipe?"
                : "Отправлять ли не забранные игроком награды на склад после вайпа?")]
            public bool EnableSendUnclaimedRewardsStorage = true;

            [JsonProperty(
                isEn ? "Storage cooldown after wipe (minutes)" : "Кулдаун после вайпа на использование склада")]
            public int StorageCooldownAfterWipeMinutes = 60;

            [JsonProperty(isEn
                ? "Storage lifetime (days). Set 0, if disabled"
                : "Время хранения предметов в складе. Поставь 0, если выключено.")]
            public int StorageItemLifetimeDays = 7;
        }

        internal class PremiumSettings
        {
            [JsonProperty(isEn ? "Enable premium bonus" : "Включен ли бонус премиум-игрокам")]
            public bool EnablePremiumBonus = true;

            [JsonProperty(isEn ? "XP multiplier" : "Множитель XP (например, 1.25 = +25%)")]
            public double XpMultiplier = 1.25;
        }

        internal class DiscordSettings
        {
            [JsonProperty(isEn ? "Discord webhook url" : "Вебхук дискорд логов")]
            public string WebhookUrl = "";

            [JsonProperty(isEn ? "Log level up events" : "Логировать повышение уровня")]
            public bool LogLevelUp = true;

            [JsonProperty(isEn ? "Log reward claim events" : "Логировать получение награды")]
            public bool LogRewardClaim = true;
        }

        internal class PlayerSettings
        {
            [JsonProperty(isEn ? "Archive season data" : "Архивировать ли данные после окончания сезона")]
            public bool ArchiveSeasonData = false;

            [JsonProperty(isEn ? "Delete inactive players after days" : "Через сколько дней удалять неактивных игроков")]
            public int InactiveDeleteAfterDays = 30;
        }

        internal class IntegrationSkillTree
        {
            [JsonProperty(isEn ? "Enable integration with SkillTree" : "Включена ли интеграция с плагином SkillTree")]
            public bool isEnableIntegration = false;

            [JsonProperty(isEn ? "Integration mode (0 - Full, 1 - BpToSk, 2 - SkToBp)" : "Режим интеграции (0 - Full, 1 - BpToSk, 2 - SkToBp)")]
            public SkillTreeIntegrationMode Mode = SkillTreeIntegrationMode.Full;
        }

        public enum SkillTreeIntegrationMode
        {
            Full,    
            BpToSk,   
            SkToBp    
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                _config = Config.ReadObject<Configuration>();
                if (_config == null) throw new Exception();

                if (_config.General.PREMIUMRewardPermission == "bp.premium")
                    _config.General.PREMIUMRewardPermission = "battlepassreborn.premium";

                SaveConfig();
            }
            catch
            {
                PrintError("Your configuration file contains an error. Using default configuration values.");
                LoadDefaultConfig();
            }
        }

        protected override void SaveConfig()
        {
            Config.WriteObject(_config);
        }

        protected override void LoadDefaultConfig()
        {
            _config = new Configuration();
        }

        #endregion

        #region OxideHooks

        private void Init()
        {
            Unsubscribe(nameof(OnDispenserGather));
            Unsubscribe(nameof(OnDispenserBonus));
            Unsubscribe(nameof(OnCollectiblePickup));
            Unsubscribe(nameof(OnGrowableGathered));
            Unsubscribe(nameof(OnFishCatch));
            Unsubscribe(nameof(OnLootEntity));
            if(!_config.IntegrationST.isEnableIntegration || _config.IntegrationST.Mode == SkillTreeIntegrationMode.BpToSk)
                Unsubscribe(nameof(STCanGainXP));
        }

        private void ServerOpened() => LastWipe = SaveRestore.SaveCreatedTime.Subtract(Epoch).TotalSeconds;

        private void OnServerInitialized()
        {
            LastWipe = SaveRestore.SaveCreatedTime.Subtract(Epoch).TotalSeconds;
            if (!permission.PermissionExists(_config.General.PREMIUMRewardPermission, this))
                permission.RegisterPermission(_config.General.PREMIUMRewardPermission, this);

            if (!permission.PermissionExists(_config.General.BPPermission, this))
                permission.RegisterPermission(_config.General.BPPermission, this);


            _allLevels = EnsureLevelInfoFileExists();

            RemoveInactivePlayers();

            foreach (var check in ItemManager.itemList)
            {
                if (!_shortNameToItemID.ContainsKey(check.shortname))
                    _shortNameToItemID.Add(check.shortname, check.itemid);
            }

            foreach (var check in _allLevels)
            {
                if (string.IsNullOrEmpty(check.ImageDefault) || string.IsNullOrEmpty(check.ImagePremium))
                {
                    var nonPremium = check.RewardList.FirstOrDefault(r => !r.IsPremium);
                    var premium = check.RewardList.FirstOrDefault(r => r.IsPremium);

                    if (string.IsNullOrEmpty(check.ImageDefault) && nonPremium != null)
                    {
                        check.ImageDefault = string.IsNullOrEmpty(nonPremium.img) ? nonPremium.ShortName : nonPremium.img;
                        if (check.ImageDefault.StartsWith("http") || check.ImageDefault == nonPremium.img)
                            ImageSettingsList.Add(new ImageSettings()
                            {
                                Name = check.ImageDefault,
                                Url = check.ImageDefault
                            });
                    }

                    if (string.IsNullOrEmpty(check.ImagePremium) && premium != null)
                    {
                        check.ImagePremium = string.IsNullOrEmpty(premium.img) ? premium.ShortName : premium.img;
                        if (check.ImagePremium.StartsWith("http") || check.ImagePremium == premium.img)
                            ImageSettingsList.Add(new ImageSettings()
                            {
                                Name = check.ImagePremium,
                                Url = check.ImagePremium
                            });
                    }
                }
                else
                {
                    if (check.ImageDefault.StartsWith("http"))
                        ImageSettingsList.Add(new ImageSettings()
                        {
                            Name = check.ImageDefault,
                            Url = check.ImageDefault
                        });
                    if (check.ImagePremium.StartsWith("http"))
                        ImageSettingsList.Add(new ImageSettings()
                        {
                            Name = check.ImagePremium,
                            Url = check.ImagePremium
                        });
                }

                foreach (var it in check.RewardList)
                {
                    if (string.IsNullOrEmpty(it.img))
                        ImageSettingsList.Add(new ImageSettings()
                        {
                            Name = it.img,
                            Url = it.img
                        });
                }
            }

            ImageSettingsList.Add(new ImageSettings()
            {
                Name = "BG_BRBagG",
                Url = _UISettings.BackGroundURL
            });

            if (!_config.Wipe.ResetProgressOnWipe && _config.Wipe.ResetProgressOnDate != null)
                ResetProgressOnDate();

            foreach (var check in _config.CommandList) cmd.AddChatCommand(check, this, nameof(cmdChat));

            DownloadImage();

            foreach (var check in BasePlayer.activePlayerList)
                OnPlayerConnected(check);

            PrintError("|-----------------------------------|");
            PrintWarning($"|  Plugin {Title} v{Version} is loaded  |");
            PrintWarning("|          Discord: CASHR#6906      |");
            PrintError("|-----------------------------------|");
            ServerPanel?.Call("API_OnServerPanelProcessCategory", Name);
            Subscribe(nameof(OnDispenserGather));  
            Subscribe(nameof(OnDispenserBonus));
            Subscribe(nameof(OnCollectiblePickup));
            Subscribe(nameof(OnGrowableGathered));
            Subscribe(nameof(OnFishCatch));
            Subscribe(nameof(OnLootEntity));
        }

        private void Unload()
        {
            OnServerSave();
            foreach (var check in BasePlayer.activePlayerList)
                CuiHelper.DestroyUi(check, "bp_progress_bar");
        }

        private void OnPlayerConnected(BasePlayer player)
        {
            if(player.IsNpc || !player.userID.IsSteamId())
                return;
            LoadPlayerData(player.userID, player.displayName);
            LoadStoragePlayerData(player.userID, player.displayName);
            ShowProgressBarBPUI(player);

            if(!_preloadScroll)
            {
                _preloadScroll = true;
                PreloadScroll(player);
            }
        }

        private void OnServerSave()
        {
            foreach (var check in _dataStorages)
            {
                SaveStoragePlayerData(check.Key, check.Value);
            }

            foreach (var check in _dataPlayers)
            {
                SavePlayerData(check.Key, check.Value);
            }
        }

        private void OnNewSave()
        {
            ServerMgr.Instance.Invoke(() =>
            {
                if (_config.StorageS.EnableSendUnclaimedRewardsStorage)
                    GiveAllUnclaimedRewardsToAllPlayers();

                if (_config.Wipe.ResetProgressOnWipe)
                    ArchiveData(true);

                if (_config.StorageS.EnableClearingRewardsStorage)
                    ClearStorageFolder();

                foreach (var check in BasePlayer.activePlayerList)
                    OnPlayerConnected(check);
            }, 60);
        }

        private void OnDispenserGather(ResourceDispenser dispenser, BasePlayer player, Item item) =>
            OnDispenserBonus(dispenser, player, item);

        private void OnDispenserBonus(ResourceDispenser dispenser, BasePlayer player, Item item)
        {
            if (player == null) return;

            if (item != null)
                HandleQuestEvent(player, "extract", item.info.shortname, item.amount);
        }

        private void OnItemCraftFinished(ItemCraftTask task, Item item, ItemCrafter crafter)
        {
            if (crafter == null) return;
            var player = crafter.owner;
            if (player == null) return;

            HandleQuestEvent(player, "craft", item.info.shortname, item.amount);
        }

        private object OnCollectiblePickup(CollectibleEntity collectible, BasePlayer player)
        {
            if (collectible == null || collectible.itemList == null) return null;
            var items = collectible.itemList.ToList();
            for (var index = 0; index < items.Count; index++)
            {
                var check = collectible.itemList[index];
                if (check.itemDef == null) continue;
                int amount = (int)Math.Ceiling(check.amount);
                HandleQuestEvent(player, "pickup", check.itemDef.shortname, amount);
                HandleQuestEvent(player, "extract", check.itemDef.shortname, amount);
            }


            return null;
        }

        private void OnEntityKill(BaseNetworkable entity)
        {
            var be = entity as BaseEntity;
            if (be == null) return;
            if (!IgnoredContainers.Contains(be)) return;
            IgnoredContainers.Remove(be);
        }

        private void OnLootEntity(BasePlayer player, StorageContainer container)
        {
            if (player == null || container == null || IgnoredContainers.Contains(container)) return;

            HandleQuestEvent(player, "loot", container.ShortPrefabName, 1);

            IgnoredContainers.Add(container);
        }

        private object OnEntityTakeDamage(BradleyAPC entity, HitInfo info)
        {
            if (entity == null || info == null || info.InitiatorPlayer == null) return null;
            var player = info.InitiatorPlayer;
            if (!LastBradleyHit.ContainsKey(entity.net.ID.Value))
                LastBradleyHit.Add(entity.net.ID.Value, player.userID);
            LastBradleyHit[entity.net.ID.Value] = player.userID;
            return null;
        }

        private object OnEntityTakeDamage(PatrolHelicopter entity, HitInfo info)
        {
            if (entity == null || info == null || info.InitiatorPlayer == null) return null;
            var player = info.InitiatorPlayer;
            if (!LastHeliHit.ContainsKey(entity.net.ID.Value))
                LastHeliHit.Add(entity.net.ID.Value, player.userID);
            LastHeliHit[entity.net.ID.Value] = player.userID;
            return null;
        }

        private void OnEntityDeath(PatrolHelicopter entity, HitInfo info)
        {
            if (entity == null || info == null || info.InitiatorPlayer == null) return;
            if (!LastHeliHit.ContainsKey(entity.net.ID.Value)) return;
            var player = info.InitiatorPlayer;
            HandleQuestEvent(player, "kill", entity.ShortPrefabName, 1);
        }

        private void OnEntityDeath(BradleyAPC entity, HitInfo info)
        {
            if (entity == null || info == null || info.InitiatorPlayer == null) return;
            if (!LastBradleyHit.ContainsKey(entity.net.ID.Value)) return;
            var player = info.InitiatorPlayer;
            HandleQuestEvent(player, "kill", entity.ShortPrefabName, 1);
        }

        private void OnEntityDeath(BaseEntity entity, HitInfo info)
        {
            if (entity == null || info == null || info.InitiatorPlayer == null) return;
            var player = info.InitiatorPlayer;
            if (player.IsNpc) return;
            if (entity is DecayEntity && entity.OwnerID == player.userID) return;
            if (player.Team != null && player.Team.members.Contains(entity.OwnerID)) return;

            HandleQuestEvent(player, "kill", entity.ShortPrefabName, 1);

            var weaponItem = info.Weapon?.GetCachedItem();
            if (weaponItem != null)
            {
                HandleQuestEvent(player, "kill_with_weapon", entity.ShortPrefabName, weaponItem.info.shortname, 1);
            }
        }

        private void OnEntityBuilt(Planner plan, GameObject go)
        {
            if (plan == null || go == null) return;
            var player = plan.GetOwnerPlayer();
            if (player == null) return;
            var entity = go.ToBaseEntity();

            HandleQuestEvent(player, "build", entity.ShortPrefabName, 1);
        }

        private void OnQuestCompleted(BasePlayer player, string DisplayName)
        {
            if (player == null) return;

            HandleQuestEvent(player, "quest", "completed", 1);
        }

        private void OnGrowableGathered(GrowableEntity plant, Item item, BasePlayer player)
        {
            if (player == null || item == null) return;
            HandleQuestEvent(player, "growup", item.info.shortname, item.amount);
        }

        private void OnStructureUpgrade(BuildingBlock block, BasePlayer player, BuildingGrade.Enum grade)
        {
            if (player == null || block == null) return;

            HandleQuestEvent(player, "upgrade", grade.ToString(), 1);
        }

        private void OnFishCatch(Item item, BaseFishingRod rod, BasePlayer player)
        {
            if (player == null || item == null) return;
            HandleQuestEvent(player, "fishcatch", item.info.shortname, item.amount);
        }

        private void OnHealingItemUse(MedicalTool tool, BasePlayer player)
        {
            if (player == null || tool == null) return;

            HandleQuestEvent(player, "heal", tool.ShortPrefabName, 1);
        }

        private void OnMlrsRocketFired(MLRS mlrs, ServerProjectile projectile)
        {
            if (projectile?.baseEntity == null || projectile.baseEntity.OwnerID == 0) return;

            var player = BasePlayer.FindByID(projectile.baseEntity.OwnerID);
            if (player == null) return;

            HandleQuestEvent(player, "mlrsrocketfire", "ammo.rocket.mlrs", 1);
        }

        private void OnItemUse(Item item, int amountToUse)
        {
            if (item == null || amountToUse <= 0) return;

            var player = item.GetRootContainer()?.playerOwner;
            if (player == null) return;

            if (item.info.category is not (ItemCategory.Food or ItemCategory.Medical)) return;

            HandleQuestEvent(player, "itemuse", item.info.shortname, amountToUse);
        }

        private void OnCardSwipe(CardReader cardReader, Keycard card, BasePlayer player)
        {
            if (player == null || card == null) return;
            var item = card.GetItem();
            if (item == null) return;

            HandleQuestEvent(player, "cardswipe", item.info.shortname, 1);
        }

        private void OnNpcGiveSoldItem(NPCVendingMachine machine, Item soldItem, BasePlayer buyer)
        {
            if (buyer == null) return;
            HandleQuestEvent(buyer, "purchasing", soldItem.info.shortname, 1);
            HandleQuestEvent(buyer, "purchasing", "", 1);
        }

        private void OnPlayerAttack(BasePlayer attacker, HitInfo info)
        {
            if (attacker == null || info?.HitEntity is not BasePlayer victim) return;
            if (attacker == victim) return;
            if (attacker.Team != null && attacker.Team.members.Contains(victim.userID)) return;

            var bodyPart = info.boneArea == (HitArea)(-1) ? "Body" : info.boneArea.ToString();

            HandleQuestEvent(attacker, "hit_area", bodyPart, 1);
        }

        private void OnRecyclingEnd(Recycler rec, BasePlayer player)
        {
            if (player == null) return;
            for (var i = 0; i < 6; i++)
            {
                var slot = rec.inventory.GetSlot(i);
                if (slot?.info.Blueprint is null) continue;

                var amount = slot.amount > 1 ? Mathf.CeilToInt(Mathf.Min(slot.amount, slot.MaxStackable() * 0.1f)) : 1;

                HandleQuestEvent(player, "recycling", slot.info.shortname, amount);
                HandleQuestEvent(player, "recycling", "", amount);
                break;
            }
        }

        private void OnGamblingDeposit(string typePrefix, BasePlayer player, int scrapPaid)
        {
            if (player == null) return;
            HandleQuestEvent(player, "gambling", $"{typePrefix}_deposit", scrapPaid);
        }

        private void OnGamblingWin(string typePrefix, BasePlayer player, int scrapPaid, int scrapRecieved)
        {
            if (player == null) return;
            HandleQuestEvent(player, "gambling", $"{typePrefix}_won", scrapRecieved);

            var profit = scrapRecieved - scrapPaid;
            if (profit > 0)
                HandleQuestEvent(player, "gambling", $"win", profit);
        }

        private void OnGamblingWin(string typePrefix, BasePlayer player, int scrapRecieved)
        {
            if (player == null) return;
            HandleQuestEvent(player, "gambling", $"{typePrefix}_won", scrapRecieved);

            HandleQuestEvent(player, "gambling", $"win", scrapRecieved);
        }

        private void OnEnterZone(string Id, BasePlayer player)
        {
            if (player == null) return;

            HandleQuestEvent(player, "enter_zone", Id, 1);
        }

        private void OnUserPermissionGranted(string id, string permName)
        {
            if (_config.General.AvailableBPPermission && permName == _config.General.BPPermission)
            {
                if (ulong.TryParse(id, out ulong steamId))
                {
                    var data = LoadPlayerData(steamId);
                    data.Tasks = GenerateTasks();
                }
            }
        }

        private void OnUserPermissionRevoked(string id, string permName)
        {
            if (_config.General.AvailableBPPermission && permName == _config.General.BPPermission)
            {
                if (ulong.TryParse(id, out ulong steamId))
                {
                    var data = LoadPlayerData(steamId);
                    data.Tasks.Clear();
                }
            }
        }

        #region Events

        private void OnRaidableBaseCompleted(Vector3 raidPos, int mode, bool allowPVP, string id, float spawnTime,
            float despawnTime, float loadTime, ulong ownerId, BasePlayer owner, List<BasePlayer> raiders,
            List<BasePlayer> intruders, List<BaseEntity> entities)
        {
            if (raiders == null)
                return;

            string mode_nane = mode switch
            {
                0 => "Easy",
                1 => "Medium",
                2 => "Hard",
                3 => "Expert",
                4 => "Nightmare",
                _ => "Easy"
            };

            foreach (var player in raiders)
            {
                HandleQuestEvent(player, "events", $"RaidableBase_{mode_nane}", 1);
            }
        }

        private void OnSpaceEventWin(ulong winnerId)
        {
            HandleQuestEvent(winnerId, "events", "SpaceEvent", 1);
        }

        private void OnArcticBaseEventWinner(ulong winnerId)
        {
            HandleQuestEvent(winnerId, "events", "ArcticBaseEvent", 1);
        }

        private void OnArmoredTrainEventWin(ulong winnerID)
        {
            HandleQuestEvent(winnerID, "events", "ArmoredTrainEvent", 1);
        }

        private void OnGasStationEventWinner(ulong winnerId)
        {
            HandleQuestEvent(winnerId, "events", "GasStationEvent", 1);
        }

        private void OnSatDishEventWinner(ulong winnerId)
        {
            HandleQuestEvent(winnerId, "events", "SatDishEvent", 1);
        }

        private void OnBossKilled(ScientistNPC boss, BasePlayer attacker)
        {
            HandleQuestEvent(attacker, "events", "BossMonster", 1);
        }

        private void OnJunkyardEventWinner(ulong winnerId)
        {
            HandleQuestEvent(winnerId, "events", "JunkyardEvent", 1);
        }

        private void OnPowerPlantEventWinner(ulong winnerId)
        {
            HandleQuestEvent(winnerId, "events", "PowerPlantEvent", 1);
        }

        private void OnAirEventWinner(ulong winnerId)
        {
            HandleQuestEvent(winnerId, "events", "AirEvent", 1);
        }

        private void OnHarborEventWinner(ulong winnerId)
        {
            HandleQuestEvent(winnerId, "events", "HarborEvent", 1);
        }

        private void OnWaterEventWinner(ulong winnerId)
        {
            HandleQuestEvent(winnerId, "events", "WaterEvent", 1);
        }

        private void OnConvoyEventWin(ulong winnerId)
        {
            HandleQuestEvent(winnerId, "events", "ConvoyEvent", 1);
        }

        private void OnCaravanEventWin(ulong winnerId)
        {
            HandleQuestEvent(winnerId, "events", "CaravanEvent", 1);
        }

        private void OnSputnikEventWin(ulong winnerId)
        {
            HandleQuestEvent(winnerId, "events", "SputnikEvent", 1);
        }

        private void OnShipwreckEventWin(ulong winnerId)
        {
            HandleQuestEvent(winnerId, "events", "Shipwreck", 1);
        }

        private void OnTriangulationWinner(ulong winnerId)
        {
            HandleQuestEvent(winnerId, "events", "TriangulationEvent", 1);
        }

        private void OnFerryTerminalEventWinner(ulong winnerId)
        {
            HandleQuestEvent(winnerId, "events", "FerryEvent", 1);
        }

        private void OnSupermarketEventWinner(ulong winnerId)
        {
            HandleQuestEvent(winnerId, "events", "MarketEvent", 1);
        }

        private void OnPaintballTeamWin(ulong userId)
        {
            HandleQuestEvent(userId, "events", "Paintball", 1);
        }

        private void OnGunGameWin(ulong userId)
        {
            HandleQuestEvent(userId, "events", "GunGame", 1);
        }

        private void OnTugboatPiratesCompleted(ulong userId)
        {
            HandleQuestEvent(userId, "events", "Tugboat", 1);
        }

        private void OnDungeonWin(ulong userId)
        {
            HandleQuestEvent(userId, "events", "Dungeon", 1);
        }

        private void OnFlyingCargoCompleted(ulong userId)
        {
            HandleQuestEvent(userId, "events", "FlyingCargo", 1);
        }

        private void OnAbandonedBaseEnded(Vector3 pos, float rad, bool pvp, List<BasePlayer> participants,
            List<ulong> ids, List<BaseEntity> ents)
        {
            if (ids == null) return;
            foreach (ulong id in ids)
            {
                HandleQuestEvent(id, "events", "AbandonedBase", 1);
            }
        }

        private void AirfieldEventWinner(BasePlayer player)
        {
            if (player == null) return;
            HandleQuestEvent(player, "events", "AirfieldEvent", 1);
        }

        private void OnSurvivalArenaWin(BasePlayer player)
        {
            if (player == null) return;
            HandleQuestEvent(player, "events", "SurvivalArena", 1);
        }

        #endregion

        #region Harmony Patches

        [AutoPatch]
        [HarmonyPatch(typeof(Recycler), "RecycleThink")]
        private static class RecycleThinkPatch
        {
            [HarmonyPrefix]
            private static void Prefix(Recycler __instance)
            {
                if (__instance == null) return;

                var player = __instance.LastLootedByPlayer;
                if (player == null) return;

                Interface.Oxide.CallHook("OnRecyclingEnd", __instance, player);
            }
        }

        [AutoPatch]
        [HarmonyPatch]
        private static class GamblingPatches
        {
            [HarmonyPatch(typeof(Facepunch.Rust.Analytics.Azure), "OnGamblingResult")]
            [HarmonyPrefix]
            private static void OnGamblingResultPrefix(BasePlayer player, BaseEntity entity, int scrapPaid,
                int scrapRecieved, Guid? gambleGroupId)
            {
                if (player == null || entity == null) return;

                if (entity is not (BigWheelBettingTerminal or SlotMachine)) return;
                var typePrefix = entity is BigWheelBettingTerminal ? "Wheel" : "Slot";

                if (scrapPaid > 0)
                    Interface.Oxide.CallHook("OnGamblingDeposit", typePrefix, player, scrapPaid);

                if (scrapRecieved <= 0) return;

                Interface.Oxide.CallHook("OnGamblingWin", typePrefix, player, scrapPaid, scrapRecieved);
            }


            [HarmonyPatch(typeof(Facepunch.CardGames.CardGameController), "TryMoveToPotStorage")]
            [HarmonyPostfix]
            private static void TryMoveToPotStoragePostfix(Facepunch.CardGames.CardGameController __instance,
                Facepunch.CardGames.CardPlayerData playerData, int maxAmount, ref int __result)
            {
                if (__result <= 0 || playerData == null) return;

                var typePrefix = __instance.GetType().Name.Contains("Blackjack") ? "Blackjack" : "Pocker";

                var player = BasePlayer.FindByID(playerData.UserID);
                if (player == null) return;

                Interface.Oxide.CallHook("OnGamblingDeposit", typePrefix, player, __result);
            }


            [HarmonyPatch(typeof(Facepunch.CardGames.CardGameController), "PayOutFromPot")]
            [HarmonyPostfix]
            private static void PayOutFromPotPostfix(Facepunch.CardGames.CardGameController __instance,
                Facepunch.CardGames.CardPlayerData playerData, int maxAmount, ref int __result)
            {
                if (__result <= 0 || playerData == null) return;

                var typePrefix = __instance.GetType().Name.Contains("Blackjack") ? "Blackjack" : "Pocker";

                var player = BasePlayer.FindByID(playerData.UserID);
                if (player == null) return;

                Interface.Oxide.CallHook("OnGamblingWin", typePrefix, player, __result);
            }
        }

        #endregion

        #region IntegrationSkillTree
        private object STCanGainXP(BasePlayer player, BaseEntity source, double value, string sourceString)
        {
            if(player == null) return null;
            
            if(!_config.IntegrationST.isEnableIntegration || _config.IntegrationST.Mode == SkillTreeIntegrationMode.BpToSk) return null;

            if(sourceString == "BattlePassReborn") return null;

            GiveEventExp(player.userID, (int) value);

            return null;
        }
        #endregion

        #endregion

        #region Function

        private void HandleQuestEvent(BasePlayer player, string eventType, string target, int progress)
        {
            if (player == null || player.userID == 0) return;

            var data = LoadPlayerData(player.userID);
            if (data.Tasks == null) return;

            var isPremium = permission.UserHasPermission(player.UserIDString, _config.General.PREMIUMRewardPermission);

            var countMission = isPremium ? _config.Tasks.DailyTasksPremium : _config.Tasks.DailyTasks;

            if (data.CountCompletedTask >= countMission && _config.Tasks.DailyTasks != 0)
                return;

            var missionMap = _allMissions.ToDictionary(m => m.TaskID);

            foreach (var playerTask in data.Tasks.Where(t => !t.Completed))
            {
                if (!missionMap.TryGetValue(playerTask.TaskID, out var mission)) continue;

                var isCondition = mission.EventType == "extract" ? mission.Conditions.Contains(target) : mission.Conditions.Any(cond => target.Contains(cond));

                if (mission.EventType == eventType && isCondition)
                {
                    playerTask.Progress += progress;


                    if (playerTask.Progress >= mission.TargetCount)
                    {
                        playerTask.Completed = true;
                        playerTask.Progress = mission.TargetCount;
                        data.CountCompletedTask++;

                        int exp = RewardExp(mission.Difficulty);
                        if (_config.Premium.EnablePremiumBonus && isPremium)
                            exp = (int)(exp * _config.Premium.XpMultiplier);

                        player.ChatMessage(GetLang("TEXT_TASKCOMPLETED", player.UserIDString,
                            GetLang(mission.TaskID, player.UserIDString), exp));
                        GiveExp(data, exp, player);
                        if(_config.IntegrationST.isEnableIntegration && (_config.IntegrationST.Mode == SkillTreeIntegrationMode.BpToSk || _config.IntegrationST.Mode == SkillTreeIntegrationMode.Full))
                            Interface.CallHook("AwardXP", player, (double)exp, "BattlePassReborn", false, true, "BattlePassReborn");

                        if (_config.Tasks.AddNewTaskAfterComplete)
                            AddNewTaskAfterComplete(data, playerTask.TaskID);
                    }
                }
            }
        }

        private void HandleQuestEvent(ulong userID, string eventType, string target, int progress)
        {
            var player = BasePlayer.FindByID(userID);
            if (player == null) return;

            HandleQuestEvent(player, eventType, target, progress);
        }

        private void HandleQuestEvent(BasePlayer player, string eventType, string target, string weapon, int progress)
        {
            if (player == null || player.userID == 0) return;

            var data = LoadPlayerData(player.userID);
            if (data.Tasks == null) return;

            var isPremium = permission.UserHasPermission(player.UserIDString, _config.General.PREMIUMRewardPermission);

            var countMission = isPremium ? _config.Tasks.DailyTasksPremium : _config.Tasks.DailyTasks;


            if (data.CountCompletedTask >= countMission && _config.Tasks.DailyTasks != 0)
                return;

            var missionMap = _allMissions.ToDictionary(m => m.TaskID);

            foreach (var playerTask in data.Tasks.Where(t => !t.Completed))
            {
                if (!missionMap.TryGetValue(playerTask.TaskID, out var mission)) continue;

                if (mission.EventType == eventType && mission.Conditions.Any(cond => target.Contains(cond)) &&
                    mission.Conditions.Any(cond => weapon.Contains(cond)))
                {
                    playerTask.Progress += progress;


                    if (playerTask.Progress >= mission.TargetCount)
                    {
                        playerTask.Completed = true;
                        playerTask.Progress = mission.TargetCount;
                        data.CountCompletedTask++;

                        int exp = RewardExp(mission.Difficulty);
                        if (_config.Premium.EnablePremiumBonus && isPremium)
                            exp = (int)(exp * _config.Premium.XpMultiplier);

                        player.ChatMessage(GetLang("TEXT_TASKCOMPLETED", player.UserIDString,
                            GetLang(mission.TaskID, player.UserIDString), exp));
                        GiveExp(data, exp, player);
                        if(_config.IntegrationST.isEnableIntegration && (_config.IntegrationST.Mode == SkillTreeIntegrationMode.BpToSk || _config.IntegrationST.Mode == SkillTreeIntegrationMode.Full))
                            Interface.CallHook("AwardXP", player, (double)exp, "BattlePassReborn", false, true, "BattlePassReborn");

                        if (_config.Tasks.AddNewTaskAfterComplete)
                            AddNewTaskAfterComplete(data, playerTask.TaskID);
                    }
                }
            }
        }

        private int RewardExp(TaskDifficulty diff)
        {
            return diff switch
            {
                TaskDifficulty.Easy => _config.Tasks.ExpForTasks.Easy,
                TaskDifficulty.Medium => _config.Tasks.ExpForTasks.Medium,
                TaskDifficulty.Hard => _config.Tasks.ExpForTasks.Hard,
                _ => _config.Tasks.ExpForTasks.Easy
            };
        }

        private void GiveExp(PlayerData data, int exp, BasePlayer player = null)
        {
            var settings = _allLevels.FirstOrDefault(p => p.LevelID == data.Level + 1);
            if (settings == null)
            {
                if (_config.General.TotalLevels == data.Level)
                {
                    data.Exp = 0;
                    RefreshProgressBarUI(player, data);
                    return;
                }

                settings = _allLevels.LastOrDefault();
            }

            data.Exp += exp;

            RefreshProgressBarUI(player, data);
            var expPerLevel = settings.ExpForLevel > 0 ? settings.ExpForLevel : _config.General.ExpPerLevel;
            if (data.Exp < expPerLevel) return;

            data.Level++;
            data.Exp -= expPerLevel;

            player.ChatMessage(GetLang("TEXT_GAINEDLVL", player.UserIDString, data.Level));
            if (_config.Discord.LogLevelUp)
                SendDiscordSimple(GetLang("TEXT_GAINEDLVLDISCORD", player.UserIDString, player.displayName, player.UserIDString, data.Level));
            
            GiveExp(data, 0, player);
        }

        private void GiveEventExp(ulong userID, int exp)
        {
            if (userID == 0) return;

            var player = BasePlayer.FindByID(userID);
            if (player == null) return;

            var data = LoadPlayerData(userID);

            GiveExp(data, exp, player);
        }

        private DateTime GetFirstThursdayOfMonth(int year, int month)
        {
            var firstDay = new DateTime(year, month, 1);
            var daysToAdd = (DayOfWeek.Thursday - firstDay.DayOfWeek + 7) % 7;
            return firstDay.AddDays(daysToAdd);
        }

        private void ResetProgressOnDate()
        {
            var today = DateTime.Today;
            if (today == _config.Wipe.ResetProgressOnDate)
            {
                ArchiveData(true);
                var nextMonth = today.AddMonths(1);
                var firstThursdayNextMonth = GetFirstThursdayOfMonth(nextMonth.Year, nextMonth.Month);

                _config.Wipe.ResetProgressOnDate = firstThursdayNextMonth;
                SaveConfig();

                foreach (var check in BasePlayer.activePlayerList) OnPlayerConnected(check);
            }
        }

        #endregion
    }


    #region UI

    partial class BattlePassReborn
    {
        private void ShowMainUI(BasePlayer player)
        {
            var isPremium = permission.UserHasPermission(player.UserIDString, _config.General.PREMIUMRewardPermission);
            var data = LoadPlayerData(player.userID);
            var settings = _allLevels.FirstOrDefault(p => p.LevelID == data.Level + 1);
            if (settings == null)
            {
                settings = _allLevels.LastOrDefault();
            }

            var expPerLevel = settings.ExpForLevel > 0 ? settings.ExpForLevel : _config.General.ExpPerLevel;

            BattlePassRebornUI.Builder.Root root = new BattlePassRebornUI.Builder.Root("OverlayNonScaled");
            {
                BattlePassRebornUI.Builder.Element suCepD = root.AddContainer(
                    anchorMin: "0 0",
                    anchorMax: "1 1",
                    offsetMin: "0 0",
                    offsetMax: "0 0",
                    name: "Main");
                if (!string.IsNullOrEmpty(_UISettings.BackGroundURL))
                {
                    BattlePassRebornUI.Builder.Element BRBagG = suCepD.AddImage(
                        content: GetImage("BG_BRBagG"),
                        material: "",
                        anchorMin: "0 0",
                        anchorMax: "1 1",
                        offsetMin: "0 0",
                        offsetMax: "0 0",
                        name: "BG");
                }

                BattlePassRebornUI.Builder.Element KpZTtS = suCepD.AddImage(
                    content: GetImage("img_shadow_KpZTtS"),
                    material: "",
                    anchorMin: "0 1",
                    anchorMax: "0 1",
                    offsetMin: "0 -241",
                    offsetMax: "440 -87",
                    name: "img_shadow");
                {
                    BattlePassRebornUI.Builder.Element iQmAri = suCepD.AddPanel(
                        sprite: "",
                        material: "assets/content/ui/menuui/mainmenu.panel.mat",
                        color: "0 0 0 0.9309",
                        imageType: UnityEngine.UI.Image.Type.Simple,
                        anchorMin: "0 1",
                        anchorMax: "1 1",
                        offsetMin: "0 -47",
                        offsetMax: "0 0",
                        cursorEnabled: true,
                        keyboardEnabled: true,
                        name: "Header");
                    BattlePassRebornUI.Builder.Element uCrLQN = iQmAri.AddPanel(
                        sprite: "",
                        material: "",
                        color: "0.4245283 0.4245283 0.4245283 1",
                        imageType: UnityEngine.UI.Image.Type.Simple,
                        anchorMin: "0 0",
                        anchorMax: "1 0",
                        offsetMin: "0 0",
                        offsetMax: "0 1",
                        name: "Panel");
                    {
                        BattlePassRebornUI.Builder.Element ehBQth = iQmAri.AddButton(
                            command: null,
                            close: "Main",
                            color: "0.1333333 0.1333333 0.1333333 1",
                            imageType: UnityEngine.UI.Image.Type.Simple,
                            anchorMin: "1 0",
                            anchorMax: "1 1",
                            offsetMin: "-113 10",
                            offsetMax: "-43 -10",
                            name: "Btn close");
                        BattlePassRebornUI.Builder.Element hRNrFI = ehBQth.AddText(
                            text: GetLang("TEXT_CLOSE", player.UserIDString),
                            color: "0.7294118 0.6941177 0.6588235 1",
                            font: BattlePassRebornUI.Builder.Font.RobotoCondensedBold,
                            fontSize: 10,
                            align: TextAnchor.MiddleLeft,
                            overflow: VerticalWrapMode.Truncate,
                            anchorMin: "0 1",
                            anchorMax: "0 1",
                            offsetMin: "27 -27",
                            offsetMax: "70 0",
                            name: "Text");
                        BattlePassRebornUI.Builder.Element OxvJwN = ehBQth.AddImage(
                            content: GetImage("Image_OxvJwN"),
                            material: "",
                            anchorMin: "0 1",
                            anchorMax: "0 1",
                            offsetMin: "9 -20",
                            offsetMax: "22 -7",
                            name: "Image");
                        BattlePassRebornUI.Builder.Element qFZEBk = ehBQth.AddPanel(
                            sprite: "",
                            material: "",
                            color: "0.1137255 0.1137255 0.1137255 1",
                            imageType: UnityEngine.UI.Image.Type.Simple,
                            anchorMin: "0 0",
                            anchorMax: "1 0",
                            offsetMin: "0 0",
                            offsetMax: "0 1",
                            name: "Panel");
                    }
                    {
                        BattlePassRebornUI.Builder.Element ehBQth = iQmAri.AddButton(
                            command: "UI_STORAGE",
                            color: "0.1333333 0.1333333 0.1333333 1",
                            imageType: UnityEngine.UI.Image.Type.Simple,
                            anchorMin: "1 0",
                            anchorMax: "1 1",
                            offsetMin: "-198 10",
                            offsetMax: "-123 -10",
                            name: "Btn storage");
                        BattlePassRebornUI.Builder.Element hRNrFI = ehBQth.AddText(
                            text: GetLang("TEXT_STORAGE", player.UserIDString),
                            color: "0.7294118 0.6941177 0.6588235 1",
                            font: BattlePassRebornUI.Builder.Font.RobotoCondensedBold,
                            fontSize: 10,
                            align: TextAnchor.MiddleLeft,
                            overflow: VerticalWrapMode.Truncate,
                            anchorMin: "0 1",
                            anchorMax: "0 1",
                            offsetMin: "27 -27",
                            offsetMax: "70 0",
                            name: "Text");
                        BattlePassRebornUI.Builder.Element OxvJwN = ehBQth.AddImage(
                            content: GetImage("Frame"),
                            material: "",
                            anchorMin: "0 1",
                            anchorMax: "0 1",
                            offsetMin: "9 -20",
                            offsetMax: "22 -7",
                            name: "Image");
                        BattlePassRebornUI.Builder.Element qFZEBk = ehBQth.AddPanel(
                            sprite: "",
                            material: "",
                            color: "0.1137255 0.1137255 0.1137255 1",
                            imageType: UnityEngine.UI.Image.Type.Simple,
                            anchorMin: "0 0",
                            anchorMax: "1 0",
                            offsetMin: "0 0",
                            offsetMax: "0 1",
                            name: "Panel");
                    }
                }
                {
                    BattlePassRebornUI.Builder.Element EqwEyw = suCepD.AddContainer(
                        anchorMin: "0.5 0.5",
                        anchorMax: "0.5 0.5",
                        offsetMin: "-597 -268",
                        offsetMax: "597 268",
                        name: "content");
                    BattlePassRebornUI.Builder.Element UNOaIT = EqwEyw.AddText(
                        text: GetLang("TEXT_BP", player.UserIDString),
                        font: BattlePassRebornUI.Builder.Font.RobotoCondensedBold,
                        fontSize: 40,
                        align: TextAnchor.MiddleLeft,
                        overflow: VerticalWrapMode.Overflow,
                        anchorMin: "0 1",
                        anchorMax: "0 1",
                        offsetMin: "0 -47",
                        offsetMax: "357 0",
                        name: "Title");
                    BattlePassRebornUI.Builder.Element gEhUKg = EqwEyw.AddText(
                        text: GetLang("TEXT_DESCR", player.UserIDString),
                        font: BattlePassRebornUI.Builder.Font.RobotoCondensedBold,
                        fontSize: 16,
                        align: TextAnchor.MiddleLeft,
                        overflow: VerticalWrapMode.Overflow,
                        anchorMin: "0 1",
                        anchorMax: "0 1",
                        offsetMin: "0 -114",
                        offsetMax: "357 -57",
                        name: "description");
                    {
                        BattlePassRebornUI.Builder.Element HgMZGJ = EqwEyw.AddContainer(
                            anchorMin: "0 1",
                            anchorMax: "0 1",
                            offsetMin: "0 -300",
                            offsetMax: "357 -144",
                            name: "premium_baner");
                        BattlePassRebornUI.Builder.Element jpGRHv = HgMZGJ.AddImage(
                            content: GetImage("Image_jpGRHv"),
                            material: "",
                            anchorMin: "0 0",
                            anchorMax: "1 1",
                            offsetMin: "0 0",
                            offsetMax: "0 0",
                            name: "Image");
                        BattlePassRebornUI.Builder.Element zMbNCU = HgMZGJ.AddText(
                            text: GetLang("TEXT_PP", player.UserIDString),
                            color: "0.6156863 0.8117647 0.2705882 1",
                            font: BattlePassRebornUI.Builder.Font.RobotoCondensedBold,
                            fontSize: 20,
                            align: TextAnchor.MiddleLeft,
                            overflow: VerticalWrapMode.Overflow,
                            anchorMin: "0 1",
                            anchorMax: "0 1",
                            offsetMin: "20 -43",
                            offsetMax: "225 -20",
                            name: "premium_title");
                        BattlePassRebornUI.Builder.Element EfkgJN = HgMZGJ.AddText(
                            text: GetLang("TEXT_PREMIUMDESCR", player.UserIDString),
                            font: BattlePassRebornUI.Builder.Font.RobotoCondensedBold,
                            fontSize: 12,
                            align: TextAnchor.UpperLeft,
                            overflow: VerticalWrapMode.Overflow,
                            anchorMin: "0 1",
                            anchorMax: "0 1",
                            offsetMin: "20 -144",
                            offsetMax: "225 -51",
                            name: "premium_description");
                        {
                            BattlePassRebornUI.Builder.Element diXquO = HgMZGJ.AddButton(
                                command: "UI_PremiumPassBaner",
                                color: _UISettings.PremBanner.ColorPanel,
                                sprite: "",
                                material: "",
                                imageType: UnityEngine.UI.Image.Type.Simple,
                                anchorMin: "0 1",
                                anchorMax: "0 1",
                                offsetMin: _UISettings.PremBanner.offsetMin,
                                offsetMax: _UISettings.PremBanner.offsetMax,
                                name: "premium_btn");
                            BattlePassRebornUI.Builder.Element svSTIf = diXquO.AddText(
                                text: GetLang("TEXT_BUYPASS", player.UserIDString),
                                color: _UISettings.PremBanner.ColorText,
                                font: BattlePassRebornUI.Builder.Font.RobotoCondensedBold,
                                fontSize: 10,
                                align: TextAnchor.MiddleCenter,
                                overflow: VerticalWrapMode.Truncate,
                                anchorMin: "0.5 0.5",
                                anchorMax: "0.5 0.5",
                                offsetMin: "-60 -13",
                                offsetMax: "60 13",
                                name: "Text");
                        }
                    }

                    {
                        BattlePassRebornUI.Builder.Element xYuKDj = EqwEyw.AddContainer(
                            anchorMin: "0 1",
                            anchorMax: "0 1",
                            offsetMin: "397 -300",
                            offsetMax: "1194 0",
                            name: "challenges");
                        BattlePassRebornUI.Builder.Element qqKgoS = xYuKDj.AddPanel(
                            sprite: "",
                            material: "",
                            color: "0 0 0 0.7014",
                            imageType: UnityEngine.UI.Image.Type.Simple,
                            anchorMin: "0 0",
                            anchorMax: "1 1",
                            offsetMin: "0 0",
                            offsetMax: "0 0",
                            name: "challenges_bg");
                        BattlePassRebornUI.Builder.Element xYuKTT = xYuKDj.AddContainer(
                            anchorMin: "0 0",
                            anchorMax: "1 1",
                            offsetMin: "0 0",
                            offsetMax: "0 0",
                            name: "challenges_text");
                        BattlePassRebornUI.Builder.Element fapmNb = xYuKDj.AddText(
                            text: GetLang("TEXT_DAILYCHALL", player.UserIDString),
                            font: BattlePassRebornUI.Builder.Font.RobotoCondensedBold,
                            fontSize: 20,
                            align: TextAnchor.UpperLeft,
                            overflow: VerticalWrapMode.Overflow,
                            anchorMin: "0 1",
                            anchorMax: "0 1",
                            offsetMin: "12 -47",
                            offsetMax: "520 -17",
                            name: "challenges_title");

                        if (_config.Tasks.RefreshTask)
                        {
                            var countRefreshes = isPremium
                                ? _config.Tasks.TaskRefreshPremiumCount
                                : _config.Tasks.TaskRefreshCount;
                            BattlePassRebornUI.Builder.Element oUbrBa = xYuKTT.AddText(
                                text: GetLang("TEXT_DAILYREROLLS", player.UserIDString, data.CountRefresh,
                                    countRefreshes),
                                font: BattlePassRebornUI.Builder.Font.RobotoCondensedBold,
                                fontSize: 12,
                                align: TextAnchor.UpperRight,
                                overflow: VerticalWrapMode.Overflow,
                                anchorMin: "0 1",
                                anchorMax: "0 1",
                                offsetMin: "576 -44",
                                offsetMax: "776 -24",
                                name: "rerolls_info");
                        }

                        {
                            BattlePassRebornUI.Builder.Element GvTOGj = xYuKDj.AddContainer(
                                anchorMin: "0 1",
                                anchorMax: "0 1",
                                offsetMin: "12 -288",
                                offsetMax: "784 -57",
                                name: "challenges_container");
                            int easyMinY = -69, easyMaxY = 0, easyMinX = 0, easyMaxX = 244;
                            int mediumMinY = -69, mediumMaxY = 0, mediumMinX = 264, mediumMaxX = 508;
                            int hardMinY = -69, hardMaxY = 0, hardMinX = 528, hardMaxX = 772;
                            int i = 0;

                            foreach (var task in data.Tasks)
                            {
                                var mission = _allMissions.FirstOrDefault(x => x.TaskID == task.TaskID);
                                if (mission == null)
                                    continue;
                                int minY = 0, maxY = 0, minX = 0, maxX = 0;
                                string bg_Image = "", status = task.Completed ? "status_fScNCW" : "status_UBWXHE";

                                switch (mission.Difficulty)
                                {
                                    case TaskDifficulty.Easy:
                                        minY = easyMinY;
                                        maxY = easyMaxY;
                                        minX = easyMinX;
                                        maxX = easyMaxX;
                                        easyMinY -= 81;
                                        easyMaxY -= 81;
                                        bg_Image = "bg_UkKjRE";
                                        break;

                                    case TaskDifficulty.Medium:
                                        minY = mediumMinY;
                                        maxY = mediumMaxY;
                                        minX = mediumMinX;
                                        maxX = mediumMaxX;
                                        mediumMinY -= 81;
                                        mediumMaxY -= 81;
                                        bg_Image = "bg_PeBYrm";
                                        break;

                                    case TaskDifficulty.Hard:
                                        minY = hardMinY;
                                        maxY = hardMaxY;
                                        minX = hardMinX;
                                        maxX = hardMaxX;
                                        hardMinY -= 81;
                                        hardMaxY -= 81;
                                        bg_Image = "bg_xGzFMA";
                                        break;

                                    default:
                                        continue;
                                }

                                BattlePassRebornUI.Builder.Element qJtrzc = GvTOGj.AddContainer(
                                    anchorMin: "0 1",
                                    anchorMax: "0 1",
                                    offsetMin: $"{minX} {minY}",
                                    offsetMax: $"{maxX} {maxY}",
                                    name: $"easy_challenges_card_task{i}");
                                BattlePassRebornUI.Builder.Element UkKjRE = qJtrzc.AddImage(
                                    content: GetImage(bg_Image),
                                    material: "",
                                    anchorMin: "0 0",
                                    anchorMax: "1 1",
                                    offsetMin: "0 0",
                                    offsetMax: "0 0",
                                    name: $"bg_task{i}");
                                BattlePassRebornUI.Builder.Element QODSLl = qJtrzc.AddText(
                                    text: GetLang(mission.TaskID, player.UserIDString),
                                    font: BattlePassRebornUI.Builder.Font.RobotoCondensedBold,
                                    fontSize: 12,
                                    align: TextAnchor.UpperLeft,
                                    overflow: VerticalWrapMode.Overflow,
                                    anchorMin: "0 1",
                                    anchorMax: "0 1",
                                    offsetMin: "13 -27",
                                    offsetMax: "183 -13",
                                    name: $"callenges_name_task{i}");
                                BattlePassRebornUI.Builder.Element iRfTPg = qJtrzc.AddText(
                                    text: $"{task.Progress}/{mission.TargetCount}",
                                    color: "0.7058824 0.7058824 0.7058824 1",
                                    font: BattlePassRebornUI.Builder.Font.RobotoCondensedRegular,
                                    fontSize: 10,
                                    align: TextAnchor.UpperLeft,
                                    overflow: VerticalWrapMode.Overflow,
                                    anchorMin: "0 1",
                                    anchorMax: "0 1",
                                    offsetMin: "13 -61",
                                    offsetMax: "183 -49",
                                    name: $"callenges_info_otional_task{i}");
                                BattlePassRebornUI.Builder.Element fScNCW = qJtrzc.AddImage(
                                    content: GetImage(status),
                                    material: "",
                                    anchorMin: "0 1",
                                    anchorMax: "0 1",
                                    offsetMin: "189 -55",
                                    offsetMax: "231 -13",
                                    name: $"status_task{i}");
                                {
                                    if (!task.Completed && _config.Tasks.RefreshTask)
                                    {
                                        BattlePassRebornUI.Builder.Element zvCIHY = qJtrzc.AddButton(
                                            command: $"UI_REFRESH_TASK {task.TaskID}",
                                            color: "1 1 1 0.0999",
                                            sprite: "",
                                            material: "",
                                            imageType: UnityEngine.UI.Image.Type.Simple,
                                            anchorMin: "0 1",
                                            anchorMax: "0 1",
                                            offsetMin: "224 -61",
                                            offsetMax: "236 -49",
                                            name: $"reroll_task{i}");
                                        BattlePassRebornUI.Builder.Element zZtJnt = zvCIHY.AddImage(
                                            content: GetImage("Image_zZtJnt"),
                                            material: "",
                                            anchorMin: "0 0",
                                            anchorMax: "1 1",
                                            offsetMin: "0 0",
                                            offsetMax: "0 0",
                                            name: $"Image_task{i}");
                                    }
                                }
                                i++;
                                if (i == 9)
                                    break;
                            }
                        }
                        var countMission = isPremium ? _config.Tasks.DailyTasksPremium : _config.Tasks.DailyTasks;
                        if (data.CountCompletedTask >= countMission && _config.Tasks.DailyTasks != 0)
                        {
                            BattlePassRebornUI.Builder.Element YiMLYM = xYuKDj.AddPanel(
                                sprite: "",
                                material: "",
                                color: "0 0 0 0.9377",
                                imageType: UnityEngine.UI.Image.Type.Simple,
                                anchorMin: "0 0",
                                anchorMax: "1 1",
                                offsetMin: "0 0",
                                offsetMax: "0 0",
                                name: $"task_block");
                            BattlePassRebornUI.Builder.Element eUmWKQ = YiMLYM.AddText(
                                text: GetLang("TEXT_TASK_BLOCK", player.UserIDString),
                                font: BattlePassRebornUI.Builder.Font.RobotoCondensedBold,
                                fontSize: 20,
                                align: TextAnchor.MiddleCenter,
                                overflow: VerticalWrapMode.Overflow,
                                anchorMin: "0 0.5",
                                anchorMax: "1 0.5",
                                offsetMin: "0 -40",
                                offsetMax: "0 0",
                                name: $"Text_task_block");
                            BattlePassRebornUI.Builder.Element iUQHgg = YiMLYM.AddImage(
                                content: GetImage("Image_iUQHgg"),
                                material: "",
                                anchorMin: "0.5 0.5",
                                anchorMax: "0.5 0.5",
                                offsetMin: "-16 10",
                                offsetMax: "16 42",
                                name: $"Image_task_block");
                        }
                    }

                    {
                        BattlePassRebornUI.Builder.Element njbGtl = EqwEyw.AddContainer(
                            anchorMin: "0 0",
                            anchorMax: "1 0",
                            offsetMin: "0 0",
                            offsetMax: "0 206",
                            name: "reward_container");
                        {
                            BattlePassRebornUI.Builder.Element PQxotn = njbGtl.AddContainer(
                                anchorMin: "0 1",
                                anchorMax: "1 1",
                                offsetMin: "0 -26",
                                offsetMax: "0 0",
                                name: "progress_info");
                            BattlePassRebornUI.Builder.Element ZYiSSE = PQxotn.AddText(
                                text: GetLang("TEXT_TIER", player.UserIDString, data.Level),
                                font: BattlePassRebornUI.Builder.Font.RobotoCondensedBold,
                                fontSize: 12,
                                align: TextAnchor.MiddleLeft,
                                overflow: VerticalWrapMode.Overflow,
                                anchorMin: "0 1",
                                anchorMax: "0 1",
                                offsetMin: "10 -23",
                                offsetMax: "80 0",
                                name: "next_tier");
                            BattlePassRebornUI.Builder.Element ZcKrvn = PQxotn.AddText(
                                text: $"{data.Exp}/{expPerLevel}",
                                font: BattlePassRebornUI.Builder.Font.RobotoCondensedBold,
                                fontSize: 12,
                                align: TextAnchor.MiddleRight,
                                overflow: VerticalWrapMode.Overflow,
                                anchorMin: "0 1",
                                anchorMax: "0 1",
                                offsetMin: "80 -23",
                                offsetMax: "160 0",
                                name: "point");
                            BattlePassRebornUI.Builder.Element cMKVKe = PQxotn.AddText(
                                text: GetLang("TEXT_XP", player.UserIDString),
                                font: BattlePassRebornUI.Builder.Font.RobotoCondensedBold,
                                fontSize: 12,
                                align: TextAnchor.MiddleLeft,
                                overflow: VerticalWrapMode.Overflow,
                                anchorMin: "0 1",
                                anchorMax: "0 1",
                                offsetMin: "165 -23",
                                offsetMax: "190 0",
                                name: "XP");
                            {
                                BattlePassRebornUI.Builder.Element AeeJUL = PQxotn.AddContainer(
                                    anchorMin: "0 0",
                                    anchorMax: "1 0",
                                    offsetMin: "0 0",
                                    offsetMax: "0 3",
                                    name: "progess_bar");
                                BattlePassRebornUI.Builder.Element bFpBNN = AeeJUL.AddPanel(
                                    sprite: "",
                                    material: "",
                                    color: "0.05882353 0.0627451 0.0627451 0.7977",
                                    imageType: UnityEngine.UI.Image.Type.Simple,
                                    anchorMin: "0 0",
                                    anchorMax: "1 1",
                                    offsetMin: "0 0",
                                    offsetMax: "0 0",
                                    name: "bg");
                                BattlePassRebornUI.Builder.Element VQXSTy = AeeJUL.AddPanel(
                                    sprite: "",
                                    material: "",
                                    color: "0.3647059 0.4470588 0.2196078 1",
                                    imageType: UnityEngine.UI.Image.Type.Simple,
                                    anchorMin: "0 0",
                                    anchorMax: "0 1",
                                    offsetMin: "0 0",
                                    offsetMax: $"{1194 * data.Exp / expPerLevel} 0",
                                    name: "Panel");
                            }
                        }
                        {
                            BattlePassRebornUI.Builder.Element sQXNjh = njbGtl.AddContainer(
                                anchorMin: "0 0",
                                anchorMax: "1 0",
                                offsetMin: "0 0",
                                offsetMax: "0 172",
                                name: "reward_scroll");

                            var scrollRoot = sQXNjh.AddPanel(
                                sprite: "",
                                material: "",
                                color: "1 1 1 0",
                                anchorMin: "0 0",
                                anchorMax: "1 1",
                                offsetMin: "0 0",
                                offsetMax: "0 0",
                                name: "ScrollRootRewards");

                            var totalRewardsCount = _allLevels.Sum(lvl =>
                            {
                                var nonPremium = lvl.RewardList.FirstOrDefault(r => !r.IsPremium);
                                var premium = lvl.RewardList.FirstOrDefault(r => r.IsPremium);
                                return (nonPremium != null ? 1 : 0) + (premium != null ? 1 : 0);
                            });

                            scrollRoot.Components.AddScrollView(
                                horizontal: true,
                                vertical: false,
                                inertia: true,
                                movementType: ScrollRect.MovementType.Elastic,
                                decelerationRate: 0.1f,
                                elasticity: 0.1f,
                                scrollSensitivity: 10f,
                                horizontalScrollbar: new CuiScrollbar
                                {
                                    Invert = true,
                                    HandleColor = "#FFFFFFFF",
                                    HighlightColor = "#AAAAAAFF",
                                    PressedColor = "#888888FF",
                                    TrackColor = "#00000080",
                                    HandleSprite = "assets/content/ui/ui.background.tiletex.psd",
                                    TrackSprite = "assets/content/ui/ui.background.tiletex.psd",
                                    AutoHide = true,
                                    Size = -1
                                },
                                anchorMin: "0 1",
                                anchorMax: "1 1",
                                offsetMin: "0 0",
                                offsetMax: $"{1194 * (int)(totalRewardsCount / 11)} 0");

                            var scrollRootPanel = scrollRoot.AddPanel(
                                sprite: "",
                                material: "",
                                color: "1 1 1 0",
                                anchorMin: "0 0",
                                anchorMax: "1 1",
                                offsetMin: "0 0",
                                offsetMax: "0 0",
                                name: "ScrollRootPanel");

                            {
                                int i = 0, minX = 0;
                                foreach (var lvl in _allLevels)
                                {
                                    var firstNonPremium = lvl.RewardList.FirstOrDefault(r => !r.IsPremium);
                                    var firstPremium = lvl.RewardList.FirstOrDefault(r => r.IsPremium);

                                    var selectedRewards = new List<LevelReward>();
                                    if (firstNonPremium != null) selectedRewards.Add(firstNonPremium);
                                    if (firstPremium != null) selectedRewards.Add(firstPremium);

                                    if (firstNonPremium == null && firstPremium == null)
                                        continue;

                                    bool lvlHeigher = lvl.LevelID > data.Level;
                                    string colorPanelTier = lvlHeigher
                                        ? "0.1333333 0.1333333 0.1333333 1"
                                        : "0.3058824 0.3058824 0.3058824 1";
                                    var rewardInStorage =
                                        data.RewardsReceived.FirstOrDefault(x => x.Level == lvl.LevelID);

                                    if (selectedRewards.Count == 1)
                                    {
                                        BattlePassRebornUI.Builder.Element dxdGnY = scrollRoot.AddContainer(
                                            anchorMin: "0 1",
                                            anchorMax: "0 1",
                                            offsetMin: $"{minX} -86",
                                            offsetMax: $"{minX + 100} 86",
                                            name: $"reward_solo_reward{i}");
                                        {
                                            BattlePassRebornUI.Builder.Element XLrInu = dxdGnY.AddContainer(
                                                anchorMin: "0 1",
                                                anchorMax: "0 1",
                                                offsetMin: "0 -140",
                                                offsetMax: "100 0",
                                                name: $"Container_reward{i}");
                                            BattlePassRebornUI.Builder.Element eFJxML = XLrInu.AddPanel(
                                                sprite: "",
                                                material: "",
                                                color: "0 0 0 0.7014",
                                                imageType: UnityEngine.UI.Image.Type.Simple,
                                                anchorMin: "0 0",
                                                anchorMax: "1 1",
                                                offsetMin: "0 0",
                                                offsetMax: "0 0",
                                                name: $"bg_reward{i}");
                                            BattlePassRebornUI.Builder.Element vawAMJ = XLrInu.AddText(
                                                text: selectedRewards[0].IsPremium
                                                    ? (string.IsNullOrEmpty(lvl.generalDisplayNamePremium)
                                                        ? selectedRewards[0].displayName
                                                        : lvl.generalDisplayNamePremium)
                                                    : (string.IsNullOrEmpty(lvl.generalDisplayName)
                                                        ? selectedRewards[0].displayName
                                                        : lvl.generalDisplayName),
                                                color: "0.9254902 0.8901961 0.8588235 1",
                                                font: BattlePassRebornUI.Builder.Font.RobotoCondensedBold,
                                                fontSize: 12,
                                                align: TextAnchor.MiddleCenter,
                                                overflow: VerticalWrapMode.Overflow,
                                                anchorMin: "0 1",
                                                anchorMax: "0 1",
                                                offsetMin: "6 -35",
                                                offsetMax: "94 -6",
                                                name: $"reward_title_reward{i}");
                                            var img = selectedRewards[0].IsPremium
                                                ? (string.IsNullOrEmpty(lvl.ImagePremium)
                                                    ? (string.IsNullOrEmpty(selectedRewards[0].img) ? selectedRewards[0].ShortName : selectedRewards[0].img)
                                                    : lvl.ImagePremium)
                                                : (string.IsNullOrEmpty(lvl.ImageDefault)
                                                    ? (string.IsNullOrEmpty(selectedRewards[0].img) ? selectedRewards[0].ShortName : selectedRewards[0].img)
                                                    : lvl.ImageDefault);
                                            if (img.StartsWith("https") || string.IsNullOrEmpty(img) || img != selectedRewards[0].ShortName)
                                            {
                                                BattlePassRebornUI.Builder.Element ihqtOs = XLrInu.AddImage(
                                                    content: GetImage(img),
                                                    material: "",
                                                    anchorMin: "0 1",
                                                    anchorMax: "0 1",
                                                    offsetMin: "10 -121",
                                                    offsetMax: "90 -41",
                                                    name: $"Image_reward{i}");
                                            }
                                            else
                                            {
                                                BattlePassRebornUI.Builder.Element ihqtOs = XLrInu.AddIcon(
                                                    itemId: _shortNameToItemID[img],
                                                    material: "",
                                                    anchorMin: "0 1",
                                                    anchorMax: "0 1",
                                                    offsetMin: "10 -121",
                                                    offsetMax: "90 -41",
                                                    name: $"Image_reward{i}");
                                            }

                                            BattlePassRebornUI.Builder.Element hRTfcj = XLrInu.AddText(
                                                text: $"x{selectedRewards[0].Amount}",
                                                color: "0.9254902 0.8901961 0.8588235 1",
                                                font: BattlePassRebornUI.Builder.Font.RobotoCondensedBold,
                                                align: TextAnchor.MiddleRight,
                                                overflow: VerticalWrapMode.Overflow,
                                                anchorMin: "0 1",
                                                anchorMax: "0 1",
                                                offsetMin: "44 -134",
                                                offsetMax: "94 -120",
                                                name: $"amound_reward{i}");
                                        }
                                        if (lvlHeigher || (selectedRewards[0].IsPremium == true && !isPremium))
                                        {
                                            BattlePassRebornUI.Builder.Element YiMLYM = dxdGnY.AddPanel(
                                                sprite: "",
                                                material: "",
                                                color: "0 0 0 0.7977",
                                                imageType: UnityEngine.UI.Image.Type.Simple,
                                                anchorMin: "0 0",
                                                anchorMax: "1 1",
                                                offsetMin: "0 0",
                                                offsetMax: "0 0",
                                                name: $"premium_block_reward{i}");
                                            BattlePassRebornUI.Builder.Element eUmWKQ = YiMLYM.AddText(
                                                text: selectedRewards[0].IsPremium
                                                    ? GetLang("TEXT_PREMIUM", player.UserIDString)
                                                    : "",
                                                font: BattlePassRebornUI.Builder.Font.RobotoCondensedBold,
                                                fontSize: 12,
                                                align: TextAnchor.MiddleCenter,
                                                overflow: VerticalWrapMode.Overflow,
                                                anchorMin: "0 1",
                                                anchorMax: "1 1",
                                                offsetMin: "0 -101",
                                                offsetMax: "0 -83",
                                                name: $"Text_reward{i}");
                                            BattlePassRebornUI.Builder.Element iUQHgg = YiMLYM.AddImage(
                                                content: GetImage("Image_iUQHgg"),
                                                material: "",
                                                anchorMin: "0.5 1",
                                                anchorMax: "0.5 1",
                                                offsetMin: "-16 -75",
                                                offsetMax: "16 -43",
                                                name: $"Image_reward1{i}");
                                        }
                                        else if (rewardInStorage != null)
                                        {
                                            BattlePassRebornUI.Builder.Element YiMLYM = dxdGnY.AddPanel(
                                                sprite: "",
                                                material: "",
                                                color: "0 0 0 0.7977",
                                                imageType: UnityEngine.UI.Image.Type.Simple,
                                                anchorMin: "0 0",
                                                anchorMax: "1 1",
                                                offsetMin: "0 0",
                                                offsetMax: "0 0",
                                                name: $"con_reward_solo_reward{i}");
                                            BattlePassRebornUI.Builder.Element eUmWKQ = YiMLYM.AddText(
                                                text: selectedRewards[0].IsPremium
                                                    ? GetLang("TEXT_PREMIUM", player.UserIDString)
                                                    : "",
                                                font: BattlePassRebornUI.Builder.Font.RobotoCondensedBold,
                                                fontSize: 12,
                                                align: TextAnchor.MiddleCenter,
                                                overflow: VerticalWrapMode.Overflow,
                                                anchorMin: "0 1",
                                                anchorMax: "1 1",
                                                offsetMin: "0 -101",
                                                offsetMax: "0 -83",
                                                name: $"Text_reward_solo_reward{i}");
                                            BattlePassRebornUI.Builder.Element iUQHgg = YiMLYM.AddImage(
                                                content: GetImage("status_fScNCW"),
                                                material: "",
                                                anchorMin: "0.5 1",
                                                anchorMax: "0.5 1",
                                                offsetMin: "-16 -75",
                                                offsetMax: "16 -43",
                                                name: $"Image_reward_solo_reward{i}");
                                        }
                                        else
                                        {
                                            var priv = selectedRewards[0].IsPremium ? "PREM" : "NOPREM";
                                            BattlePassRebornUI.Builder.Element ehBQth = dxdGnY.AddButton(
                                                command: $"UI_TAKE {lvl.LevelID} {priv} reward_solo_reward{i}",
                                                color: "0.1333333 0.1333333 0.1333333 0",
                                                imageType: UnityEngine.UI.Image.Type.Simple,
                                                anchorMin: "0 0",
                                                anchorMax: "1 1",
                                                offsetMin: "0 0",
                                                offsetMax: "0 0",
                                                name: $"Btn take_reward{i}");
                                        }

                                        {
                                            BattlePassRebornUI.Builder.Element mVlOmT = dxdGnY.AddPanel(
                                                sprite: "",
                                                material: "",
                                                color: colorPanelTier,
                                                imageType: UnityEngine.UI.Image.Type.Simple,
                                                anchorMin: "0 0",
                                                anchorMax: "1 0",
                                                offsetMin: "0 0",
                                                offsetMax: "0 26",
                                                name: $"tier_info_reward{i}");
                                            BattlePassRebornUI.Builder.Element wOFwgJ = mVlOmT.AddText(
                                                text: GetLang("TEXT_TIER", player.UserIDString, lvl.LevelID),
                                                color: "0.9254902 0.8901961 0.8588235 1",
                                                font: BattlePassRebornUI.Builder.Font.RobotoCondensedBold,
                                                fontSize: 12,
                                                align: TextAnchor.MiddleCenter,
                                                overflow: VerticalWrapMode.Overflow,
                                                anchorMin: "0 0",
                                                anchorMax: "1 1",
                                                offsetMin: "0 0",
                                                offsetMax: "0 0",
                                                name: $"Text_reward1{i}");
                                        }
                                        minX += 112;
                                    }

                                    if (selectedRewards.Count == 2)
                                    {
                                        BattlePassRebornUI.Builder.Element IRWPmn = scrollRoot.AddContainer(
                                            anchorMin: "0 1",
                                            anchorMax: "0 1",
                                            offsetMin: $"{minX} -86",
                                            offsetMax: $"{minX + 212} 86",
                                            name: $"reward_duo{i}");
                                        {
                                            BattlePassRebornUI.Builder.Element tYOkDv = IRWPmn.AddContainer(
                                                anchorMin: "0 1",
                                                anchorMax: "0 1",
                                                offsetMin: "0 -140",
                                                offsetMax: "100 0",
                                                name: $"reward_reward{i}");
                                            BattlePassRebornUI.Builder.Element awjeKe = tYOkDv.AddPanel(
                                                sprite: "",
                                                material: "",
                                                color: "0 0 0 0.7014",
                                                imageType: UnityEngine.UI.Image.Type.Simple,
                                                anchorMin: "0 0",
                                                anchorMax: "1 1",
                                                offsetMin: "0 0",
                                                offsetMax: "0 0",
                                                name: $"bg_reward{i}");
                                            BattlePassRebornUI.Builder.Element CmPwut = tYOkDv.AddText(
                                                text: string.IsNullOrEmpty(lvl.generalDisplayName)
                                                    ? selectedRewards[0].displayName
                                                    : lvl.generalDisplayName,
                                                color: "0.9254902 0.8901961 0.8588235 1",
                                                font: BattlePassRebornUI.Builder.Font.RobotoCondensedBold,
                                                fontSize: 12,
                                                align: TextAnchor.MiddleCenter,
                                                overflow: VerticalWrapMode.Overflow,
                                                anchorMin: "0 1",
                                                anchorMax: "0 1",
                                                offsetMin: "6 -35",
                                                offsetMax: "94 -6",
                                                name: $"reward_title_reward{i}");

                                            var img = string.IsNullOrEmpty(lvl.ImageDefault)
                                                ? (string.IsNullOrEmpty(selectedRewards[0].img) ? selectedRewards[0].ShortName : selectedRewards[0].img)
                                                : lvl.ImageDefault;
                                            if (img.StartsWith("https") || string.IsNullOrEmpty(img) || img != selectedRewards[0].ShortName)
                                            {
                                                BattlePassRebornUI.Builder.Element ihqtOs = tYOkDv.AddImage(
                                                    content: GetImage(img),
                                                    material: "",
                                                    anchorMin: "0 1",
                                                    anchorMax: "0 1",
                                                    offsetMin: "10 -121",
                                                    offsetMax: "90 -41",
                                                    name: $"Image_reward{i}");
                                            }
                                            else
                                            {
                                                BattlePassRebornUI.Builder.Element ihqtOs = tYOkDv.AddIcon(
                                                    itemId: _shortNameToItemID[img],
                                                    material: "",
                                                    anchorMin: "0 1",
                                                    anchorMax: "0 1",
                                                    offsetMin: "10 -121",
                                                    offsetMax: "90 -41",
                                                    name: $"Image_reward{i}");
                                            }

                                            BattlePassRebornUI.Builder.Element RObhOQ = tYOkDv.AddText(
                                                text: $"x{selectedRewards[0].Amount}",
                                                color: "0.9254902 0.8901961 0.8588235 1",
                                                font: BattlePassRebornUI.Builder.Font.RobotoCondensedBold,
                                                align: TextAnchor.MiddleRight,
                                                overflow: VerticalWrapMode.Overflow,
                                                anchorMin: "0 1",
                                                anchorMax: "0 1",
                                                offsetMin: "44 -134",
                                                offsetMax: "94 -120",
                                                name: $"amound_reward{i}");
                                            if (lvlHeigher || (selectedRewards[0].IsPremium == true && !isPremium))
                                            {
                                                BattlePassRebornUI.Builder.Element YiMLYM = tYOkDv.AddPanel(
                                                    sprite: "",
                                                    material: "",
                                                    color: "0 0 0 0.7977",
                                                    imageType: UnityEngine.UI.Image.Type.Simple,
                                                    anchorMin: "0 0",
                                                    anchorMax: "1 1",
                                                    offsetMin: "0 0",
                                                    offsetMax: "0 0",
                                                    name: $"premium_block_reward{i}");
                                                BattlePassRebornUI.Builder.Element eUmWKQ = YiMLYM.AddText(
                                                    text: selectedRewards[0].IsPremium
                                                        ? GetLang("TEXT_PREMIUM", player.UserIDString)
                                                        : "",
                                                    font: BattlePassRebornUI.Builder.Font.RobotoCondensedBold,
                                                    fontSize: 12,
                                                    align: TextAnchor.MiddleCenter,
                                                    overflow: VerticalWrapMode.Overflow,
                                                    anchorMin: "0 1",
                                                    anchorMax: "1 1",
                                                    offsetMin: "0 -101",
                                                    offsetMax: "0 -83",
                                                    name: $"Text_reward{i}");
                                                BattlePassRebornUI.Builder.Element iUQHgg = YiMLYM.AddImage(
                                                    content: GetImage("Image_iUQHgg"),
                                                    material: "",
                                                    anchorMin: "0.5 1",
                                                    anchorMax: "0.5 1",
                                                    offsetMin: "-16 -75",
                                                    offsetMax: "16 -43",
                                                    name: $"Image_reward1{i}");
                                            }
                                            else if (rewardInStorage != null && rewardInStorage.DefaultReward == true)
                                            {
                                                BattlePassRebornUI.Builder.Element YiMLYM = tYOkDv.AddPanel(
                                                    sprite: "",
                                                    material: "",
                                                    color: "0 0 0 0.7977",
                                                    imageType: UnityEngine.UI.Image.Type.Simple,
                                                    anchorMin: "0 0",
                                                    anchorMax: "1 1",
                                                    offsetMin: "0 0",
                                                    offsetMax: "0 0",
                                                    name: $"con_reward_reward{i}");
                                                BattlePassRebornUI.Builder.Element eUmWKQ = YiMLYM.AddText(
                                                    text: selectedRewards[0].IsPremium
                                                        ? GetLang("TEXT_PREMIUM", player.UserIDString)
                                                        : "",
                                                    font: BattlePassRebornUI.Builder.Font.RobotoCondensedBold,
                                                    fontSize: 12,
                                                    align: TextAnchor.MiddleCenter,
                                                    overflow: VerticalWrapMode.Overflow,
                                                    anchorMin: "0 1",
                                                    anchorMax: "1 1",
                                                    offsetMin: "0 -101",
                                                    offsetMax: "0 -83",
                                                    name: $"Text_reward_reward{i}");
                                                BattlePassRebornUI.Builder.Element iUQHgg = YiMLYM.AddImage(
                                                    content: GetImage("status_fScNCW"),
                                                    material: "",
                                                    anchorMin: "0.5 1",
                                                    anchorMax: "0.5 1",
                                                    offsetMin: "-16 -75",
                                                    offsetMax: "16 -43",
                                                    name: $"Image_reward_reward{i}");
                                            }
                                            else
                                            {
                                                var priv = selectedRewards[0].IsPremium ? "PREM" : "NOPREM";
                                                BattlePassRebornUI.Builder.Element ehBQth = tYOkDv.AddButton(
                                                    command: $"UI_TAKE {lvl.LevelID} {priv} reward_reward{i}",
                                                    color: "0.1333333 0.1333333 0.1333333 0",
                                                    imageType: UnityEngine.UI.Image.Type.Simple,
                                                    anchorMin: "0 0",
                                                    anchorMax: "1 1",
                                                    offsetMin: "0 0",
                                                    offsetMax: "0 0",
                                                    name: $"Btn take_reward{i}");
                                            }
                                        }
                                        {
                                            BattlePassRebornUI.Builder.Element jGhbIs = IRWPmn.AddContainer(
                                                anchorMin: "0 1",
                                                anchorMax: "0 1",
                                                offsetMin: "112 -140",
                                                offsetMax: "212 0",
                                                name: $"reward_need_premium_reward{i}");
                                            BattlePassRebornUI.Builder.Element EHCQvG = jGhbIs.AddPanel(
                                                sprite: "",
                                                material: "",
                                                color: "0 0 0 0.7014",
                                                imageType: UnityEngine.UI.Image.Type.Simple,
                                                anchorMin: "0 0",
                                                anchorMax: "1 1",
                                                offsetMin: "0 0",
                                                offsetMax: "0 0",
                                                name: $"bg_reward2{i}");
                                            BattlePassRebornUI.Builder.Element fDqMVp = jGhbIs.AddText(
                                                text: string.IsNullOrEmpty(lvl.generalDisplayNamePremium)
                                                    ? selectedRewards[1].displayName
                                                    : lvl.generalDisplayNamePremium,
                                                color: "0.9254902 0.8901961 0.8588235 1",
                                                font: BattlePassRebornUI.Builder.Font.RobotoCondensedBold,
                                                fontSize: 12,
                                                align: TextAnchor.MiddleCenter,
                                                overflow: VerticalWrapMode.Overflow,
                                                anchorMin: "0 1",
                                                anchorMax: "0 1",
                                                offsetMin: "6 -35",
                                                offsetMax: "94 -6",
                                                name: $"reward_title_reward2{i}");

                                            var img = string.IsNullOrEmpty(lvl.ImagePremium)
                                                ? (string.IsNullOrEmpty(selectedRewards[1].img) ? selectedRewards[1].ShortName : selectedRewards[1].img)
                                                : lvl.ImagePremium;
                                            if (img.StartsWith("https") || string.IsNullOrEmpty(img) || img != selectedRewards[1].ShortName)
                                            {
                                                BattlePassRebornUI.Builder.Element ihqtOs = jGhbIs.AddImage(
                                                    content: GetImage(img),
                                                    material: "",
                                                    anchorMin: "0 1",
                                                    anchorMax: "0 1",
                                                    offsetMin: "10 -121",
                                                    offsetMax: "90 -41",
                                                    name: $"Image_reward2{i}");
                                            }
                                            else
                                            {
                                                BattlePassRebornUI.Builder.Element ihqtOs = jGhbIs.AddIcon(
                                                    itemId: _shortNameToItemID[img],
                                                    material: "",
                                                    anchorMin: "0 1",
                                                    anchorMax: "0 1",
                                                    offsetMin: "10 -121",
                                                    offsetMax: "90 -41",
                                                    name: $"Image_reward2{i}");
                                            }

                                            BattlePassRebornUI.Builder.Element OKyGAR = jGhbIs.AddText(
                                                text: $"x{selectedRewards[1].Amount}",
                                                color: "0.9254902 0.8901961 0.8588235 1",
                                                font: BattlePassRebornUI.Builder.Font.RobotoCondensedBold,
                                                align: TextAnchor.MiddleRight,
                                                overflow: VerticalWrapMode.Overflow,
                                                anchorMin: "0 1",
                                                anchorMax: "0 1",
                                                offsetMin: "44 -134",
                                                offsetMax: "94 -120",
                                                name: $"amound_reward2{i}");
                                            if (lvlHeigher || (selectedRewards[1].IsPremium == true && !isPremium))
                                            {
                                                BattlePassRebornUI.Builder.Element YiMLYM = jGhbIs.AddPanel(
                                                    sprite: "",
                                                    material: "",
                                                    color: "0 0 0 0.7977",
                                                    imageType: UnityEngine.UI.Image.Type.Simple,
                                                    anchorMin: "0 0",
                                                    anchorMax: "1 1",
                                                    offsetMin: "0 0",
                                                    offsetMax: "0 0",
                                                    name: $"premium_block_reward2{i}");
                                                BattlePassRebornUI.Builder.Element eUmWKQ = YiMLYM.AddText(
                                                    text: selectedRewards[1].IsPremium
                                                        ? GetLang("TEXT_PREMIUM", player.UserIDString)
                                                        : "",
                                                    font: BattlePassRebornUI.Builder.Font.RobotoCondensedBold,
                                                    fontSize: 12,
                                                    align: TextAnchor.MiddleCenter,
                                                    overflow: VerticalWrapMode.Overflow,
                                                    anchorMin: "0 1",
                                                    anchorMax: "1 1",
                                                    offsetMin: "0 -101",
                                                    offsetMax: "0 -83",
                                                    name: $"Text_reward2{i}");
                                                BattlePassRebornUI.Builder.Element iUQHgg = YiMLYM.AddImage(
                                                    content: GetImage("Image_iUQHgg"),
                                                    material: "",
                                                    anchorMin: "0.5 1",
                                                    anchorMax: "0.5 1",
                                                    offsetMin: "-16 -75",
                                                    offsetMax: "16 -43",
                                                    name: $"Image_reward3{i}");
                                            }
                                            else if (rewardInStorage != null && rewardInStorage.PremiumReward == true)
                                            {
                                                BattlePassRebornUI.Builder.Element YiMLYM = jGhbIs.AddPanel(
                                                    sprite: "",
                                                    material: "",
                                                    color: "0 0 0 0.7977",
                                                    imageType: UnityEngine.UI.Image.Type.Simple,
                                                    anchorMin: "0 0",
                                                    anchorMax: "1 1",
                                                    offsetMin: "0 0",
                                                    offsetMax: "0 0",
                                                    name: $"con_reward_need_premium_reward{i}");
                                                BattlePassRebornUI.Builder.Element eUmWKQ = YiMLYM.AddText(
                                                    text: selectedRewards[1].IsPremium
                                                        ? GetLang("TEXT_PREMIUM", player.UserIDString)
                                                        : "",
                                                    font: BattlePassRebornUI.Builder.Font.RobotoCondensedBold,
                                                    fontSize: 12,
                                                    align: TextAnchor.MiddleCenter,
                                                    overflow: VerticalWrapMode.Overflow,
                                                    anchorMin: "0 1",
                                                    anchorMax: "1 1",
                                                    offsetMin: "0 -101",
                                                    offsetMax: "0 -83",
                                                    name: $"Text_reward_need_premium_reward{i}");
                                                BattlePassRebornUI.Builder.Element iUQHgg = YiMLYM.AddImage(
                                                    content: GetImage("status_fScNCW"),
                                                    material: "",
                                                    anchorMin: "0.5 1",
                                                    anchorMax: "0.5 1",
                                                    offsetMin: "-16 -75",
                                                    offsetMax: "16 -43",
                                                    name: $"Image_reward_need_premium_reward{i}");
                                            }
                                            else
                                            {
                                                var priv = selectedRewards[1].IsPremium ? "PREM" : "NOPREM";
                                                BattlePassRebornUI.Builder.Element ehBQth = jGhbIs.AddButton(
                                                    command:
                                                    $"UI_TAKE {lvl.LevelID} {priv} reward_need_premium_reward{i}",
                                                    color: "0.1333333 0.1333333 0.1333333 0",
                                                    imageType: UnityEngine.UI.Image.Type.Simple,
                                                    anchorMin: "0 0",
                                                    anchorMax: "1 1",
                                                    offsetMin: "0 0",
                                                    offsetMax: "0 0",
                                                    name: $"Btn take_reward2{i}");
                                            }
                                        }
                                        {
                                            BattlePassRebornUI.Builder.Element CbmTIb = IRWPmn.AddPanel(
                                                sprite: "",
                                                material: "",
                                                color: colorPanelTier,
                                                imageType: UnityEngine.UI.Image.Type.Simple,
                                                anchorMin: "0 0",
                                                anchorMax: "1 0",
                                                offsetMin: "0 0",
                                                offsetMax: "0 26",
                                                name: $"tier_info_reward2{i}");
                                            BattlePassRebornUI.Builder.Element pouZlZ = CbmTIb.AddText(
                                                text: GetLang("TEXT_TIER", player.UserIDString, lvl.LevelID),
                                                color: "0.9254902 0.8901961 0.8588235 1",
                                                font: BattlePassRebornUI.Builder.Font.RobotoCondensedBold,
                                                fontSize: 12,
                                                align: TextAnchor.MiddleCenter,
                                                overflow: VerticalWrapMode.Overflow,
                                                anchorMin: "0 0",
                                                anchorMax: "1 1",
                                                offsetMin: "0 0",
                                                offsetMax: "0 0",
                                                name: $"Text_reward3{i}");
                                        }

                                        minX += 224;
                                    }

                                    i++;
                                }
                            }
                        }
                    }
                }
                {
                    BattlePassRebornUI.Builder.Element YByHIP = suCepD.AddContainer(
                        anchorMin: "0 0",
                        anchorMax: "1 0",
                        offsetMin: "0 0",
                        offsetMax: "0 40",
                        name: "Footer");
                    BattlePassRebornUI.Builder.Element BqHenx = YByHIP.AddPanel(
                        sprite: "",
                        material: "",
                        color: "0.05882353 0.05882353 0.05882353 1",
                        imageType: UnityEngine.UI.Image.Type.Simple,
                        anchorMin: "0 0",
                        anchorMax: "1 1",
                        offsetMin: "0 0",
                        offsetMax: "0 0",
                        name: "bg");
                    BattlePassRebornUI.Builder.Element ieeMqn = YByHIP.AddImage(
                        content: GetImage("Image_ieeMqn"),
                        material: "",
                        anchorMin: "0 1",
                        anchorMax: "0 1",
                        offsetMin: "43 -27",
                        offsetMax: "57 -13",
                        name: "Image");
                    BattlePassRebornUI.Builder.Element grvsUO = YByHIP.AddText(
                        text: GetLang(_UISettings.Hints[_rand.Next(_UISettings.Hints.Count)], player.UserIDString),
                        color: "0.9254902 0.8901961 0.8588235 1",
                        font: BattlePassRebornUI.Builder.Font.RobotoCondensedBold,
                        fontSize: 12,
                        align: TextAnchor.MiddleLeft,
                        overflow: VerticalWrapMode.Overflow,
                        anchorMin: "0 0",
                        anchorMax: "0 1",
                        offsetMin: "66 0",
                        offsetMax: "866 0",
                        name: "Text");

                    BattlePassRebornUI.Builder.Element kHSdpd = YByHIP.AddImage(
                        content: player.UserIDString,
                        material: "",
                        anchorMin: "1 1",
                        anchorMax: "1 1",
                        offsetMin: "-68 -33",
                        offsetMax: "-42 -7",
                        name: "player_avatar");
                    {
                        BattlePassRebornUI.Builder.Element GOMQfQ = YByHIP.AddPanel(
                            sprite: "",
                            material: "",
                            color: "0.1333333 0.1333333 0.1333333 1",
                            imageType: UnityEngine.UI.Image.Type.Simple,
                            anchorMin: "1 1",
                            anchorMax: "1 1",
                            offsetMin: "-174 -33",
                            offsetMax: "-79 -7",
                            name: "player_premium_status_off");
                        BattlePassRebornUI.Builder.Element TpHnoo = GOMQfQ.AddText(
                            text: GetLang("TEXT_PREMIUM", player.UserIDString),
                            color: "1 1 1 0.7354",
                            font: BattlePassRebornUI.Builder.Font.RobotoCondensedBold,
                            fontSize: 12,
                            align: TextAnchor.MiddleCenter,
                            overflow: VerticalWrapMode.Overflow,
                            anchorMin: "0 0",
                            anchorMax: "1 1",
                            offsetMin: "0 0",
                            offsetMax: "0 0",
                            name: "Text");
                    }
                    {
                        BattlePassRebornUI.Builder.Element EYcaGT = YByHIP.AddPanel(
                            sprite: "",
                            material: "",
                            color: "0.1333333 0.1333333 0.1333333 1",
                            imageType: UnityEngine.UI.Image.Type.Simple,
                            anchorMin: "1 1",
                            anchorMax: "1 1",
                            offsetMin: "-174 -33",
                            offsetMax: "-79 -7",
                            name: "player_premium_status_on");
                        if (isPremium)
                        {
                            BattlePassRebornUI.Builder.Element BwnjeJ = EYcaGT.AddImage(
                                content: GetImage("Image_BwnjeJ"),
                                material: "",
                                anchorMin: "0 0",
                                anchorMax: "1 1",
                                offsetMin: "0 0",
                                offsetMax: "0 0",
                                name: "Image");
                        }

                        BattlePassRebornUI.Builder.Element RJmGVJ = EYcaGT.AddText(
                            text: GetLang("TEXT_PREMIUM", player.UserIDString),
                            color: isPremium ? "0.1333333 0.1333333 0.1333333 1" : "0.73 0.69 0.66 1",
                            font: BattlePassRebornUI.Builder.Font.RobotoCondensedBold,
                            fontSize: 12,
                            align: TextAnchor.MiddleCenter,
                            overflow: VerticalWrapMode.Overflow,
                            anchorMin: "0 0",
                            anchorMax: "1 1",
                            offsetMin: "0 0",
                            offsetMax: "0 0",
                            name: "Text");
                    }
                }
            }
            root.Render(player);
        }

        private void ShowPremiumPassBanerUI(BasePlayer player)
        {
            BattlePassRebornUI.Builder.Root root = new BattlePassRebornUI.Builder.Root("OverlayNonScaled");
            {
                BattlePassRebornUI.Builder.Element bQnlBI = root.AddContainer(
                    anchorMin: "0 0",
                    anchorMax: "1 1",
                    offsetMin: "0 0",
                    offsetMax: "0 0",
                    name: "premium_pass_baner");
                BattlePassRebornUI.Builder.Element sKqnDQ = bQnlBI.AddPanel(
                    sprite: "",
                    material: "",
                    color: "0.1333333 0.1333333 0.1333333 0.9063",
                    imageType: UnityEngine.UI.Image.Type.Simple,
                    anchorMin: "0 0",
                    anchorMax: "1 1",
                    offsetMin: "0 0",
                    offsetMax: "0 0",
                    name: "bg");
                {
                    BattlePassRebornUI.Builder.Element mqroIe = bQnlBI.AddContainer(
                        anchorMin: "0 1",
                        anchorMax: "0 1",
                        offsetMin: "283 -599.5",
                        offsetMax: "997 -120.5",
                        name: "premium_pass_info");
                    {
                        BattlePassRebornUI.Builder.Element myZhOk = mqroIe.AddPanel(
                            sprite: "",
                            material: "",
                            color: "0.09411765 0.09411765 0.09803922 1",
                            imageType: UnityEngine.UI.Image.Type.Simple,
                            anchorMin: "0 1",
                            anchorMax: "1 1",
                            offsetMin: "0 -47",
                            offsetMax: "0 0",
                            name: "header");
                        BattlePassRebornUI.Builder.Element wjxnyw = myZhOk.AddText(
                            text: GetLang("TEXT_PP", player.UserIDString),
                            font: BattlePassRebornUI.Builder.Font.RobotoCondensedBold,
                            fontSize: 20,
                            align: TextAnchor.MiddleLeft,
                            overflow: VerticalWrapMode.Overflow,
                            anchorMin: "0 1",
                            anchorMax: "0 1",
                            offsetMin: "20 -47",
                            offsetMax: "320 0",
                            name: "title");
                        {
                            BattlePassRebornUI.Builder.Element qwxDZA = myZhOk.AddButton(
                                command: null,
                                color: "0.1333333 0.1333333 0.1333333 1",
                                imageType: UnityEngine.UI.Image.Type.Simple,
                                anchorMin: "1 1",
                                anchorMax: "1 1",
                                offsetMin: "-90 -37",
                                offsetMax: "-20 -10",
                                close: "premium_pass_baner",
                                name: "Btn close");
                            BattlePassRebornUI.Builder.Element RHacyC = qwxDZA.AddText(
                                text: GetLang("TEXT_CLOSE", player.UserIDString),
                                color: "0.7294118 0.6941177 0.6588235 1",
                                font: BattlePassRebornUI.Builder.Font.RobotoCondensedBold,
                                fontSize: 10,
                                align: TextAnchor.MiddleLeft,
                                overflow: VerticalWrapMode.Truncate,
                                anchorMin: "0 1",
                                anchorMax: "0 1",
                                offsetMin: "27 -27",
                                offsetMax: "70 0",
                                name: "Text");
                            BattlePassRebornUI.Builder.Element hRusJD = qwxDZA.AddImage(
                                content: GetImage("Image_OxvJwN"),
                                material: "",
                                anchorMin: "0 1",
                                anchorMax: "0 1",
                                offsetMin: "9 -20",
                                offsetMax: "22 -7",
                                name: "Image");
                            BattlePassRebornUI.Builder.Element JtXtMV = qwxDZA.AddPanel(
                                sprite: "",
                                material: "",
                                color: "0.1137255 0.1137255 0.1137255 1",
                                imageType: UnityEngine.UI.Image.Type.Simple,
                                anchorMin: "0 0",
                                anchorMax: "1 0",
                                offsetMin: "0 0",
                                offsetMax: "0 1",
                                name: "Panel");
                        }
                    }
                    {
                        BattlePassRebornUI.Builder.Element wbbEJb = mqroIe.AddContainer(
                            anchorMin: "0 0",
                            anchorMax: "1 1",
                            offsetMin: "0 0",
                            offsetMax: "0 -47",
                            name: "content");
                        BattlePassRebornUI.Builder.Element dBYwSV = wbbEJb.AddImage(
                            content: GetImage("bg_dBYwSV"),
                            material: "",
                            anchorMin: "0 0",
                            anchorMax: "1 1",
                            offsetMin: "0 0",
                            offsetMax: "0 0",
                            name: "bg");
                        BattlePassRebornUI.Builder.Element hmkscg = wbbEJb.AddText(
                            text: GetLang("TEXT_UPGRADE", player.UserIDString),
                            font: BattlePassRebornUI.Builder.Font.RobotoCondensedBold,
                            fontSize: 16,
                            align: TextAnchor.UpperLeft,
                            overflow: VerticalWrapMode.Overflow,
                            anchorMin: "0 1",
                            anchorMax: "0 1",
                            offsetMin: "20 -47",
                            offsetMax: "370 -20",
                            name: "Text");
                        BattlePassRebornUI.Builder.Element cguPDI = wbbEJb.AddText(
                            text: GetLang("TEXT_PFEATURES", player.UserIDString),
                            font: BattlePassRebornUI.Builder.Font.RobotoCondensedBold,
                            fontSize: 16,
                            align: TextAnchor.UpperLeft,
                            overflow: VerticalWrapMode.Overflow,
                            anchorMin: "0 1",
                            anchorMax: "0 1",
                            offsetMin: "20 -104",
                            offsetMax: "370 -77",
                            name: "Text (2)");
                        BattlePassRebornUI.Builder.Element TCugLq = wbbEJb.AddText(
                            text: GetLang("TEXT_HGETPREMIUM", player.UserIDString),
                            font: BattlePassRebornUI.Builder.Font.RobotoCondensedBold,
                            fontSize: 16,
                            align: TextAnchor.UpperLeft,
                            overflow: VerticalWrapMode.Overflow,
                            anchorMin: "0 1",
                            anchorMax: "0 1",
                            offsetMin: "20 -203",
                            offsetMax: "370 -176",
                            name: "Text (4)");
                        BattlePassRebornUI.Builder.Element iudUet = wbbEJb.AddText(
                            text: GetLang("TEXT_GQUEST", player.UserIDString),
                            font: BattlePassRebornUI.Builder.Font.RobotoCondensedBold,
                            fontSize: 16,
                            align: TextAnchor.UpperLeft,
                            overflow: VerticalWrapMode.Overflow,
                            anchorMin: "0 1",
                            anchorMax: "0 1",
                            offsetMin: "20 -341",
                            offsetMax: "370 -314",
                            name: "Text (7)");
                        BattlePassRebornUI.Builder.Element iYioXc = wbbEJb.AddText(
                            text: GetLang("TEXT_UNLOCKBPEXPERIENCE", player.UserIDString),
                            color: "0.9254902 0.8901961 0.8588235 1",
                            font: BattlePassRebornUI.Builder.Font.RobotoCondensedBold,
                            fontSize: 12,
                            align: TextAnchor.UpperLeft,
                            overflow: VerticalWrapMode.Overflow,
                            anchorMin: "0 1",
                            anchorMax: "0 1",
                            offsetMin: "20 -77",
                            offsetMax: "370 -47",
                            name: "Text (1)");
                        BattlePassRebornUI.Builder.Element mJSPcS = wbbEJb.AddText(
                            text: GetLang("TEXT_COPYURL", player.UserIDString),
                            color: "0.9254902 0.8901961 0.8588235 0.7354",
                            font: BattlePassRebornUI.Builder.Font.RobotoCondensedBold,
                            fontSize: 12,
                            align: TextAnchor.UpperLeft,
                            overflow: VerticalWrapMode.Overflow,
                            anchorMin: "0 1",
                            anchorMax: "0 1",
                            offsetMin: "20 -270",
                            offsetMax: "370 -250",
                            name: "Text (9)");
                        BattlePassRebornUI.Builder.Element QWVWjf = wbbEJb.AddText(
                            text: GetLang("TEXT_COPYURL", player.UserIDString),
                            color: "0.9254902 0.8901961 0.8588235 0.7354",
                            font: BattlePassRebornUI.Builder.Font.RobotoCondensedBold,
                            fontSize: 12,
                            align: TextAnchor.UpperLeft,
                            overflow: VerticalWrapMode.Overflow,
                            anchorMin: "0 1",
                            anchorMax: "0 1",
                            offsetMin: "20 -408",
                            offsetMax: "370 -388",
                            name: "Text (10)");
                        BattlePassRebornUI.Builder.Element OfBIhJ = wbbEJb.AddText(
                            text: GetLang("TEXT_JOINDS", player.UserIDString),
                            color: "0.9254902 0.8901961 0.8588235 1",
                            font: BattlePassRebornUI.Builder.Font.RobotoCondensedBold,
                            fontSize: 12,
                            align: TextAnchor.UpperLeft,
                            overflow: VerticalWrapMode.Overflow,
                            anchorMin: "0 1",
                            anchorMax: "0 1",
                            offsetMin: "20 -363",
                            offsetMax: "370 -341",
                            name: "Text (8)");
                        BattlePassRebornUI.Builder.Element OLnJRW = wbbEJb.AddText(
                            text: GetLang("TEXT_PURCHASEPP", player.UserIDString),
                            color: "0.9254902 0.8901961 0.8588235 1",
                            font: BattlePassRebornUI.Builder.Font.RobotoCondensedBold,
                            fontSize: 12,
                            align: TextAnchor.UpperLeft,
                            overflow: VerticalWrapMode.Overflow,
                            anchorMin: "0 1",
                            anchorMax: "0 1",
                            offsetMin: "20 -314",
                            offsetMax: "370 -270",
                            name: "Text (6)");
                        BattlePassRebornUI.Builder.Element VVnrhs = wbbEJb.AddText(
                            text: GetLang("TEXT_VISITWEBSITE", player.UserIDString),
                            color: "0.9254902 0.8901961 0.8588235 1",
                            font: BattlePassRebornUI.Builder.Font.RobotoCondensedBold,
                            fontSize: 12,
                            align: TextAnchor.UpperLeft,
                            overflow: VerticalWrapMode.Overflow,
                            anchorMin: "0 1",
                            anchorMax: "0 1",
                            offsetMin: "20 -225",
                            offsetMax: "370 -203",
                            name: "Text (5)");
                        BattlePassRebornUI.Builder.Element sogCIF = wbbEJb.AddText(
                            text: GetLang("TEXT_PRIVELEGES", player.UserIDString),
                            color: "0.9254902 0.8901961 0.8588235 1",
                            font: BattlePassRebornUI.Builder.Font.RobotoCondensedBold,
                            fontSize: 12,
                            align: TextAnchor.UpperLeft,
                            overflow: VerticalWrapMode.Overflow,
                            anchorMin: "0 1",
                            anchorMax: "0 1",
                            offsetMin: "20 -176",
                            offsetMax: "370 -104",
                            name: "Text (3)");
                        {
                            BattlePassRebornUI.Builder.Element ZdEGmc = wbbEJb.AddContainer(
                                anchorMin: "0 1",
                                anchorMax: "0 1",
                                offsetMin: "20 -250",
                                offsetMax: "217 -225",
                                name: "link1");
                            {
                                BattlePassRebornUI.Builder.Element ItVBuJ = ZdEGmc.AddPanel(
                                    sprite: "",
                                    material: "",
                                    color: "0.1333333 0.1333333 0.1333333 1",
                                    imageType: UnityEngine.UI.Image.Type.Simple,
                                    anchorMin: "0 0",
                                    anchorMax: "1 1",
                                    offsetMin: "0 0",
                                    offsetMax: "0 0",
                                    name: "Panel");
                                {
                                    BattlePassRebornUI.Builder.Element WoNqbb = ItVBuJ.AddInputfield(
                                        command: null,
                                        text: _UISettings.URL1,
                                        font: BattlePassRebornUI.Builder.Font.RobotoCondensedRegular,
                                        align: TextAnchor.MiddleCenter,
                                        lineType: InputField.LineType.SingleLine,
                                        charsLimit: 0,
                                        anchorMin: "0 0",
                                        anchorMax: "0 1",
                                        offsetMin: "5 0",
                                        offsetMax: "192 0",
                                        name: "Inputfield",
                                        @readonly: true);
                                }
                            }
                        }
                        {
                            BattlePassRebornUI.Builder.Element jGmJAd = wbbEJb.AddContainer(
                                anchorMin: "0 1",
                                anchorMax: "0 1",
                                offsetMin: "20 -388",
                                offsetMax: "217 -363",
                                name: "link2");
                            {
                                BattlePassRebornUI.Builder.Element wNgHoD = jGmJAd.AddPanel(
                                    sprite: "",
                                    material: "",
                                    color: "0.1333333 0.1333333 0.1333333 1",
                                    imageType: UnityEngine.UI.Image.Type.Simple,
                                    anchorMin: "0 0",
                                    anchorMax: "1 1",
                                    offsetMin: "0 0",
                                    offsetMax: "0 0",
                                    name: "Panel");
                                {
                                    BattlePassRebornUI.Builder.Element VcczQe = wNgHoD.AddInputfield(
                                        command: null,
                                        text: _UISettings.URL2,
                                        font: BattlePassRebornUI.Builder.Font.RobotoCondensedRegular,
                                        align: TextAnchor.MiddleCenter,
                                        lineType: InputField.LineType.SingleLine,
                                        charsLimit: 0,
                                        anchorMin: "0 0",
                                        anchorMax: "0 1",
                                        offsetMin: "5 0",
                                        offsetMax: "192 0",
                                        name: "Inputfield",
                                        @readonly: true);
                                }
                            }
                        }
                    }
                }
            }
            root.Render(player);
        }

        private void ShowStorageUI(BasePlayer player)
        {
            BattlePassRebornUI.Builder.Root root = new BattlePassRebornUI.Builder.Root("OverlayNonScaled");
            {
                var data = LoadStoragePlayerData(player.userID);
                BattlePassRebornUI.Builder.Element bQnlBI = root.AddContainer(
                    anchorMin: "0 0",
                    anchorMax: "1 1",
                    offsetMin: "0 0",
                    offsetMax: "0 0",
                    name: "storage");
                BattlePassRebornUI.Builder.Element sKqnDQ = bQnlBI.AddPanel(
                    sprite: "",
                    material: "",
                    color: "0.1333333 0.1333333 0.1333333 0.9063",
                    imageType: UnityEngine.UI.Image.Type.Simple,
                    anchorMin: "0 0",
                    anchorMax: "1 1",
                    offsetMin: "0 0",
                    offsetMax: "0 0",
                    name: "bg");
                {
                    BattlePassRebornUI.Builder.Element mqroIe = bQnlBI.AddContainer(
                        anchorMin: "0.5 1",
                        anchorMax: "0.5 1",
                        offsetMin: "-310 -580.5",
                        offsetMax: "310 -120.5",
                        name: "storage_info");
                    {
                        BattlePassRebornUI.Builder.Element myZhOk = mqroIe.AddPanel(
                            sprite: "",
                            material: "",
                            color: "0.09411765 0.09411765 0.09803922 1",
                            imageType: UnityEngine.UI.Image.Type.Simple,
                            anchorMin: "0 1",
                            anchorMax: "1 1",
                            offsetMin: "0 -47",
                            offsetMax: "0 0",
                            name: "header");
                        BattlePassRebornUI.Builder.Element wjxnyw = myZhOk.AddText(
                            text: GetLang("TEXT_STORAGE", player.UserIDString),
                            font: BattlePassRebornUI.Builder.Font.RobotoCondensedBold,
                            fontSize: 20,
                            align: TextAnchor.MiddleLeft,
                            overflow: VerticalWrapMode.Overflow,
                            anchorMin: "0 1",
                            anchorMax: "0 1",
                            offsetMin: "20 -47",
                            offsetMax: "320 0",
                            name: "title");
                        {
                            BattlePassRebornUI.Builder.Element qwxDZA = myZhOk.AddButton(
                                command: null,
                                color: "0.1333333 0.1333333 0.1333333 1",
                                imageType: UnityEngine.UI.Image.Type.Simple,
                                anchorMin: "1 1",
                                anchorMax: "1 1",
                                offsetMin: "-90 -37",
                                offsetMax: "-20 -10",
                                close: "storage",
                                name: "Btn close");
                            BattlePassRebornUI.Builder.Element RHacyC = qwxDZA.AddText(
                                text: GetLang("TEXT_CLOSE", player.UserIDString),
                                color: "0.7294118 0.6941177 0.6588235 1",
                                font: BattlePassRebornUI.Builder.Font.RobotoCondensedBold,
                                fontSize: 10,
                                align: TextAnchor.MiddleLeft,
                                overflow: VerticalWrapMode.Truncate,
                                anchorMin: "0 1",
                                anchorMax: "0 1",
                                offsetMin: "27 -27",
                                offsetMax: "70 0",
                                name: "Text");
                            BattlePassRebornUI.Builder.Element hRusJD = qwxDZA.AddImage(
                                content: GetImage("Image_OxvJwN"),
                                material: "",
                                anchorMin: "0 1",
                                anchorMax: "0 1",
                                offsetMin: "9 -20",
                                offsetMax: "22 -7",
                                name: "Image");
                            BattlePassRebornUI.Builder.Element JtXtMV = qwxDZA.AddPanel(
                                sprite: "",
                                material: "",
                                color: "0.1137255 0.1137255 0.1137255 1",
                                imageType: UnityEngine.UI.Image.Type.Simple,
                                anchorMin: "0 0",
                                anchorMax: "1 0",
                                offsetMin: "0 0",
                                offsetMax: "0 1",
                                name: "Panel");
                        }
                    }
                    {
                        BattlePassRebornUI.Builder.Element wbbEJb = mqroIe.AddContainer(
                            anchorMin: "0 0",
                            anchorMax: "1 1",
                            offsetMin: "0 0",
                            offsetMax: "0 -47",
                            name: "content");
                        BattlePassRebornUI.Builder.Element dBYwSV = wbbEJb.AddPanel(
                            sprite: "",
                            material: "",
                            color: "0.13 0.13 0.13 1",
                            imageType: UnityEngine.UI.Image.Type.Simple,
                            anchorMin: "0 0",
                            anchorMax: "1 1",
                            offsetMin: "0 0",
                            offsetMax: "0 0",
                            name: "bg");
                        BattlePassRebornUI.Builder.Element WBYwEW = dBYwSV.AddPanel(
                            sprite: "",
                            material: "",
                            color: "1 1 1 0",
                            imageType: UnityEngine.UI.Image.Type.Simple,
                            anchorMin: "0 1",
                            anchorMax: "1 1",
                            offsetMin: "0 -52",
                            offsetMax: "0 0",
                            name: "storagedescr");

                        BattlePassRebornUI.Builder.Element WBYwEt = WBYwEW.AddText(
                            text: GetLang("TEXT_STORAGEDESCR", player.UserIDString),
                            font: BattlePassRebornUI.Builder.Font.RobotoCondensedBold,
                            fontSize: 10,
                            align: TextAnchor.MiddleLeft,
                            overflow: VerticalWrapMode.Overflow,
                            anchorMin: "0 1",
                            anchorMax: "0 1",
                            offsetMin: "20 -52",
                            offsetMax: "600 0",
                            name: "descr");

                        BattlePassRebornUI.Builder.Element sQXNjh = dBYwSV.AddContainer(
                            anchorMin: "0 1",
                            anchorMax: "0 1",
                            offsetMin: "20 -413",
                            offsetMax: "600 -52",
                            name: "reward_scroll_storage");

                        var scrollRoot = sQXNjh.AddPanel(
                            sprite: "",
                            material: "",
                            color: "1 1 1 0",
                            anchorMin: "0 0",
                            anchorMax: "1 1",
                            offsetMin: "0 0",
                            offsetMax: "0 0",
                            name: "ScrollRootStorageRewards");

                        scrollRoot.Components.AddScrollView(
                            horizontal: false,
                            vertical: true,
                            movementType: ScrollRect.MovementType.Elastic,
                            decelerationRate: 0.1f,
                            elasticity: 0.1f,
                            scrollSensitivity: 10f,
                            inertia: true,
                            verticalScrollbar: new CuiScrollbar
                            {
                                Invert = true,
                                HandleColor = "#FFFFFFFF",
                                HighlightColor = "#AAAAAAFF",
                                PressedColor = "#888888FF",
                                TrackColor = "#00000080",
                                HandleSprite = "assets/content/ui/ui.background.tiletex.psd",
                                TrackSprite = "assets/content/ui/ui.background.tiletex.psd",
                                AutoHide = true,
                                Size = -1
                            },
                            anchorMin: "0 1",
                            anchorMax: "1 1",
                            offsetMin: $"0 {-413 * ((int)(data.RewardList.Count / 11) + 1)}",
                            offsetMax: $"0 0");

                        var scrollRootPanel = scrollRoot.AddPanel(
                            sprite: "",
                            material: "",
                            color: "1 1 1 0",
                            anchorMin: "0 0",
                            anchorMax: "1 1",
                            offsetMin: "0 0",
                            offsetMax: "0 0",
                            name: "ScrollRootPanel");

                        {
                            int i = 0, j = 0, k = 0;

                            foreach (var reward in data.RewardList)
                            {
                                BattlePassRebornUI.Builder.Element dxdGnY = scrollRoot.AddContainer(
                                    anchorMin: "0 1",
                                    anchorMax: "0 1",
                                    offsetMin: $"{0 + 120 * i} {-140 - 150 * j}",
                                    offsetMax: $"{100 + 120 * i} {0 - 150 * j}",
                                    name: $"reward{k}");
                                {
                                    BattlePassRebornUI.Builder.Element XLrInu = dxdGnY.AddContainer(
                                        anchorMin: "0 1",
                                        anchorMax: "0 1",
                                        offsetMin: "0 -140",
                                        offsetMax: "100 0",
                                        name: "Container");
                                    BattlePassRebornUI.Builder.Element eFJxML = XLrInu.AddPanel(
                                        sprite: "",
                                        material: "",
                                        color: "0 0 0 0.7014",
                                        imageType: UnityEngine.UI.Image.Type.Simple,
                                        anchorMin: "0 0",
                                        anchorMax: "1 1",
                                        offsetMin: "0 0",
                                        offsetMax: "0 0",
                                        name: "bg");
                                    BattlePassRebornUI.Builder.Element vawAMJ = XLrInu.AddText(
                                        text: reward.displayName,
                                        color: "0.9254902 0.8901961 0.8588235 1",
                                        font: BattlePassRebornUI.Builder.Font.RobotoCondensedBold,
                                        fontSize: 12,
                                        align: TextAnchor.MiddleCenter,
                                        overflow: VerticalWrapMode.Overflow,
                                        anchorMin: "0 1",
                                        anchorMax: "0 1",
                                        offsetMin: "6 -35",
                                        offsetMax: "94 -6",
                                        name: "reward_title");
                                    var img = string.IsNullOrEmpty(reward.img) ? reward.ShortName : reward.img;
                                    if (img.StartsWith("https") || !string.IsNullOrEmpty(reward.img))
                                    {
                                        BattlePassRebornUI.Builder.Element ihqtOs = XLrInu.AddImage(
                                            content: GetImage(img),
                                            material: "",
                                            anchorMin: "0 1",
                                            anchorMax: "0 1",
                                            offsetMin: "10 -121",
                                            offsetMax: "90 -41",
                                            name: "Image");
                                    }
                                    else
                                    {
                                        BattlePassRebornUI.Builder.Element ihqtOs = XLrInu.AddIcon(
                                            itemId: _shortNameToItemID[img],
                                            material: "",
                                            anchorMin: "0 1",
                                            anchorMax: "0 1",
                                            offsetMin: "10 -121",
                                            offsetMax: "90 -41",
                                            name: "Image");
                                    }

                                    BattlePassRebornUI.Builder.Element hRTfcj = XLrInu.AddText(
                                        text: $"x{reward.Amount}",
                                        color: "0.9254902 0.8901961 0.8588235 1",
                                        font: BattlePassRebornUI.Builder.Font.RobotoCondensedBold,
                                        align: TextAnchor.MiddleRight,
                                        overflow: VerticalWrapMode.Overflow,
                                        anchorMin: "0 1",
                                        anchorMax: "0 1",
                                        offsetMin: "44 -134",
                                        offsetMax: "94 -120",
                                        name: "amound");
                                    BattlePassRebornUI.Builder.Element ehBQth = XLrInu.AddButton(
                                        command: $"UI_STORAGE_TAKE {k}",
                                        color: "0.1333333 0.1333333 0.1333333 0",
                                        imageType: UnityEngine.UI.Image.Type.Simple,
                                        anchorMin: "0 0",
                                        anchorMax: "1 1",
                                        offsetMin: "0 0",
                                        offsetMax: "0 0",
                                        name: "Btn take");
                                }
                                i++;
                                k++;

                                if (i == 5)
                                {
                                    i = 0;
                                    j++;
                                }
                            }
                        }
                    }
                    {
                    }
                }
            }
            root.Render(player);
        }

        private void ShowProgressBarBPUI(BasePlayer player)
        {
            if (!_UISettings.ProgressBar)
                return;
            CuiHelper.DestroyUi(player, "bp_progress_bar");
            var data = LoadPlayerData(player.userID);
            var settings = _allLevels.FirstOrDefault(p => p.LevelID == data.Level + 1);
            if (settings == null)
            {
                settings = _allLevels.LastOrDefault();
            }

            var expPerLevel = settings.ExpForLevel > 0 ? settings.ExpForLevel : _config.General.ExpPerLevel;

            BattlePassRebornUI.Builder.Root root = new BattlePassRebornUI.Builder.Root("Hud");
            {
                BattlePassRebornUI.Builder.Element bQnlBI = root.AddContainer(
                    anchorMin: "0 0",
                    anchorMax: "1 1",
                    offsetMin: "0 0",
                    offsetMax: "0 0",
                    name: "bp_progress_bar");
                BattlePassRebornUI.Builder.Element sKqnDQ = bQnlBI.AddPanel(
                    sprite: "",
                    material: "",
                    color: "0.5 0.5 0.5 0.4731",
                    imageType: UnityEngine.UI.Image.Type.Simple,
                    anchorMin: "0.5 0",
                    anchorMax: "0.5 0",
                    offsetMin: "-199 0",
                    offsetMax: "180 16",
                    name: "bp_progress_bar_panel");

                double progress = 379 * data.Exp / expPerLevel;

                progress = progress > 379 ? 379 : progress;

                BattlePassRebornUI.Builder.Element VQXSTy = sKqnDQ.AddPanel(
                    sprite: "",
                    material: "",
                    color: "0.3647059 0.4470588 0.2196078 1",
                    imageType: UnityEngine.UI.Image.Type.Simple,
                    anchorMin: "0 0",
                    anchorMax: "0 1",
                    offsetMin: "0 0",
                    offsetMax: $"{progress} 0",
                    name: "bp_progress_bar_panel_lvl_prog");

                BattlePassRebornUI.Builder.Element ZcKrvn = sKqnDQ.AddText(
                    text: $"{data.Exp}/{expPerLevel}",
                    font: BattlePassRebornUI.Builder.Font.RobotoCondensedBold,
                    fontSize: 12,
                    align: TextAnchor.MiddleLeft,
                    overflow: VerticalWrapMode.Overflow,
                    anchorMin: "1 0",
                    anchorMax: "1 1",
                    offsetMin: "-60 0",
                    offsetMax: "0 0",
                    name: "bp_progress_bar_panel_lvl");

                BattlePassRebornUI.Builder.Element ZYiSSE = sKqnDQ.AddText(
                    text: GetLang("TEXT_TIER", player.UserIDString, data.Level),
                    font: BattlePassRebornUI.Builder.Font.RobotoCondensedBold,
                    fontSize: 12,
                    align: TextAnchor.MiddleRight,
                    overflow: VerticalWrapMode.Overflow,
                    anchorMin: "1 0",
                    anchorMax: "1 1",
                    offsetMin: "-125 0",
                    offsetMax: "-65 0",
                    name: "bp_progress_bar_panel_next_tier");

                
            }
            root.Render(player);
        }

        private void PreloadScroll(BasePlayer player)
        {
            BattlePassRebornUI.Builder.Root root = new BattlePassRebornUI.Builder.Root("OverlayNonScaled");
            {
                BattlePassRebornUI.Builder.Element sQXNjh = root.AddContainer(
                    anchorMin: "0 0",
                    anchorMax: "0 0",
                    offsetMin: "0 0",
                    offsetMax: "0 0",
                    name: "reward_scroll");

                {
                    var scrollRoot = sQXNjh.AddPanel(
                        sprite: "",
                        material: "",
                        color: "1 1 1 0",
                        anchorMin: "0 0",
                        anchorMax: "1 1",
                        offsetMin: "0 0",
                        offsetMax: "0 0",
                        name: "ScrollRootRewards");

                    scrollRoot.Components.AddScrollView(
                        horizontal: true,
                        vertical: false,
                        inertia: true,
                        movementType: ScrollRect.MovementType.Elastic,
                        decelerationRate: 0.1f,
                        elasticity: 0.1f,
                        scrollSensitivity: 10f,
                        horizontalScrollbar: new CuiScrollbar
                        {
                            Invert = true,
                            HandleColor = "#FFFFFFFF",
                            HighlightColor = "#AAAAAAFF",
                            PressedColor = "#888888FF",
                            TrackColor = "#00000080",
                            HandleSprite = "assets/content/ui/ui.background.tiletex.psd",
                            TrackSprite = "assets/content/ui/ui.background.tiletex.psd",
                            AutoHide = true,
                            Size = -1
                        },
                        anchorMin: "0 0",
                        anchorMax: "1 1",
                        offsetMin: "0 0",
                        offsetMax: $"0 0");
                }
            }
            root.Render(player);
            CuiHelper.DestroyUi(player, "reward_scroll");
        }

        #region Types

        internal class UISettings
        {
            [JsonProperty(isEn ? "BackGround for BP (URL/Folder)" : "Картинка фона БП  (URL/Folder)")]
            public string BackGroundURL = "BG_BRBagG";

            [JsonProperty(isEn ? "Website URL" : "Ссылка для сайта")]
            public string URL1 = "www.rustserver.com/store";

            [JsonProperty(isEn ? "Discord URL" : "Ссылка для дискорда")]
            public string URL2 = "www.discord.com/rustserver";

            [JsonProperty(isEn
                ? "Hints for users in the footer (add only the key, don't forget to fill in the lang)"
                : "Подсказки для юзеров в футере (добавлять только ключ, не забудьте заполнить lang)")]
            public List<string> Hints = new List<string>();

            [JsonProperty(isEn ? "Is the progress bar enabled under the fast slots?" : "Включен ли progress bar под быстрыми слотами?")]
            public bool ProgressBar = true;

            [JsonProperty(isEn
                ? "Settings of button 'BUY PREMIUM PASS'"
                : "Настройка кнопки 'BUY PREMIUM PASS'")]
            public PremiumBannerUI PremBanner = new PremiumBannerUI();
        }

        internal class PremiumBannerUI
        {
            [JsonProperty(isEn ? "Text color" : "Цвет текста")]
            public string ColorText = "0.6156863 0.8117647 0.2705882 1";

            [JsonProperty(isEn ? "Panel color" : "Цвет панели")]
            public string ColorPanel = "0.3647059 0.4470588 0.2196078 1";

            [JsonProperty(isEn ? "offsetMin" : "offsetMin")]
            public string offsetMin = "225 -144";

            [JsonProperty(isEn ? "offsetMax" : "offsetMax")]
            public string offsetMax = "345 -118";
        }

        #endregion

        private const string UIFolder = "BattlePassReborn";

        private UISettings EnsureUIFileExists()
        {
            var uiFile = $"{UIFolder}/UI";

            if (!Interface.Oxide.DataFileSystem.ExistsDatafile(uiFile))
            {
                var defaultUI = new UISettings
                {
                    BackGroundURL = "BG_BRBagG",
                    URL1 = "www.rustserver.com/store",
                    URL2 = "www.discord.com/rustserver",
                    ProgressBar = true,
                    Hints = new List<string>
                    {
                        "TEXT_HINTS1", "TEXT_HINTS2", "TEXT_HINTS3", "TEXT_HINTS4"
                    },
                    PremBanner = new PremiumBannerUI()
                };

                Interface.Oxide.DataFileSystem.WriteObject(uiFile, defaultUI);
                Puts(isEn
                    ? "[BattlePassReborn] The UI.json file has been created."
                    : "[BattlePassReborn] Файл UI.json создан.");
            }
            var ui = Interface.Oxide.DataFileSystem.ReadObject<UISettings>(uiFile);
            Interface.Oxide.DataFileSystem.WriteObject(uiFile, ui);

            return ui;
        }

        #region Function

        private void cmdChat(BasePlayer player, string command, string[] args)
        {
            if (_config.General.AvailableBPPermission &&
                !permission.UserHasPermission(player.UserIDString, _config.General.BPPermission))
                return;

            ShowMainUI(player);
        }

        private void RefreshTask(BasePlayer player, string taskIDToReplace)
        {
            if (!_config.Tasks.RefreshTask)
                return;

            var playerData = LoadPlayerData(player.userID);

            if (playerData.CountRefresh <= 0)
            {
                player.ChatMessage(GetLang("TEXT_SPENTREFR", player.UserIDString));
                return;
            }

            var oldTask = playerData.Tasks.FirstOrDefault(t => t.TaskID == taskIDToReplace);
            if (oldTask == null)
                return;

            var oldMission = _allMissions.FirstOrDefault(m => m.TaskID == taskIDToReplace);
            if (oldMission == null)
                return;

            var difficulty = oldMission.Difficulty;

            var existingTaskIds = playerData.Tasks.Select(t => t.TaskID);

            var candidateTasks = _allMissions
                .Where(m => m.Difficulty == difficulty && !existingTaskIds.Contains(m.TaskID)).ToList();

            if (!candidateTasks.Any())
                return;

            var newMission = candidateTasks[_rand.Next(candidateTasks.Count)];

            var index = playerData.Tasks.FindIndex(t => t.TaskID == taskIDToReplace);
            if (index >= 0)
            {
                playerData.Tasks[index] = new PlayerTask
                {
                    TaskID = newMission.TaskID,
                    Completed = false,
                    Progress = 0
                };
            }

            playerData.CountRefresh--;


            RefreshTaskUI(player, playerData);
        }

        private void AddNewTaskAfterComplete(PlayerData playerData, string taskIDToReplace)
        {
            var oldTask = playerData.Tasks.FirstOrDefault(t => t.TaskID == taskIDToReplace);
            if (oldTask == null)
                return;

            var oldMission = _allMissions.FirstOrDefault(m => m.TaskID == taskIDToReplace);
            if (oldMission == null)
                return;

            var difficulty = oldMission.Difficulty;

            var existingTaskIds = playerData.Tasks.Select(t => t.TaskID);

            var candidateTasks = _allMissions
                .Where(m => m.Difficulty == difficulty && !existingTaskIds.Contains(m.TaskID)).ToList();

            if (!candidateTasks.Any())
                return;

            var newMission = candidateTasks[_rand.Next(candidateTasks.Count)];

            var index = playerData.Tasks.FindIndex(t => t.TaskID == taskIDToReplace);
            if (index >= 0)
            {
                playerData.Tasks[index] = new PlayerTask
                {
                    TaskID = newMission.TaskID,
                    Completed = false,
                    Progress = 0
                };
            }
        }

        private void RefreshTaskUI(BasePlayer player, PlayerData data)
        {
            var isPremium = permission.UserHasPermission(player.UserIDString, _config.General.PREMIUMRewardPermission);
            BattlePassRebornUI.Builder.Root root = new BattlePassRebornUI.Builder.Root("challenges");
            {
                if (_config.Tasks.RefreshTask)
                {
                    BattlePassRebornUI.Builder.Element xYuKTT = root.AddContainer(
                        anchorMin: "0 0",
                        anchorMax: "1 1",
                        offsetMin: "0 0",
                        offsetMax: "0 0",
                        name: "challenges_text");
                    var countRefreshes =
                        isPremium ? _config.Tasks.TaskRefreshPremiumCount : _config.Tasks.TaskRefreshCount;
                    BattlePassRebornUI.Builder.Element oUbrBa = xYuKTT.AddText(
                        text: GetLang("TEXT_DAILYREROLLS", player.UserIDString, data.CountRefresh, countRefreshes),
                        font: BattlePassRebornUI.Builder.Font.RobotoCondensedBold,
                        fontSize: 12,
                        align: TextAnchor.UpperRight,
                        overflow: VerticalWrapMode.Overflow,
                        anchorMin: "0 1",
                        anchorMax: "0 1",
                        offsetMin: "576 -44",
                        offsetMax: "776 -24",
                        name: "rerolls_info");
                }

                BattlePassRebornUI.Builder.Element GvTOGj = root.AddContainer(
                    anchorMin: "0 1",
                    anchorMax: "0 1",
                    offsetMin: "12 -288",
                    offsetMax: "784 -57",
                    name: "challenges_container");
                int easyMinY = -69, easyMaxY = 0, easyMinX = 0, easyMaxX = 244;
                int mediumMinY = -69, mediumMaxY = 0, mediumMinX = 264, mediumMaxX = 508;
                int hardMinY = -69, hardMaxY = 0, hardMinX = 528, hardMaxX = 772;
                int i = 0;

                foreach (var task in data.Tasks)
                {
                    var mission = _allMissions.FirstOrDefault(x => x.TaskID == task.TaskID);
                    if (mission == null)
                        continue;
                    int minY = 0, maxY = 0, minX = 0, maxX = 0;
                    string bg_Image = "", status = task.Completed ? "status_fScNCW" : "status_UBWXHE";

                    switch (mission.Difficulty)
                    {
                        case TaskDifficulty.Easy:
                            minY = easyMinY;
                            maxY = easyMaxY;
                            minX = easyMinX;
                            maxX = easyMaxX;
                            easyMinY -= 81;
                            easyMaxY -= 81;
                            bg_Image = "bg_UkKjRE";
                            break;

                        case TaskDifficulty.Medium:
                            minY = mediumMinY;
                            maxY = mediumMaxY;
                            minX = mediumMinX;
                            maxX = mediumMaxX;
                            mediumMinY -= 81;
                            mediumMaxY -= 81;
                            bg_Image = "bg_PeBYrm";
                            break;

                        case TaskDifficulty.Hard:
                            minY = hardMinY;
                            maxY = hardMaxY;
                            minX = hardMinX;
                            maxX = hardMaxX;
                            hardMinY -= 81;
                            hardMaxY -= 81;
                            bg_Image = "bg_xGzFMA";
                            break;

                        default:
                            continue;
                    }

                    BattlePassRebornUI.Builder.Element qJtrzc = GvTOGj.AddContainer(
                        anchorMin: "0 1",
                        anchorMax: "0 1",
                        offsetMin: $"{minX} {minY}",
                        offsetMax: $"{maxX} {maxY}",
                        name: $"easy_challenges_card_task{i}");
                    BattlePassRebornUI.Builder.Element UkKjRE = qJtrzc.AddImage(
                        content: GetImage(bg_Image),
                        material: "",
                        anchorMin: "0 0",
                        anchorMax: "1 1",
                        offsetMin: "0 0",
                        offsetMax: "0 0",
                        name: $"bg_task{i}");
                    BattlePassRebornUI.Builder.Element QODSLl = qJtrzc.AddText(
                        text: GetLang(mission.TaskID, player.UserIDString),
                        font: BattlePassRebornUI.Builder.Font.RobotoCondensedBold,
                        fontSize: 12,
                        align: TextAnchor.UpperLeft,
                        overflow: VerticalWrapMode.Overflow,
                        anchorMin: "0 1",
                        anchorMax: "0 1",
                        offsetMin: "13 -27",
                        offsetMax: "183 -13",
                        name: $"callenges_name_task{i}");
                    BattlePassRebornUI.Builder.Element iRfTPg = qJtrzc.AddText(
                        text: $"{task.Progress}/{mission.TargetCount}",
                        color: "0.7058824 0.7058824 0.7058824 1",
                        font: BattlePassRebornUI.Builder.Font.RobotoCondensedRegular,
                        fontSize: 10,
                        align: TextAnchor.UpperLeft,
                        overflow: VerticalWrapMode.Overflow,
                        anchorMin: "0 1",
                        anchorMax: "0 1",
                        offsetMin: "13 -61",
                        offsetMax: "183 -49",
                        name: $"callenges_info_otional_task{i}");
                    BattlePassRebornUI.Builder.Element fScNCW = qJtrzc.AddImage(
                        content: GetImage(status),
                        material: "",
                        anchorMin: "0 1",
                        anchorMax: "0 1",
                        offsetMin: "189 -55",
                        offsetMax: "231 -13",
                        name: $"status_task{i}");
                    {
                        if (!task.Completed && _config.Tasks.RefreshTask)
                        {
                            BattlePassRebornUI.Builder.Element zvCIHY = qJtrzc.AddButton(
                                command: $"UI_REFRESH_TASK {task.TaskID}",
                                color: "1 1 1 0.0999",
                                sprite: "",
                                material: "",
                                imageType: UnityEngine.UI.Image.Type.Simple,
                                anchorMin: "0 1",
                                anchorMax: "0 1",
                                offsetMin: "224 -61",
                                offsetMax: "236 -49",
                                name: $"reroll_task{i}");
                            BattlePassRebornUI.Builder.Element zZtJnt = zvCIHY.AddImage(
                                content: GetImage("Image_zZtJnt"),
                                material: "",
                                anchorMin: "0 0",
                                anchorMax: "1 1",
                                offsetMin: "0 0",
                                offsetMax: "0 0",
                                name: $"Image_task{i}");
                        }
                    }
                    i++;
                    if (i == 9)
                        break;
                }
            }
            root.Update(player);
        }

        private void RefreshRewardsUI(BasePlayer player, PlayerData data, string reward_container, bool prem)
        {
            BattlePassRebornUI.Builder.Root root = new BattlePassRebornUI.Builder.Root($"{reward_container}");
            {
                BattlePassRebornUI.Builder.Element YiMLYM = root.AddPanel(
                    sprite: "",
                    material: "",
                    color: "0 0 0 0.7977",
                    imageType: UnityEngine.UI.Image.Type.Simple,
                    anchorMin: "0 1",
                    anchorMax: "0 1",
                    offsetMin: "0 -140",
                    offsetMax: "100 0",
                    name: $"con_{reward_container}");
                BattlePassRebornUI.Builder.Element eUmWKQ = YiMLYM.AddText(
                    text: prem ? GetLang("TEXT_PREMIUM", player.UserIDString) : "",
                    font: BattlePassRebornUI.Builder.Font.RobotoCondensedBold,
                    fontSize: 12,
                    align: TextAnchor.MiddleCenter,
                    overflow: VerticalWrapMode.Overflow,
                    anchorMin: "0 1",
                    anchorMax: "1 1",
                    offsetMin: "0 -101",
                    offsetMax: "0 -83",
                    name: $"Text_{reward_container}");
                BattlePassRebornUI.Builder.Element iUQHgg = YiMLYM.AddImage(
                    content: GetImage("status_fScNCW"),
                    material: "",
                    anchorMin: "0.5 1",
                    anchorMax: "0.5 1",
                    offsetMin: "-16 -75",
                    offsetMax: "16 -43",
                    name: $"Image_{reward_container}");
            }
            root.Render(player);
        }

        private void RefreshStoragesUI(BasePlayer player, Storage data)
        {
            CuiHelper.DestroyUi(player, "ScrollRootStorageRewards");

            BattlePassRebornUI.Builder.Root root = new BattlePassRebornUI.Builder.Root("reward_scroll_storage");
            {
                var scrollRoot = root.AddPanel(
                    sprite: "",
                    material: "",
                    color: "1 1 1 0",
                    anchorMin: "0 0",
                    anchorMax: "1 1",
                    offsetMin: "0 0",
                    offsetMax: "0 0",
                    name: "ScrollRootStorageRewards");

                scrollRoot.Components.AddScrollView(
                    horizontal: false,
                    vertical: true,
                    movementType: ScrollRect.MovementType.Elastic,
                    decelerationRate: 0.1f,
                    elasticity: 0.1f,
                    scrollSensitivity: 10f,
                    verticalScrollbar: new CuiScrollbar
                    {
                        HandleColor = "#FFFFFFFF",
                        HighlightColor = "#AAAAAAFF",
                        PressedColor = "#888888FF",
                        TrackColor = "#00000080",
                        HandleSprite = "assets/content/ui/ui.background.tiletex.psd",
                        TrackSprite = "assets/content/ui/ui.background.tiletex.psd",
                        AutoHide = true,
                        Size = -1
                    },
                    anchorMin: "0 1",
                    anchorMax: "1 1",
                    offsetMin: $"0 {-413 * ((int)(data.RewardList.Count / 11) + 1)}",
                    offsetMax: $"0 0");

                var scrollRootPanel = scrollRoot.AddPanel(
                    sprite: "",
                    material: "",
                    color: "1 1 1 0",
                    anchorMin: "0 0",
                    anchorMax: "1 1",
                    offsetMin: "0 0",
                    offsetMax: "0 0",
                    name: "ScrollRootPanel");

                {
                    int i = 0, j = 0, k = 0;

                    foreach (var reward in data.RewardList)
                    {
                        BattlePassRebornUI.Builder.Element dxdGnY = scrollRoot.AddContainer(
                            anchorMin: "0 1",
                            anchorMax: "0 1",
                            offsetMin: $"{0 + 120 * i} {-140 - 150 * j}",
                            offsetMax: $"{100 + 120 * i} {0 - 150 * j}",
                            name: $"reward{k}");
                        {
                            BattlePassRebornUI.Builder.Element XLrInu = dxdGnY.AddContainer(
                                anchorMin: "0 1",
                                anchorMax: "0 1",
                                offsetMin: "0 -140",
                                offsetMax: "100 0",
                                name: "Container");
                            BattlePassRebornUI.Builder.Element eFJxML = XLrInu.AddPanel(
                                sprite: "",
                                material: "",
                                color: "0 0 0 0.7014",
                                imageType: UnityEngine.UI.Image.Type.Simple,
                                anchorMin: "0 0",
                                anchorMax: "1 1",
                                offsetMin: "0 0",
                                offsetMax: "0 0",
                                name: "bg");

                            BattlePassRebornUI.Builder.Element vawAMJ = XLrInu.AddText(
                                text: reward.displayName,
                                color: "0.9254902 0.8901961 0.8588235 1",
                                font: BattlePassRebornUI.Builder.Font.RobotoCondensedBold,
                                fontSize: 12,
                                align: TextAnchor.MiddleCenter,
                                overflow: VerticalWrapMode.Overflow,
                                anchorMin: "0 1",
                                anchorMax: "0 1",
                                offsetMin: "6 -35",
                                offsetMax: "94 -6",
                                name: "reward_title");
                            var img = string.IsNullOrEmpty(reward.img) ? reward.ShortName : reward.img;
                            if (img.StartsWith("https") || !string.IsNullOrEmpty(reward.img))
                            {
                                BattlePassRebornUI.Builder.Element ihqtOs = XLrInu.AddImage(
                                    content: GetImage(img),
                                    material: "",
                                    anchorMin: "0 1",
                                    anchorMax: "0 1",
                                    offsetMin: "10 -121",
                                    offsetMax: "90 -41",
                                    name: "Image");
                            }
                            else
                            {
                                BattlePassRebornUI.Builder.Element ihqtOs = XLrInu.AddIcon(
                                    itemId: _shortNameToItemID[img],
                                    material: "",
                                    anchorMin: "0 1",
                                    anchorMax: "0 1",
                                    offsetMin: "10 -121",
                                    offsetMax: "90 -41",
                                    name: "Image");
                            }

                            BattlePassRebornUI.Builder.Element hRTfcj = XLrInu.AddText(
                                text: $"x{reward.Amount}",
                                color: "0.9254902 0.8901961 0.8588235 1",
                                font: BattlePassRebornUI.Builder.Font.RobotoCondensedBold,
                                align: TextAnchor.MiddleRight,
                                overflow: VerticalWrapMode.Overflow,
                                anchorMin: "0 1",
                                anchorMax: "0 1",
                                offsetMin: "44 -134",
                                offsetMax: "94 -120",
                                name: "amound");
                            BattlePassRebornUI.Builder.Element ehBQth = XLrInu.AddButton(
                                command: $"UI_STORAGE_TAKE {k}",
                                color: "0.1333333 0.1333333 0.1333333 0",
                                imageType: UnityEngine.UI.Image.Type.Simple,
                                anchorMin: "0 0",
                                anchorMax: "1 1",
                                offsetMin: "0 0",
                                offsetMax: "0 0",
                                name: "Btn take");
                        }
                        i++;
                        k++;

                        if (i == 5)
                        {
                            i = 0;
                            j++;
                        }
                    }
                }
            }
            root.Render(player);
        }

        private void RefreshProgressBarUI(BasePlayer player, PlayerData data)
        {
            if (!_UISettings.ProgressBar)
                return;
            var settings = _allLevels.FirstOrDefault(p => p.LevelID == data.Level + 1);
            if (settings == null)
            {
                settings = _allLevels.LastOrDefault();
            }

            var expPerLevel = settings.ExpForLevel > 0 ? settings.ExpForLevel : _config.General.ExpPerLevel;

            BattlePassRebornUI.Builder.Root root = new BattlePassRebornUI.Builder.Root("Hud");
            {
                BattlePassRebornUI.Builder.Element bQnlBI = root.AddContainer(
                    anchorMin: "0 0",
                    anchorMax: "1 1",
                    offsetMin: "0 0",
                    offsetMax: "0 0",
                    name: "bp_progress_bar");
                BattlePassRebornUI.Builder.Element sKqnDQ = bQnlBI.AddPanel(
                    sprite: "",
                    material: "",
                    color: "0.5 0.5 0.5 0.4731",
                    imageType: UnityEngine.UI.Image.Type.Simple,
                    anchorMin: "0.5 0",
                    anchorMax: "0.5 0",
                    offsetMin: "-199 0",
                    offsetMax: "180 16",
                    name: "bp_progress_bar_panel");

                double progress = 379 * data.Exp / expPerLevel;

                progress = progress > 379 ? 379 : progress;

                BattlePassRebornUI.Builder.Element VQXSTy = sKqnDQ.AddPanel(
                    sprite: "",
                    material: "",
                    color: "0.3647059 0.4470588 0.2196078 1",
                    imageType: UnityEngine.UI.Image.Type.Simple,
                    anchorMin: "0 0",
                    anchorMax: "0 1",
                    offsetMin: "0 0",
                    offsetMax: $"{progress} 0",
                    name: "bp_progress_bar_panel_lvl_prog");

                BattlePassRebornUI.Builder.Element ZcKrvn = sKqnDQ.AddText(
                    text: $"{data.Exp}/{expPerLevel}",
                    font: BattlePassRebornUI.Builder.Font.RobotoCondensedBold,
                    fontSize: 12,
                    align: TextAnchor.MiddleLeft,
                    overflow: VerticalWrapMode.Overflow,
                    anchorMin: "1 0",
                    anchorMax: "1 1",
                    offsetMin: "-60 0",
                    offsetMax: "0 0",
                    name: "bp_progress_bar_panel_lvl");

                BattlePassRebornUI.Builder.Element ZYiSSE = sKqnDQ.AddText(
                    text: GetLang("TEXT_TIER", player.UserIDString, data.Level),
                    font: BattlePassRebornUI.Builder.Font.RobotoCondensedBold,
                    fontSize: 12,
                    align: TextAnchor.MiddleRight,
                    overflow: VerticalWrapMode.Overflow,
                    anchorMin: "1 0",
                    anchorMax: "1 1",
                    offsetMin: "-125 0",
                    offsetMax: "-65 0",
                    name: "bp_progress_bar_panel_next_tier");

                
            }
            root.Update(player);
        }

        [ConsoleCommand("UI_TAKE")]
        private void cmdConsoleUI_TAKE(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            var playerData = LoadPlayerData(player.userID);
            var storagePlayerData = LoadStoragePlayerData(player.userID, player.displayName);
            // CHANGE: Added ToString() to handle StringView -> string conversion after Rust update
            var levelID = int.Parse(arg.Args[0].ToString());
            bool priv = arg.Args[1].ToString() == "PREM" ? true : false;
            var reward_container = arg.Args[2].ToString();

            var lvlSettings = _allLevels.FirstOrDefault(x => x.LevelID == levelID);
            if (lvlSettings == null)
                return;

            var rewardPlayer = playerData.RewardsReceived.FirstOrDefault(x => x.Level == levelID);
            if (rewardPlayer != null)
            {
                if (priv && rewardPlayer.PremiumReward)
                    return;
                else if (!priv && rewardPlayer.DefaultReward)
                    return;
            }

            if (rewardPlayer == null)
            {
                var newReward = new Rewards
                {
                    Level = levelID,
                    DefaultReward = false,
                    PremiumReward = false
                };
                playerData.RewardsReceived.Add(newReward);
                rewardPlayer = newReward;
            }

            if (priv)
                rewardPlayer.PremiumReward = true;
            else
                rewardPlayer.DefaultReward = true;


            ShowGreenTip(player, GetLang("TEXT_REWARDCLAIMED", player.UserIDString), 5f);
            RefreshRewardsUI(player, playerData, reward_container, priv);

            if (_config.Discord.LogRewardClaim)
                // CHANGE: Added ToString() to handle StringView -> string conversion after Rust update
                SendDiscordSimple(GetLang("TEXT_REWARDCLAIMEDDISCORD", player.UserIDString, player.displayName, player.UserIDString, levelID, arg.Args[1].ToString()));


            var rewards = lvlSettings.RewardList.Where(x => x.IsPremium == priv);
            foreach (var rewardTemplate in rewards)
            {
                var playerReward = new LevelRewardStorage
                {
                    ShortName = rewardTemplate.ShortName,
                    Amount = rewardTemplate.Amount,
                    IsPremium = rewardTemplate.IsPremium,
                    displayName = rewardTemplate.displayName,
                    img = rewardTemplate.img,
                    SkinID = rewardTemplate.SkinID,
                    CmdList = rewardTemplate.CmdList?.ToList() ?? new List<string>(),
                    isBlueprint = rewardTemplate.isBlueprint,
                    DateInStorage = DateTime.Today
                };

                storagePlayerData.RewardList.Add(playerReward);
            }
        }

        [ConsoleCommand("UI_REFRESH_TASK")]
        private void cmdConsoleUI_REFRESH_TASK(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            // CHANGE: Added ToString() to handle StringView -> string conversion after Rust update
            var taskID = arg.Args[0].ToString();
            RefreshTask(player, taskID);
        }

        [ConsoleCommand("UI_PremiumPassBaner")]
        private void cmdConsoleUI_PREMIUMPASS_BANER(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            ShowPremiumPassBanerUI(player);
        }

        [ConsoleCommand("UI_STORAGE")]
        private void cmdConsoleUI_STORAGE(ConsoleSystem.Arg arg)
        {
            double timeLeft = Math.Abs(LastWipe + _config.StorageS.StorageCooldownAfterWipeMinutes - CurrentTime);
            if (timeLeft < _config.StorageS.StorageCooldownAfterWipeMinutes * 60)
                return;

            var player = arg.Player();
            ShowStorageUI(player);
        }

        [ConsoleCommand("UI_STORAGE_TAKE")]
        private void cmdConsoleUI_STORAGE_TAKE(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            var data = LoadStoragePlayerData(player.userID);

            // CHANGE: Added ToString() to handle StringView -> string conversion after Rust update
            int reward = int.Parse(arg.Args[0].ToString());

            if (data.RewardList == null || data.RewardList.Count == 0 || reward >= data.RewardList.Count)
            {
                return;
            }

            GiveItemToPlayer(player, data.RewardList[reward]);

            data.RewardList.Remove(data.RewardList[reward]);

            ShowGreenTip(player, GetLang("TEXT_ITEMCLAIMED", player.UserIDString), 5f);

            RefreshStoragesUI(player, data);
        }

        private void ShowGreenTip(BasePlayer player, string message, float duration = 5f)
        {
            CuiHelper.DestroyUi(player, "GreenTip");
            BattlePassRebornUI.Builder.Root root = new BattlePassRebornUI.Builder.Root("OverlayNonScaled");
            {
                var data = LoadStoragePlayerData(player.userID);
                BattlePassRebornUI.Builder.Element bQnlBI = root.AddContainer(
                    anchorMin: "0 0",
                    anchorMax: "1 1",
                    offsetMin: "0 0",
                    offsetMax: "0 0",
                    name: "GreenTip");
                BattlePassRebornUI.Builder.Element sKqnDQ = bQnlBI.AddPanel(
                    sprite: "",
                    material: "",
                    color: "0.36 0.45 0.22 0.9593",
                    imageType: UnityEngine.UI.Image.Type.Simple,
                    anchorMin: "0.5 0",
                    anchorMax: "0.5 0",
                    offsetMin: "-185.5 40",
                    offsetMax: "185.5 77",
                    name: "bg");
                BattlePassRebornUI.Builder.Element wjxnyw = sKqnDQ.AddText(
                    text: message,
                    font: BattlePassRebornUI.Builder.Font.RobotoCondensedBold,
                    fontSize: 16,
                    align: TextAnchor.MiddleCenter,
                    overflow: VerticalWrapMode.Overflow,
                    anchorMin: "0 0",
                    anchorMax: "1 1",
                    name: "title");
            }
            root.Render(player);

            ServerMgr.Instance.Invoke(() => { CuiHelper.DestroyUi(player, "GreenTip"); }, duration);
        }

        #endregion
    }

    #endregion

    #region Image

    partial class BattlePassReborn
    {
        private const string ImageFolder = "BattlePassReborn/Images/";

        #region Helpers

        private readonly List<ImageSettings> ImageSettingsList = new List<ImageSettings>
        {
            new ImageSettings
            {
                Name = "img_shadow_KpZTtS",
                Url = "img_shadow_KpZTtS"
            },
            new ImageSettings
            {
                Name = "Image_OxvJwN",
                Url = "Image_OxvJwN"
            },
            new ImageSettings
            {
                Name = "Image_jpGRHv",
                Url = "Image_jpGRHv"
            },
            new ImageSettings
            {
                Name = "bg_UkKjRE",
                Url = "bg_UkKjRE"
            },
            new ImageSettings
            {
                Name = "status_fScNCW",
                Url = "status_fScNCW"
            },
            new ImageSettings
            {
                Name = "Image_zZtJnt",
                Url = "Image_zZtJnt"
            },
            new ImageSettings
            {
                Name = "status_UBWXHE",
                Url = "status_UBWXHE"
            },
            new ImageSettings
            {
                Name = "bg_PeBYrm",
                Url = "bg_PeBYrm"
            },
            new ImageSettings
            {
                Name = "bg_xGzFMA",
                Url = "bg_xGzFMA"
            },
            new ImageSettings
            {
                Name = "Image_iUQHgg",
                Url = "Image_iUQHgg"
            },
            new ImageSettings
            {
                Name = "Image_ieeMqn",
                Url = "Image_ieeMqn"
            },
            new ImageSettings
            {
                Name = "Image_BwnjeJ",
                Url = "Image_BwnjeJ"
            },
            new ImageSettings
            {
                Name = "bg_dBYwSV",
                Url = "bg_dBYwSV"
            },
            new ImageSettings
            {
                Name = "Frame",
                Url = "Frame"
            }
        };

        private readonly Dictionary<string, string> ImageList = new Dictionary<string, string>();

        private class ImageSettings
        {
            [JsonProperty(PropertyName = "Name", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public string Name;

            [JsonProperty(PropertyName = "Path", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public string Url;
        }

        private void DownloadImage()
        {
            var image = ImageSettingsList.FirstOrDefault(p => !ImageList.ContainsKey(p.Name));
            if (image == null)
            {
                Puts("Image upload completed");
                return;
            }

            ServerMgr.Instance.StartCoroutine(StartDownloadImage(image));
        }

        private IEnumerator StartDownloadImage(ImageSettings image)
        {
            if (string.IsNullOrEmpty(image?.Url) || string.IsNullOrEmpty(image.Name))
            {
                ImageList[image.Name] = "";
                DownloadImage();
                yield break;
            }

            if (image.Url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                using (var www = new WWW(image.Url))
                {
                    yield return www;
                    if (!string.IsNullOrEmpty(www.error))
                    {
                        PrintError(
                            $"Failed to download image {image.Name}. Address [{image.Url}] invalid: {www.error}");
                        ImageList[image.Name] = "";
                    }
                    else
                    {
                        var texture = www.texture;
                        if (texture != null)
                        {
                            var png = FileStorage.server.Store(texture.EncodeToPNG(), FileStorage.Type.png,
                                CommunityEntity.ServerInstance.net.ID).ToString();
                            ImageList[image.Name] = png;
                        }
                        else
                        {
                            ImageList[image.Name] = "";
                        }
                    }
                }
            }
            else
            {
                string fileUrl = "file://" + Interface.Oxide.DataDirectory + Path.DirectorySeparatorChar + ImageFolder +
                                 image.Name + ".png";

                using (var www = new WWW(fileUrl))
                {
                    yield return www;
                    if (!string.IsNullOrEmpty(www.error))
                    {
                        PrintError($"Failed to load local image {image.Name} from {fileUrl}: {www.error}");
                        ImageList[image.Name] = "";
                    }
                    else
                    {
                        if (www.error == null)
                        {
                            Texture2D tex = www.texture;
                            var png = FileStorage.server.Store(tex.EncodeToPNG(), FileStorage.Type.png,
                                CommunityEntity.ServerInstance.net.ID).ToString();
                            ImageList[image.Name] = png;
                        }
                        else
                        {
                            ImageList[image.Name] = "";
                        }
                    }
                }
            }

            DownloadImage();
        }

        #endregion

        private string GetImage(string imageName)
        {
            try
            {
                return ImageList[imageName];
            }
            catch (Exception ex)
            {
                Interface.Oxide.LogError(ex.Message);
                return "";
            }
        }
    }

    #endregion

    #region PlayerData

    partial class BattlePassReborn
    {
        #region Types

        internal class PlayerData
        {
            [JsonProperty("Lvl")] public int Level = 0;
            [JsonProperty("Exp")] public int Exp = 0;
            [JsonProperty("Premium")] public bool Premium = false;

            [JsonProperty("T")] public List<PlayerTask> Tasks = new List<PlayerTask>();

            [JsonProperty("Last")] public int Day = DateTime.UtcNow.Day;

            [JsonProperty("Count of Refresh Task")]
            public int CountRefresh = 0;

            [JsonProperty("Count of Completed Task")]
            public int CountCompletedTask = 0;

            [JsonProperty("LastActive")] 
            public long LastActiveTimestamp = DateTime.UtcNow.Ticks;

            [JsonProperty("Rewards received")] public List<Rewards> RewardsReceived = new List<Rewards>();
        }

        internal class Rewards
        {
            [JsonProperty("LVL")] public int Level;

            [JsonProperty("DefReward")] public bool DefaultReward = false;

            [JsonProperty("PremReward")] public bool PremiumReward = false;
        }

        internal class PlayerTask
        {
            [JsonProperty("Id")] public string TaskID;

            [JsonProperty("C")] public bool Completed;

            [JsonProperty("P")] public int Progress = 0;
        }

        #endregion

        #region PlayerData

        private const string DataFolder = "BattlePassReborn/Players";
        private const string ArchiveFolder = "/BattlePassReborn/Archive";

        private PlayerData LoadPlayerData(ulong userId, string playerName = "")
        {

            if (_dataPlayers.TryGetValue(userId, out var cachedData))
            {
                return cachedData;
            }

            var fileName = $"{DataFolder}/{userId}";
            var data = new PlayerData();
            var isPremium = permission.UserHasPermission(userId.ToString(), _config.General.PREMIUMRewardPermission);

            if (!Interface.Oxide.DataFileSystem.ExistsDatafile(fileName))
            {

                data = new PlayerData
                {
                    Level = 0,
                    Exp = 0,
                    Premium = isPremium,
                    Tasks = GenerateTasks(),
                    Day = DateTime.UtcNow.Day,
                    CountRefresh = _config.Tasks.RefreshTask
                        ? (isPremium ? _config.Tasks.TaskRefreshPremiumCount : _config.Tasks.TaskRefreshCount)
                        : 0,
                    CountCompletedTask = 0,
                };

                SavePlayerData(userId, data);
            }
            else
            {
                data = Interface.Oxide.DataFileSystem.ReadObject<PlayerData>(fileName);
                if (data.Day != DateTime.UtcNow.Day)
                {
                    data.Tasks = GenerateTasks();
                    data.Day = DateTime.UtcNow.Day;
                    data.Premium = isPremium;
                    data.CountRefresh = _config.Tasks.RefreshTask
                        ? (isPremium ? _config.Tasks.TaskRefreshPremiumCount : _config.Tasks.TaskRefreshCount)
                        : 0;
                    data.CountCompletedTask = 0;
                    SavePlayerData(userId, data);
                }
            }

            if (_config.General.AvailableBPPermission &&
                !permission.UserHasPermission(userId.ToString(), _config.General.BPPermission))
            {
                data.Tasks.Clear();
            }

            data.LastActiveTimestamp = DateTime.UtcNow.Ticks;

            _dataPlayers[userId] = data;

            return data;
        }

        private void SavePlayerData(ulong userId, PlayerData data)
        {
            var fileName = $"{DataFolder}/{userId}";
            Interface.Oxide.DataFileSystem.WriteObject(fileName, data);
        }

        private List<PlayerTask> GenerateTasks()
        {
            var result = new List<PlayerTask>();

            var easyTasks = _allMissions
                .Where(t => t.Difficulty == TaskDifficulty.Easy)
                .OrderBy(x => _rand.Next()).Take(3);

            var mediumTasks = _allMissions
                .Where(t => t.Difficulty == TaskDifficulty.Medium)
                .OrderBy(x => _rand.Next()).Take(3);

            var hardTasks = _allMissions
                .Where(t => t.Difficulty == TaskDifficulty.Hard)
                .OrderBy(x => _rand.Next()).Take(3);

            foreach (var task in easyTasks.Concat(mediumTasks).Concat(hardTasks))
            {
                result.Add(new PlayerTask
                {
                    TaskID = task.TaskID,
                    Completed = false
                });
            }

            return result;
        }

        private void ArchiveData(bool deleteOriginals = false)
        {
            try
            {
                var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
                var archiveDirectory = Interface.Oxide.DataFileSystem.Directory + ArchiveFolder;

                if (!Directory.Exists(archiveDirectory))
                    Directory.CreateDirectory(archiveDirectory);

                var archivePath = Path.Combine(Interface.Oxide.DataFileSystem.Directory + ArchiveFolder, timestamp);

                if (!Directory.Exists(archivePath))
                    Directory.CreateDirectory(archivePath);

                var files = Interface.Oxide.DataFileSystem.GetFiles(DataFolder, "*");
                foreach (var file in files)
                {
                    var fileName = Path.GetFileName(file);
                    if (_config.Players.ArchiveSeasonData)
                    {
                        var destFile = Path.Combine(archivePath, fileName);
                        File.Copy(file, destFile, overwrite: true);
                    }

                    if (deleteOriginals)
                        File.Delete(file);
                }

                Puts(isEn
                    ? $"[{Title}] The data is archived in: {archivePath} ({files.Length} files)"
                    : $"[{Title}] Данные архивируются в: {archivePath} ({files.Length} файлы)");
            }
            catch (Exception ex)
            {
                PrintError(isEn
                    ? $"[{Title}] Error in archiving: {ex.Message}"
                    : $"[{Title}] Ошибка при архивации: {ex.Message}");
            }
        }

        private void RemoveInactivePlayers()
        {
            if (_config.Players.InactiveDeleteAfterDays <= 0) return;

            var cutoffTicks = DateTime.UtcNow.AddDays(-_config.Players.InactiveDeleteAfterDays).Ticks;
            var playerFiles = Interface.Oxide.DataFileSystem.GetFiles(DataFolder, "*");
            int removedCount = 0;

            foreach (var file in playerFiles)
            {
                try
                {
                    var userIdStr = Path.GetFileNameWithoutExtension(file);
                    if (!ulong.TryParse(userIdStr, out var id)) continue;
                    
                    if(!id.IsSteamId())
                    {
                        File.Delete(file);
                        removedCount++;
                        continue;
                    }

                    var data = Interface.Oxide.DataFileSystem.ReadObject<PlayerData>($"{DataFolder}/{userIdStr}");

                    if (data.LastActiveTimestamp == 0)
                    {
                        continue;
                    }

                    if (data.LastActiveTimestamp < cutoffTicks)
                    {
                        File.Delete(file);
                        removedCount++;
                    }
                }
                catch (Exception ex)
                {
                    PrintWarning($"Failed to process player file {file}: {ex.Message}");
                }
            }

            if (removedCount > 0)
            {
                Puts(isEn
                    ? $"[BattlePassReborn] Removed {removedCount} inactive or invalid player file(s)."
                    : $"[BattlePassReborn] Удалено {removedCount} неактивных или некорректных файлов игроков.");
            }
        }

        #endregion
    }

    #endregion

    #region Missions

    partial class BattlePassReborn
    {
        #region Types

        internal class MissionTask
        {
            [JsonProperty("Id")] public string TaskID;

            [JsonProperty("Diff")] public TaskDifficulty Difficulty;

            [JsonProperty("EventType")] public string EventType;
            [JsonProperty("Conditions")] public List<string> Conditions = new List<string>();
            [JsonProperty("TargetCount")] public int TargetCount = 1;
        }

        internal enum TaskDifficulty
        {
            Easy = 0,
            Medium = 1,
            Hard = 2
        }

        #endregion

        #region MissionsData

        private const string MissionsFolder = "BattlePassReborn/Missions";

        private List<MissionTask> EnsureMissionsFileExists()
        {
            var missionsFile = $"{MissionsFolder}/{_config.Tasks.fileName}";

            if (!Interface.Oxide.DataFileSystem.ExistsDatafile(missionsFile))
            {
                var defaultMissions = new List<MissionTask>
                {
                    new MissionTask
                    {
                        TaskID = "extract_stone_2000", Difficulty = TaskDifficulty.Easy, EventType = "extract",
                        Conditions = new List<string> { "stones" }, TargetCount = 2000
                    },
                    new MissionTask
                    {
                        TaskID = "extract_metal.ore_1000", Difficulty = TaskDifficulty.Easy, EventType = "extract",
                        Conditions = new List<string> { "metal.ore" }, TargetCount = 1000
                    },
                    new MissionTask
                    {
                        TaskID = "extract_sulfur_900", Difficulty = TaskDifficulty.Easy, EventType = "extract",
                        Conditions = new List<string> { "sulfur.ore" }, TargetCount = 900
                    },
                    new MissionTask
                    {
                        TaskID = "craft_crossbow", Difficulty = TaskDifficulty.Easy, EventType = "craft",
                        Conditions = new List<string> { "crossbow" }, TargetCount = 1
                    },
                    new MissionTask
                    {
                        TaskID = "craft_nailgun", Difficulty = TaskDifficulty.Easy, EventType = "craft",
                        Conditions = new List<string> { "pistol.nailgun" }, TargetCount = 1
                    },
                    new MissionTask
                    {
                        TaskID = "craft_revolver", Difficulty = TaskDifficulty.Easy, EventType = "craft",
                        Conditions = new List<string> { "pistol.revolver" }, TargetCount = 1
                    },
                    new MissionTask
                    {
                        TaskID = "craft_double", Difficulty = TaskDifficulty.Easy, EventType = "craft",
                        Conditions = new List<string> { "shotgun.double" }, TargetCount = 1
                    },
                    new MissionTask
                    {
                        TaskID = "craft_furnace", Difficulty = TaskDifficulty.Easy, EventType = "craft",
                        Conditions = new List<string> { "furnace" }, TargetCount = 1
                    },
                    new MissionTask
                    {
                        TaskID = "craft_bed", Difficulty = TaskDifficulty.Easy, EventType = "craft",
                        Conditions = new List<string> { "bed" }, TargetCount = 1
                    },
                    new MissionTask
                    {
                        TaskID = "kill_players_3", Difficulty = TaskDifficulty.Easy, EventType = "kill",
                        Conditions = new List<string> { "player" }, TargetCount = 3
                    },
                    new MissionTask
                    {
                        TaskID = "kill_scientist_3", Difficulty = TaskDifficulty.Easy, EventType = "kill",
                        Conditions = new List<string> { "scientist" }, TargetCount = 5
                    },
                    new MissionTask
                    {
                        TaskID = "craft_shelter", Difficulty = TaskDifficulty.Easy, EventType = "craft",
                        Conditions = new List<string> { "legacy.shelter.wood" }, TargetCount = 1
                    },
                    new MissionTask
                    {
                        TaskID = "fishcatch_smallshark_1", Difficulty = TaskDifficulty.Easy, EventType = "fishcatch",
                        Conditions = new List<string> { "fish.smallshark" }, TargetCount = 1
                    },
                    new MissionTask
                    {
                        TaskID = "extract_salmon_1", Difficulty = TaskDifficulty.Easy, EventType = "fishcatch",
                        Conditions = new List<string> { "fish.salmon" }, TargetCount = 1
                    },
                    new MissionTask
                    {
                        TaskID = "kill_chicken_3", Difficulty = TaskDifficulty.Easy, EventType = "kill",
                        Conditions = new List<string> { "chicken" }, TargetCount = 3
                    },
                    new MissionTask
                    {
                        TaskID = "kill_stag_3", Difficulty = TaskDifficulty.Easy, EventType = "kill",
                        Conditions = new List<string> { "stag" }, TargetCount = 3
                    },
                    new MissionTask
                    {
                        TaskID = "kill_boar_3", Difficulty = TaskDifficulty.Easy, EventType = "kill",
                        Conditions = new List<string> { "boar" }, TargetCount = 3
                    },
                    new MissionTask
                    {
                        TaskID = "pickup_mushroom_10", Difficulty = TaskDifficulty.Easy, EventType = "pickup",
                        Conditions = new List<string> { "mushroom" }, TargetCount = 10
                    },
                    new MissionTask
                    {
                        TaskID = "loot_crate_normal_2", Difficulty = TaskDifficulty.Easy, EventType = "loot",
                        Conditions = new List<string> { "crate_normal_2" }, TargetCount = 3
                    },
                    new MissionTask
                    {
                        TaskID = "extract_stone_4000", Difficulty = TaskDifficulty.Medium, EventType = "extract",
                        Conditions = new List<string> { "stones" }, TargetCount = 4000
                    },
                    new MissionTask
                    {
                        TaskID = "extract_metal.ore_2000", Difficulty = TaskDifficulty.Medium, EventType = "extract",
                        Conditions = new List<string> { "metal.ore" }, TargetCount = 2000
                    },
                    new MissionTask
                    {
                        TaskID = "extract_sulfur_2000", Difficulty = TaskDifficulty.Medium, EventType = "extract",
                        Conditions = new List<string> { "sulfur.ore" }, TargetCount = 2000
                    },
                    new MissionTask
                    {
                        TaskID = "craft_rocket_hv", Difficulty = TaskDifficulty.Medium, EventType = "craft",
                        Conditions = new List<string> { "ammo.rocket.hv" }, TargetCount = 3
                    },
                    new MissionTask
                    {
                        TaskID = "craft_pump", Difficulty = TaskDifficulty.Medium, EventType = "craft",
                        Conditions = new List<string> { "shotgun.pump" }, TargetCount = 1
                    },
                    new MissionTask
                    {
                        TaskID = "craft_icepick", Difficulty = TaskDifficulty.Medium, EventType = "craft",
                        Conditions = new List<string> { "icepick.salvaged" }, TargetCount = 1
                    },
                    new MissionTask
                    {
                        TaskID = "craft_semiauto", Difficulty = TaskDifficulty.Medium, EventType = "craft",
                        Conditions = new List<string> { "rifle.semiauto" }, TargetCount = 1
                    },
                    new MissionTask
                    {
                        TaskID = "craft_satchel", Difficulty = TaskDifficulty.Medium, EventType = "craft",
                        Conditions = new List<string> { "explosive.satchel" }, TargetCount = 4
                    },
                    new MissionTask
                    {
                        TaskID = "craft_smg", Difficulty = TaskDifficulty.Medium, EventType = "craft",
                        Conditions = new List<string> { "smg.2" }, TargetCount = 1
                    },
                    new MissionTask
                    {
                        TaskID = "kill_players_10", Difficulty = TaskDifficulty.Medium, EventType = "kill",
                        Conditions = new List<string> { "player" }, TargetCount = 10
                    },
                    new MissionTask
                    {
                        TaskID = "kill_scientistnpc_heavy_10", Difficulty = TaskDifficulty.Medium, EventType = "kill",
                        Conditions = new List<string> { "scientistnpc_heavy", "scientistnpc_bradley_heavy" },
                        TargetCount = 10
                    },
                    new MissionTask
                    {
                        TaskID = "craft_electric_furnace", Difficulty = TaskDifficulty.Medium, EventType = "craft",
                        Conditions = new List<string> { "electric.furnace" }, TargetCount = 3
                    },
                    new MissionTask
                    {
                        TaskID = "fishcatch_catfish_10", Difficulty = TaskDifficulty.Medium, EventType = "fishcatch",
                        Conditions = new List<string> { "fish.catfish" }, TargetCount = 10
                    },
                    new MissionTask
                    {
                        TaskID = "extract_troutsmall_10", Difficulty = TaskDifficulty.Medium, EventType = "fishcatch",
                        Conditions = new List<string> { "fish.troutsmall" }, TargetCount = 10
                    },
                    new MissionTask
                    {
                        TaskID = "kill_wolf_3", Difficulty = TaskDifficulty.Medium, EventType = "kill",
                        Conditions = new List<string> { "wolf" }, TargetCount = 3
                    },
                    new MissionTask
                    {
                        TaskID = "kill_bear_3", Difficulty = TaskDifficulty.Medium, EventType = "kill",
                        Conditions = new List<string> { "bear" }, TargetCount = 3
                    },
                    new MissionTask
                    {
                        TaskID = "kill_crocodile_1", Difficulty = TaskDifficulty.Medium, EventType = "kill",
                        Conditions = new List<string> { "crocodile" }, TargetCount = 1
                    },
                    new MissionTask
                    {
                        TaskID = "kill_barrel_40", Difficulty = TaskDifficulty.Medium, EventType = "kill",
                        Conditions = new List<string>
                            { "loot-barrel-1", "loot-barrel-2", "loot_barrel_1", "loot_barrel_2", "oil_barrel" },
                        TargetCount = 40
                    },
                    new MissionTask
                    {
                        TaskID = "loot_crate_normal", Difficulty = TaskDifficulty.Medium, EventType = "loot",
                        Conditions = new List<string> { "crate_normal" }, TargetCount = 3
                    },
                    new MissionTask
                    {
                        TaskID = "loot_codelockedhackablecrate_1", Difficulty = TaskDifficulty.Medium,
                        EventType = "loot",
                        Conditions = new List<string> { "codelockedhackablecrate", "codelockedhackablecrate_oilrig" },
                        TargetCount = 1
                    },
                    new MissionTask
                    {
                        TaskID = "kill_players_20", Difficulty = TaskDifficulty.Medium, EventType = "kill",
                        Conditions = new List<string> { "player" }, TargetCount = 20
                    },
                    new MissionTask
                    {
                        TaskID = "cardswipe_blue_2", Difficulty = TaskDifficulty.Medium, EventType = "cardswipe",
                        Conditions = new List<string> { "keycard_blue" }, TargetCount = 2
                    },
                    new MissionTask
                    {
                        TaskID = "loot_codelockedhackablecrate_hard", Difficulty = TaskDifficulty.Hard,
                        EventType = "loot",
                        Conditions = new List<string> { "codelockedhackablecrate", "codelockedhackablecrate_oilrig" },
                        TargetCount = 3
                    },
                    new MissionTask
                    {
                        TaskID = "extract_sulfur_6000", Difficulty = TaskDifficulty.Hard, EventType = "extract",
                        Conditions = new List<string> { "sulfur.ore" }, TargetCount = 6000
                    },
                    new MissionTask
                    {
                        TaskID = "craft_explosive_timed", Difficulty = TaskDifficulty.Hard, EventType = "craft",
                        Conditions = new List<string> { "explosive.timed" }, TargetCount = 2
                    },
                    new MissionTask
                    {
                        TaskID = "craft_ak", Difficulty = TaskDifficulty.Hard, EventType = "craft",
                        Conditions = new List<string> { "rifle.ak" }, TargetCount = 1
                    },
                    new MissionTask
                    {
                        TaskID = "craft_bolt", Difficulty = TaskDifficulty.Hard, EventType = "craft",
                        Conditions = new List<string> { "rifle.bolt" }, TargetCount = 1
                    },
                    new MissionTask
                    {
                        TaskID = "craft_ammo_explosive", Difficulty = TaskDifficulty.Hard, EventType = "craft",
                        Conditions = new List<string> { "ammo.rifle.explosive" }, TargetCount = 128
                    },
                    new MissionTask
                    {
                        TaskID = "craft_door_toptier", Difficulty = TaskDifficulty.Hard, EventType = "craft",
                        Conditions = new List<string> { "door.double.hinged.toptier" }, TargetCount = 1
                    },
                    new MissionTask
                    {
                        TaskID = "kill_players_50", Difficulty = TaskDifficulty.Hard, EventType = "kill",
                        Conditions = new List<string> { "player" }, TargetCount = 50
                    },
                    new MissionTask
                    {
                        TaskID = "kill_scientistnpc_heavy_20", Difficulty = TaskDifficulty.Hard, EventType = "kill",
                        Conditions = new List<string> { "scientistnpc_heavy", "scientistnpc_bradley_heavy" },
                        TargetCount = 20
                    },
                    new MissionTask
                    {
                        TaskID = "cardswipe_red_2", Difficulty = TaskDifficulty.Hard, EventType = "cardswipe",
                        Conditions = new List<string> { "keycard_red" }, TargetCount = 2
                    },
                    new MissionTask
                    {
                        TaskID = "fishcatch_orangeroughy", Difficulty = TaskDifficulty.Hard, EventType = "fishcatch",
                        Conditions = new List<string> { "fish.orangeroughy" }, TargetCount = 1
                    },
                    new MissionTask
                    {
                        TaskID = "itemuse_pie_hunters", Difficulty = TaskDifficulty.Hard, EventType = "itemuse",
                        Conditions = new List<string> { "pie.hunters" }, TargetCount = 1
                    },
                    new MissionTask
                    {
                        TaskID = "itemuse_pie_bear", Difficulty = TaskDifficulty.Hard, EventType = "itemuse",
                        Conditions = new List<string> { "pie.bear" }, TargetCount = 1
                    },
                    new MissionTask
                    {
                        TaskID = "kill_tiger_10", Difficulty = TaskDifficulty.Hard, EventType = "kill",
                        Conditions = new List<string> { "tiger" }, TargetCount = 10
                    },
                    new MissionTask
                    {
                        TaskID = "bradleyapc", Difficulty = TaskDifficulty.Hard, EventType = "kill",
                        Conditions = new List<string> { "bradleyapc" }, TargetCount = 1
                    }
                };

                Interface.Oxide.DataFileSystem.WriteObject(missionsFile, defaultMissions);
                Puts(isEn
                    ? "[BattlePassReborn] The DefaultMissions.json mission file has been created."
                    : "[BattlePassReborn] Файл заданий DefaultMissions.json создан.");
            }

            var missions = Interface.Oxide.DataFileSystem.ReadObject<List<MissionTask>>(missionsFile);
            return missions;
        }

        #endregion
    }

    #endregion

    #region Rewards

    partial class BattlePassReborn
    {
        #region Types

        internal class LevelInfo
        {
            [JsonProperty(isEn ? "Level number" : "Номер уровня")]
            public int LevelID = 1;

            [JsonProperty(isEn
                ? "EXP required for this level (if 0 it is taken from the config)"
                : "Количество EXP для этого уровня (если 0, берется из конфига)")]
            public int ExpForLevel = 100;

            [JsonProperty(isEn
                ? "Reward display picture (if empty, takes 1 picture from the reward)"
                : "Картинка отображения награды(если пустое, берет 1 картинку из награды)")]
            public string ImageDefault = "";

            [JsonProperty(isEn
                ? "Reward display name (if empty, takes 1 name from the reward)"
                : "Отображаемое имя награды(если пустое, берет 1 имя из награды)")]
            public string generalDisplayName = "";

            [JsonProperty(isEn
                ? "PREMIUM reward display picture (if empty, takes 1 picture from the reward) (if available)"
                : "Картинка отображения ПРЕМИУМ награды(если пустое, берет 1 картинку из награды) (при наличии)")]
            public string ImagePremium = "";

            [JsonProperty(isEn
                ? "PREMIUM reward display name (if empty, takes 1 name from the reward) (if available)"
                : "Отображаемое имя ПРЕМИУМ награды(если пустое, берет 1 имя из награды) (при наличии)")]
            public string generalDisplayNamePremium = "";

            [JsonProperty(isEn ? "Reward list" : "Награды за уровень")]
            public List<LevelReward> RewardList = new List<LevelReward>();
        }

        internal class LevelReward
        {
            [JsonProperty("ShortName")] public string ShortName = "scrap";

            [JsonProperty(isEn ? "Amount" : "Количество")]
            public int Amount = 1;

            [JsonProperty(isEn ? "Premium reward?" : "Премиум награда?")]
            public bool IsPremium = false;

            [JsonProperty(isEn ? "Display Name" : "Отображаемое имя")]
            public string displayName = "";

            [JsonProperty(isEn ? "Picture of Item" : "Картинка предмета")]
            public string img = "";

            [JsonProperty("SkinID")] public ulong SkinID = 0;

            [JsonProperty(isEn
                ? "Commands to be executed (Use %STEAMID% to enter the player's steam ID)"
                : "Команды которые должны выполняться(Используйте %STEAMID% для ввода стимИД игрока)")]
            public List<string> CmdList = new List<string>();

            [JsonProperty("Is blueprint?")] public readonly bool isBlueprint = false;
        }

        #endregion

        private const string LevelsFolder = "BattlePassReborn";

        private List<LevelInfo> EnsureLevelInfoFileExists()
        {
            var levelsFile = $"{LevelsFolder}/LevelsList";

            if (!Interface.Oxide.DataFileSystem.ExistsDatafile(levelsFile))
            {
                var defaultLevels = new List<LevelInfo>
                {
                    new LevelInfo()
                    {
                        LevelID = 1,
                        ExpForLevel = 0,
                        ImageDefault = "",
                        generalDisplayName = "",
                        ImagePremium = "",
                        generalDisplayNamePremium = "",
                        RewardList = new List<LevelReward>
                        {
                            new LevelReward { ShortName = "scrap", Amount = 100, IsPremium = false },
                            new LevelReward { ShortName = "scrap", Amount = 500, IsPremium = true }
                        }
                    },
                    new LevelInfo()
                    {
                        LevelID = 2,
                        ExpForLevel = 0,
                        ImageDefault = "",
                        generalDisplayName = "",
                        ImagePremium = "",
                        generalDisplayNamePremium = "",
                        RewardList = new List<LevelReward>
                        {
                            new LevelReward { ShortName = "workbench1", Amount = 1, IsPremium = false }
                        }
                    },
                    new LevelInfo()
                    {
                        LevelID = 3,
                        ExpForLevel = 0,
                        ImageDefault = "",
                        generalDisplayName = "",
                        ImagePremium = "",
                        generalDisplayNamePremium = "",
                        RewardList = new List<LevelReward>
                        {
                            new LevelReward { ShortName = "crossbow", Amount = 1, IsPremium = false },
                            new LevelReward { ShortName = "pistol.nailgun", Amount = 1, IsPremium = true },
                            new LevelReward { ShortName = "ammo.nailgun.nails", Amount = 40, IsPremium = true }
                        }
                    },
                    new LevelInfo()
                    {
                        LevelID = 4,
                        ExpForLevel = 0,
                        ImageDefault = "",
                        generalDisplayName = "",
                        ImagePremium = "",
                        generalDisplayNamePremium = "",
                        RewardList = new List<LevelReward>
                        {
                            new LevelReward { ShortName = "stones", Amount = 2000, IsPremium = false }
                        }
                    },
                    new LevelInfo()
                    {
                        LevelID = 5,
                        ExpForLevel = 0,
                        ImageDefault = "",
                        generalDisplayName = "",
                        ImagePremium = "",
                        generalDisplayNamePremium = "",
                        RewardList = new List<LevelReward>
                        {
                            new LevelReward { ShortName = "metal.refined", Amount = 10, IsPremium = false }
                        }
                    },
                    new LevelInfo()
                    {
                        LevelID = 6,
                        ExpForLevel = 0,
                        ImageDefault = "",
                        generalDisplayName = "",
                        ImagePremium = "",
                        generalDisplayNamePremium = "",
                        RewardList = new List<LevelReward>
                        {
                            new LevelReward { ShortName = "can.tuna", Amount = 5, IsPremium = false },
                            new LevelReward { ShortName = "blueberries", Amount = 10, IsPremium = true }
                        }
                    },
                    new LevelInfo()
                    {
                        LevelID = 7,
                        ExpForLevel = 0,
                        ImageDefault = "",
                        generalDisplayName = "",
                        ImagePremium = "",
                        generalDisplayNamePremium = "",
                        RewardList = new List<LevelReward>
                        {
                            new LevelReward { ShortName = "hazmatsuit", Amount = 1, IsPremium = false }
                        }
                    },
                    new LevelInfo()
                    {
                        LevelID = 8,
                        ExpForLevel = 0,
                        ImageDefault = "",
                        generalDisplayName = "",
                        ImagePremium = "",
                        generalDisplayNamePremium = "",
                        RewardList = new List<LevelReward>
                        {
                            new LevelReward { ShortName = "pistol.revolver", Amount = 1, IsPremium = false }
                        }
                    },
                    new LevelInfo()
                    {
                        LevelID = 9,
                        ExpForLevel = 0,
                        ImageDefault = "",
                        generalDisplayName = "",
                        ImagePremium = "",
                        generalDisplayNamePremium = "",
                        RewardList = new List<LevelReward>
                        {
                            new LevelReward { ShortName = "lowgradefuel", Amount = 50, IsPremium = false },
                            new LevelReward { ShortName = "lowgradefuel", Amount = 100, IsPremium = true }
                        }
                    },
                    new LevelInfo()
                    {
                        LevelID = 10,
                        ExpForLevel = 0,
                        ImageDefault = "",
                        generalDisplayName = "",
                        ImagePremium = "",
                        generalDisplayNamePremium = "",
                        RewardList = new List<LevelReward>
                        {
                            new LevelReward { ShortName = "scrap", Amount = 500, IsPremium = true }
                        }
                    },
                    new LevelInfo()
                    {
                        LevelID = 11,
                        ExpForLevel = 0,
                        ImageDefault = "",
                        generalDisplayName = "",
                        ImagePremium = "",
                        generalDisplayNamePremium = "",
                        RewardList = new List<LevelReward>
                        {
                            new LevelReward { ShortName = "shotgun.double", Amount = 1, IsPremium = false }
                        }
                    },
                    new LevelInfo()
                    {
                        LevelID = 12,
                        ExpForLevel = 0,
                        ImageDefault = "",
                        generalDisplayName = "",
                        ImagePremium = "",
                        generalDisplayNamePremium = "",
                        RewardList = new List<LevelReward>
                        {
                            new LevelReward { ShortName = "wood", Amount = 5000, IsPremium = false }
                        }
                    },
                    new LevelInfo()
                    {
                        LevelID = 13,
                        ExpForLevel = 0,
                        ImageDefault = "",
                        generalDisplayName = "",
                        ImagePremium = "",
                        generalDisplayNamePremium = "",
                        RewardList = new List<LevelReward>
                        {
                            new LevelReward { ShortName = "gears", Amount = 10, IsPremium = false }
                        }
                    },
                    new LevelInfo()
                    {
                        LevelID = 14,
                        ExpForLevel = 0,
                        ImageDefault = "",
                        generalDisplayName = "",
                        ImagePremium = "",
                        generalDisplayNamePremium = "",
                        RewardList = new List<LevelReward>
                        {
                            new LevelReward { ShortName = "weapon.mod.silencer", Amount = 1, IsPremium = false },
                            new LevelReward { ShortName = "scrap", Amount = 1, IsPremium = false }
                        }
                    },
                    new LevelInfo()
                    {
                        LevelID = 15,
                        ExpForLevel = 0,
                        ImageDefault = "",
                        generalDisplayName = "",
                        ImagePremium = "",
                        generalDisplayNamePremium = "",
                        RewardList = new List<LevelReward>
                        {
                            new LevelReward { ShortName = "largebackpack", Amount = 1, IsPremium = false }
                        }
                    },
                    new LevelInfo()
                    {
                        LevelID = 16,
                        ExpForLevel = 0,
                        ImageDefault = "",
                        generalDisplayName = "",
                        ImagePremium = "",
                        generalDisplayNamePremium = "",
                        RewardList = new List<LevelReward>
                        {
                            new LevelReward { ShortName = "guntrap", Amount = 3, IsPremium = false }
                        }
                    },
                    new LevelInfo()
                    {
                        LevelID = 17,
                        ExpForLevel = 0,
                        ImageDefault = "",
                        generalDisplayName = "",
                        ImagePremium = "",
                        generalDisplayNamePremium = "",
                        RewardList = new List<LevelReward>
                        {
                            new LevelReward { ShortName = "ammo.pistol.fire", Amount = 128, IsPremium = false }
                        }
                    },
                    new LevelInfo()
                    {
                        LevelID = 18,
                        ExpForLevel = 0,
                        ImageDefault = "",
                        generalDisplayName = "",
                        ImagePremium = "",
                        generalDisplayNamePremium = "",
                        RewardList = new List<LevelReward>
                        {
                            new LevelReward { ShortName = "chainsaw", Amount = 1, IsPremium = false },
                            new LevelReward { ShortName = "jackhammer", Amount = 1, IsPremium = true }
                        }
                    },
                    new LevelInfo()
                    {
                        LevelID = 19,
                        ExpForLevel = 0,
                        ImageDefault = "",
                        generalDisplayName = "",
                        ImagePremium = "",
                        generalDisplayNamePremium = "",
                        RewardList = new List<LevelReward>
                        {
                            new LevelReward { ShortName = "pistol.semiauto", Amount = 1, IsPremium = false }
                        }
                    },
                    new LevelInfo()
                    {
                        LevelID = 20,
                        ExpForLevel = 0,
                        ImageDefault = "",
                        generalDisplayName = "",
                        ImagePremium = "",
                        generalDisplayNamePremium = "",
                        RewardList = new List<LevelReward>
                        {
                            new LevelReward { ShortName = "keycard_blue", Amount = 1, IsPremium = false },
                            new LevelReward { ShortName = "keycard_red", Amount = 1, IsPremium = true }
                        }
                    },
                    new LevelInfo()
                    {
                        LevelID = 21,
                        ExpForLevel = 0,
                        ImageDefault = "",
                        generalDisplayName = "",
                        ImagePremium = "",
                        generalDisplayNamePremium = "",
                        RewardList = new List<LevelReward>
                        {
                            new LevelReward { ShortName = "metal.ore", Amount = 2000, IsPremium = false },
                            new LevelReward { ShortName = "sulfur.ore", Amount = 900, IsPremium = true }
                        }
                    },
                    new LevelInfo()
                    {
                        LevelID = 22,
                        ExpForLevel = 0,
                        ImageDefault = "",
                        generalDisplayName = "",
                        ImagePremium = "",
                        generalDisplayNamePremium = "",
                        RewardList = new List<LevelReward>
                        {
                            new LevelReward { ShortName = "roadsigns", Amount = 10, IsPremium = false }
                        }
                    },
                    new LevelInfo()
                    {
                        LevelID = 23,
                        ExpForLevel = 0,
                        ImageDefault = "",
                        generalDisplayName = "",
                        ImagePremium = "",
                        generalDisplayNamePremium = "",
                        RewardList = new List<LevelReward>
                        {
                            new LevelReward { ShortName = "ammo.rifle", Amount = 128, IsPremium = false }
                        }
                    },
                    new LevelInfo()
                    {
                        LevelID = 24,
                        ExpForLevel = 0,
                        ImageDefault = "",
                        generalDisplayName = "",
                        ImagePremium = "",
                        generalDisplayNamePremium = "",
                        RewardList = new List<LevelReward>
                        {
                            new LevelReward { ShortName = "supply.signal", Amount = 1, IsPremium = false }
                        }
                    },
                    new LevelInfo()
                    {
                        LevelID = 25,
                        ExpForLevel = 0,
                        ImageDefault = "",
                        generalDisplayName = "",
                        ImagePremium = "",
                        generalDisplayNamePremium = "",
                        RewardList = new List<LevelReward>
                        {
                            new LevelReward
                                { ShortName = "electric.battery.rechargable.large", Amount = 1, IsPremium = false },
                            new LevelReward { ShortName = "autoturret", Amount = 1, IsPremium = true }
                        }
                    },
                    new LevelInfo()
                    {
                        LevelID = 26,
                        ExpForLevel = 0,
                        ImageDefault = "",
                        generalDisplayName = "",
                        ImagePremium = "",
                        generalDisplayNamePremium = "",
                        RewardList = new List<LevelReward>
                        {
                            new LevelReward { ShortName = "heavy.plate.helmet", Amount = 1, IsPremium = false }
                        }
                    },
                    new LevelInfo()
                    {
                        LevelID = 27,
                        ExpForLevel = 0,
                        ImageDefault = "",
                        generalDisplayName = "",
                        ImagePremium = "",
                        generalDisplayNamePremium = "",
                        RewardList = new List<LevelReward>
                        {
                            new LevelReward { ShortName = "pistol.prototype17", Amount = 1, IsPremium = false }
                        }
                    },
                    new LevelInfo()
                    {
                        LevelID = 28,
                        ExpForLevel = 0,
                        ImageDefault = "",
                        generalDisplayName = "",
                        ImagePremium = "",
                        generalDisplayNamePremium = "",
                        RewardList = new List<LevelReward>
                        {
                            new LevelReward { ShortName = "rifle.l96", Amount = 1, IsPremium = true }
                        }
                    },
                    new LevelInfo()
                    {
                        LevelID = 29,
                        ExpForLevel = 0,
                        ImageDefault = "",
                        generalDisplayName = "",
                        ImagePremium = "",
                        generalDisplayNamePremium = "",
                        RewardList = new List<LevelReward>
                        {
                            new LevelReward { ShortName = "workbench3", Amount = 1, IsPremium = false }
                        }
                    },
                    new LevelInfo()
                    {
                        LevelID = 30,
                        ExpForLevel = 0,
                        ImageDefault = "",
                        generalDisplayName = "",
                        ImagePremium = "",
                        generalDisplayNamePremium = "",
                        RewardList = new List<LevelReward>
                        {
                            new LevelReward { ShortName = "scrap", Amount = 1000, IsPremium = true },
                            new LevelReward { ShortName = "supply.signal", Amount = 10, IsPremium = false }
                        }
                    }
                };

                Interface.Oxide.DataFileSystem.WriteObject(levelsFile, defaultLevels);
                Puts(isEn
                    ? "[BattlePassReborn] The LevelsList.json task file has been created."
                    : "[BattlePassReborn] Файл заданий LevelsList.json создан.");
            }

            var levels = Interface.Oxide.DataFileSystem.ReadObject<List<LevelInfo>>(levelsFile);
            return levels;
        }
    }

    #endregion

    #region Storage

    partial class BattlePassReborn
    {
        #region Types

        internal class Storage
        {
            [JsonProperty(isEn ? "Reward in storage" : "Награды в хранилище")]
            public List<LevelRewardStorage> RewardList = new List<LevelRewardStorage>();
        }

        internal class LevelRewardStorage
        {
            [JsonProperty("ShortName")] public string ShortName = "scrap";

            [JsonProperty(isEn ? "Amount" : "Количество")]
            public int Amount = 1;

            [JsonProperty(isEn ? "Premium reward?" : "Премиум награда?")]
            public bool IsPremium = false;

            [JsonProperty(isEn ? "Display Name" : "Отображаемое имя")]
            public string displayName = "";

            [JsonProperty(isEn ? "Picture of Item" : "Картинка предмета")]
            public string img = "";

            [JsonProperty("SkinID")] public ulong SkinID = 0;

            [JsonProperty(isEn
                ? "Commands to be executed (Use %STEAMID% to enter the player's steam ID)"
                : "Команды которые должны выполняться(Используйте %STEAMID% для ввода стимИД игрока)")]
            public List<string> CmdList = new List<string>();

            [JsonProperty("Is blueprint?")] public bool isBlueprint = false;

            [JsonProperty("DateTime in Storage")] public DateTime DateInStorage;
        }

        #endregion

        private const string StorageFolder = "BattlePassReborn/Storages";

        private Storage LoadStoragePlayerData(ulong userId, string playerName = "")
        {
            if (_dataStorages.TryGetValue(userId, out var cachedData))
            {
                if (_config.StorageS.StorageItemLifetimeDays != 0)
                    foreach (var item in cachedData.RewardList.ToList())
                    {
                        if (item.DateInStorage.AddDays(_config.StorageS.StorageItemLifetimeDays) < DateTime.Today)
                        {
                            cachedData.RewardList.Remove(item);
                        }
                    }

                return cachedData;
            }

            var fileName = $"{StorageFolder}/{userId}";
            var data = new Storage();

            if (!Interface.Oxide.DataFileSystem.ExistsDatafile(fileName))
            {
                SaveStoragePlayerData(userId, data);
            }
            else
            {
                data = Interface.Oxide.DataFileSystem.ReadObject<Storage>(fileName);
                if (_config.StorageS.StorageItemLifetimeDays != 0)
                    foreach (var item in data.RewardList.ToList())
                    {
                        if (item.DateInStorage.AddDays(_config.StorageS.StorageItemLifetimeDays) < DateTime.Today)
                        {
                            data.RewardList.Remove(item);
                        }
                    }
            }

            _dataStorages[userId] = data;

            return data;
        }

        private void SaveStoragePlayerData(ulong userId, Storage data)
        {
            var fileName = $"{StorageFolder}/{userId}";
            Interface.Oxide.DataFileSystem.WriteObject(fileName, data);
        }

        private void GiveItemToPlayer(BasePlayer player, LevelRewardStorage check)
        {
            var item = ItemManager.CreateByName(check.ShortName, check.Amount, check.SkinID);
            if (item != null)
            {
                if (check.isBlueprint)
                {
                    item = ItemManager.CreateByItemID(-996920608);
                    var info = ItemManager.FindItemDefinition(check.ShortName);
                    item.blueprintTarget = info.itemid;
                }

                if (!string.IsNullOrEmpty(check.displayName))
                    item.name = check.displayName;

                player.GiveItem(item);
            }

            foreach (var cmd in check.CmdList)
                rust.RunServerCommand(cmd.Replace("%STEAMID%", player.UserIDString));
        }

        private void ClearStorageFolder()
        {
            try
            {
                var storagePath = Path.Combine(Interface.Oxide.DataFileSystem.Directory, StorageFolder);
                if (!Directory.Exists(storagePath))
                    return;

                var files = Directory.GetFiles(storagePath, "*.json");
                foreach (var file in files)
                {
                    File.Delete(file);
                    Puts($"Deleted storage file: {Path.GetFileName(file)}");
                }

                _dataStorages.Clear();
            }
            catch (Exception ex)
            {
                PrintError($"Failed to clear storage folder: {ex.Message}");
            }
        }

        private void GiveAllUnclaimedRewardsToAllPlayers()
        {
            foreach (var kvp in _dataPlayers.ToList())
            {
                var userId = kvp.Key;
                var playerData = kvp.Value;

                var eligibleLevels = _allLevels.Where(l => l.LevelID <= playerData.Level).ToList();

                if (!eligibleLevels.Any()) continue;

                var storageData = LoadStoragePlayerData(userId);
                var isPremium =
                    permission.UserHasPermission(userId.ToString(), _config.General.PREMIUMRewardPermission);

                foreach (var level in eligibleLevels)
                {
                    var claimed = playerData.RewardsReceived.FirstOrDefault(r => r.Level == level.LevelID);
                    if (claimed == null)
                    {
                        claimed = new Rewards { Level = level.LevelID, DefaultReward = false, PremiumReward = false };
                        playerData.RewardsReceived.Add(claimed);
                    }

                    if (!claimed.DefaultReward)
                    {
                        var defaultRewards = level.RewardList.Where(r => !r.IsPremium);
                        if (defaultRewards.Any())
                        {
                            claimed.DefaultReward = true;
                            foreach (var reward in defaultRewards)
                            {
                                storageData.RewardList.Add(CloneRewardToStorage(reward));
                            }
                        }
                    }

                    if (isPremium && !claimed.PremiumReward)
                    {
                        var premiumRewards = level.RewardList.Where(r => r.IsPremium);
                        if (premiumRewards.Any())
                        {
                            claimed.PremiumReward = true;
                            foreach (var reward in premiumRewards)
                            {
                                storageData.RewardList.Add(CloneRewardToStorage(reward));
                            }
                        }
                    }
                }

                _dataPlayers[userId] = playerData;
                _dataStorages[userId] = storageData;
            }
        }

        private LevelRewardStorage CloneRewardToStorage(LevelReward reward)
        {
            return new LevelRewardStorage
            {
                ShortName = reward.ShortName,
                Amount = reward.Amount,
                IsPremium = reward.IsPremium,
                displayName = reward.displayName,
                img = reward.img,
                SkinID = reward.SkinID,
                CmdList = reward.CmdList?.ToList() ?? new List<string>(),
                isBlueprint = reward.isBlueprint,
                DateInStorage = DateTime.Today
            };
        }
    }

    #endregion

    #region Discord

    partial class BattlePassReborn
    {
        private void Request(String url, String payload, Action<Int32> callback = null)
        {
            Dictionary<String, String> header = new Dictionary<String, String>();
            header.Add("Content-Type", "application/json");
            webrequest.Enqueue(url, payload, (code, response) =>
            {
                if (code != 200 && code != 204)
                {
                    if (response != null)
                    {
                        try
                        {
                            JObject json = JObject.Parse(response);
                            if (code == 429)
                            {
                                Single seconds = Single.Parse(Math.Ceiling((Double)(Int32)json["retry_after"] / 1000)
                                    .ToString());
                            }
                            else
                            {
                                PrintWarning(
                                    $" Discord rejected that payload! Responded with \"{json["message"].ToString()}\" Code: {code}");
                            }
                        }
                        catch
                        {
                            PrintWarning(
                                $"Failed to get a valid response from discord! Error: \"{response}\" Code: {code}");
                        }
                    }
                    else
                    {
                        PrintWarning($"Discord didn't respond (down?) Code: {code}");
                    }
                }

                try
                {
                    callback?.Invoke(code);
                }
                catch (Exception ex)
                {
                }
            }, this, RequestMethod.POST, header);
        }

        private void SendDiscord(List<Fields> fields)
        {
            if (_config.Discord.WebhookUrl == null ||
                String.IsNullOrWhiteSpace(_config.Discord.WebhookUrl)) return;
            FancyMessage newMessage = new FancyMessage(null, false,
                new FancyMessage.Embeds[1]
                    { new FancyMessage.Embeds(null, 10710525, fields, new Authors(null, null, null, null), null) });

            Request($"{_config.Discord.WebhookUrl}", newMessage.toJSON());
        }

        private void SendDiscordSimple(string text)
        {
            if (_config.Discord.WebhookUrl == null || string.IsNullOrWhiteSpace(_config.Discord.WebhookUrl)) return;

            var payload = JsonConvert.SerializeObject(new
            {
                content = text,
                tts = false
            });

            Request($"{_config.Discord.WebhookUrl}", payload);
        }

        public class FancyMessage
        {
            public String content { get; set; }
            public Boolean tts { get; set; }
            public Embeds[] embeds { get; set; }

            public class Embeds
            {
                public String title { get; set; }
                public Int32 color { get; set; }
                public List<Fields> fields { get; set; }
                public Footer footer { get; set; }
                public Authors author { get; set; }

                public Embeds(String title, Int32 color, List<Fields> fields, Authors author, Footer footer)
                {
                    this.title = title;
                    this.color = color;
                    this.fields = fields;
                    this.author = author;
                    this.footer = footer;
                }
            }

            public FancyMessage(String content, bool tts, Embeds[] embeds)
            {
                this.content = content;
                this.tts = tts;
                this.embeds = embeds;
            }

            public String toJSON() => JsonConvert.SerializeObject(this);
        }


        public class Authors
        {
            public String name { get; set; }
            public String url { get; set; }
            public String icon_url { get; set; }
            public String proxy_icon_url { get; set; }

            public Authors(String name, String url, String icon_url, String proxy_icon_url)
            {
                this.name = name;
                this.url = url;
                this.icon_url = icon_url;
                this.proxy_icon_url = proxy_icon_url;
            }
        }

        public class Fields
        {
            public String name { get; set; }
            public String value { get; set; }
            public bool inline { get; set; }

            public Fields(String name, String value, bool inline)
            {
                this.name = name;
                this.value = value;
                this.inline = inline;
            }
        }

        public class Footer
        {
            public String text { get; set; }
            public String icon_url { get; set; }
            public String proxy_icon_url { get; set; }

            public Footer(String text, String icon_url, String proxy_icon_url)
            {
                this.text = text;
                this.icon_url = icon_url;
                this.proxy_icon_url = proxy_icon_url;
            }
        }
    }

    #endregion

    #region Admin Commands

    partial class BattlePassReborn
    {
        [ChatCommand("resetprogress")]
        private void ResetProgress(BasePlayer player, string command, string[] args)
        {
            if (!player.IsAdmin)
                return;

            if (args.Length < 1)
            {
                player.ChatMessage("Usage: /resetprogess <steamid>");
                return;
            }

            string steamid = args[0];

            var targetPlayer = BasePlayer.Find(steamid);
            if (targetPlayer == null)
            {
                player.ChatMessage($"The player with the steamid '{steamid}' was not found");
                return;
            }

            var data = LoadPlayerData(targetPlayer.userID);

            var isPremium = permission.UserHasPermission(targetPlayer.UserIDString, _config.General.PREMIUMRewardPermission);

            data.Exp = 0;
            data.Level = 0;
            data.Tasks = GenerateTasks();
            data.Day = DateTime.UtcNow.Day;
            data.Premium = isPremium;
            data.CountRefresh = _config.Tasks.RefreshTask
                ? (isPremium ? _config.Tasks.TaskRefreshPremiumCount : _config.Tasks.TaskRefreshCount)
                : 0;
            data.CountCompletedTask = 0;

            RefreshProgressBarUI(targetPlayer, data);
        }

        [ChatCommand("resetprogressall")]
        private void ResetProgressAll(BasePlayer player, string command, string[] args)
        {
            if (!player.IsAdmin)
                return;

            foreach(var data in _dataPlayers)
            {
                var isPremium = permission.UserHasPermission(data.Key.ToString(), _config.General.PREMIUMRewardPermission);
                data.Value.Exp = 0;
                data.Value.Level = 0;
                data.Value.Tasks = GenerateTasks();
                data.Value.Day = DateTime.UtcNow.Day;
                data.Value.Premium = isPremium;
                data.Value.CountRefresh = _config.Tasks.RefreshTask
                    ? (isPremium ? _config.Tasks.TaskRefreshPremiumCount : _config.Tasks.TaskRefreshCount)
                    : 0;
                data.Value.CountCompletedTask = 0;

                var targetPlayer = BasePlayer.Find(data.Key.ToString());
                RefreshProgressBarUI(targetPlayer, data.Value);
            }
        }

        [ChatCommand("removeexp")]
        private void RemoveExp(BasePlayer player, string command, string[] args)
        {
            if (!player.IsAdmin)
                return;

            if (args.Length < 2)
            {
                player.ChatMessage("Usage: /removeexp <steamid> <quantity>");
                return;
            }

            string steamid = args[0];
            if (!int.TryParse(args[1], out int amount) || amount <= 0)
            {
                player.ChatMessage("Specify the correct amount of EXP (a positive integer).");
                return;
            }

            var targetPlayer = BasePlayer.Find(steamid);
            if (targetPlayer == null)
            {
                player.ChatMessage($"The player with the steamid '{steamid}' was not found");
                return;
            }

            var data = LoadPlayerData(targetPlayer.userID);

            data.Exp -= amount;
            data.Exp = data.Exp < 0 ? 0 : data.Exp;

            RefreshProgressBarUI(targetPlayer, data);
        }

        [ChatCommand("giveexp")]
        private void GiveExp(BasePlayer player, string command, string[] args)
        {
            if (!player.IsAdmin)
                return;

            if (args.Length < 2)
            {
                player.ChatMessage("Usage: /giveexp <steamid> <quantity>");
                return;
            }

            string steamid = args[0];
            if (!int.TryParse(args[1], out int amount) || amount <= 0)
            {
                player.ChatMessage("Specify the correct amount of EXP (a positive integer).");
                return;
            }

            var targetPlayer = BasePlayer.Find(steamid);
            if (targetPlayer == null)
            {
                player.ChatMessage($"The player with the steamid '{steamid}' was not found");
                return;
            }

            var data = LoadPlayerData(targetPlayer.userID);

            GiveExp(data, amount, targetPlayer);
            if(_config.IntegrationST.isEnableIntegration && (_config.IntegrationST.Mode == SkillTreeIntegrationMode.BpToSk || _config.IntegrationST.Mode == SkillTreeIntegrationMode.Full))
                Interface.CallHook("AwardXP", player, (double)amount, "BattlePassReborn", false, true, "BattlePassReborn");
        }

        [ChatCommand("refreshtask")]
        private void RefreshTaskCmd(BasePlayer player, string command, string[] args)
        {
            if (!player.IsAdmin)
                return;

            if (args.Length == 0)
            {
                var data = LoadPlayerData(player.userID);
                data.Tasks = GenerateTasks();
                player.ChatMessage("Tasks updated");
                return;
            }
            else
            {
                string steamid = args[0];
                var targetPlayer = BasePlayer.Find(steamid);
                if (targetPlayer == null)
                {
                    player.ChatMessage($"The player with the steamid '{steamid}' was not found");
                    return;
                }

                var data = LoadPlayerData(targetPlayer.userID);
                data.Tasks = GenerateTasks();
                player.ChatMessage($"Tasks updated for {targetPlayer.userID}");
                targetPlayer.ChatMessage("Tasks updated");
            }
            
        }

        [ConsoleCommand("refreshtask")]
        private void cmdConsoleRefreshTask(ConsoleSystem.Arg arg)
        {
            if (arg.Player() != null)
                return;

            if (!arg.HasArgs(1))
            {
                PrintWarning("Incorrect steamid!");
                return;
            }
            // CHANGE: Added ToString() to handle StringView -> string conversion after Rust update
            if (ulong.TryParse(arg.Args[0].ToString(), out ulong steamId))
            {
                var data = LoadPlayerData(steamId);
                data.Tasks = GenerateTasks(); 
            }
        }
    }

    #endregion

    #region Localization

    partial class BattlePassReborn
    {
        protected override void LoadDefaultMessages()
        {
            Dictionary<string, string> DefaultMessages = new Dictionary<string, string>()
            {
                ["TEXT_CLOSE"] = "CLOSE",
                ["TEXT_BP"] = "BATTLE PASS",
                ["TEXT_PP"] = "PREMIUM PASS",
                ["TEXT_DESCR"] =
                    "It's a reward system where each level brings you closer to valuable loot. Complete tasks, earn points, and unlock exclusive skins, items, and bonuses.",
                ["TEXT_PREMIUMDESCR"] =
                    "Get more - unlock the Premium Pass! More challenges. More rewards. \nOnly for the best.",
                ["TEXT_BUYPASS"] = "BUY PREMIUM PASS",
                ["TEXT_DAILYCHALL"] = "DAILY CHALLENGES",
                ["TEXT_DAILYREROLLS"] = "Daily Rerolls: {0} / {1}",
                ["TEXT_TIER"] = "TIER {0}",
                ["TEXT_XP"] = "XP",
                ["TEXT_PREMIUM"] = "PREMIUM",
                ["TEXT_HINTS1"] = "The Premium Pass increases the number of points received for completing a tasks",
                ["TEXT_HINTS2"] = "Tap the reward card to claim",
                ["TEXT_HINTS3"] = "Reward can be replaced 3 times per day",
                ["TEXT_HINTS4"] = "Unclaimed rewards are sent to storage at season’s end",
                ["TEXT_UPGRADE"] = "Upgrade to Premium Pass",
                ["TEXT_PFEATURES"] = "Premium Features",
                ["TEXT_HGETPREMIUM"] = "How to Get Premium",
                ["TEXT_GQUEST"] = "Got questions?",
                ["TEXT_UNLOCKBPEXPERIENCE"] = "Unlock the full Battle Pass experience!",
                ["TEXT_COPYURL"] = "Copy this URL and open it in your browser",
                ["TEXT_JOINDS"] = "Join our Discord and ask your question there",
                ["TEXT_PURCHASEPP"] =
                    "There you can purchase the Premium Pass.\nIt will be delivered to your account automatically after payment.",
                ["TEXT_VISITWEBSITE"] = "Visit our website",
                ["TEXT_PRIVELEGES"] =
                    "• Access exclusive Premium-only rewards\n• Earn more missions and progress faster\n• Get bonus XP for each completed task\n• Retrieve missed rewards from the Storage window",
                ["TEXT_STORAGE"] = "STORAGE",
                ["TEXT_STORAGEDESCR"] =
                    "Your missed Battle Pass rewards are stored here. Claim them before they expire!",
                ["TEXT_ITEMCLAIMED"] = "ITEM CLAIMED!",
                ["TEXT_REWARDCLAIMED"] = "REWARD CLAIMED!",
                ["TEXT_TASKCOMPLETED"] = "Task '{0}' completed! +{1} EXP",
                ["TEXT_GAINEDLVL"] = "You have gained a new level {0}.",
                ["TEXT_SPENTREFR"] = "You've spent all the refreshes!",
                ["TEXT_TASK_BLOCK"] = "You have reached the limit of completed tasks for today!",
                ["TEXT_GAINEDLVLDISCORD"] = "The player {0} ({1}) got a new level {2}.",
                ["TEXT_REWARDCLAIMEDDISCORD"] = "The player {0} ({1}) reward claim from {2} LVL {3}.",
                ["extract_stone_2000"] = "Gather 2000 Stone",
                ["kill_players_10"] = "Kill 10 Players",
                ["extract_metal.ore_1000"] = "Gather 1000 Metal Ore",
                ["extract_sulfur_900"] = "Gather 900 Sulfur Ore",
                ["craft_crossbow"] = "Craft a Crossbow",
                ["craft_nailgun"] = "Craft a Nailgun",
                ["craft_revolver"] = "Craft a Revolver",
                ["craft_double"] = "Craft a Double Barrel Shotgun",
                ["craft_furnace"] = "Craft a Furnace",
                ["craft_bed"] = "Craft a Bed",
                ["kill_players_3"] = "Kill 3 Players",
                ["kill_scientist_3"] = "Kill 3 Scientists",
                ["craft_shelter"] = "Craft a Wooden Shelter",
                ["fishcatch_smallshark_1"] = "Catch 1 Small Shark",
                ["extract_salmon_1"] = "Catch 1 Salmon",
                ["kill_chicken_3"] = "Kill 3 Chickens",
                ["kill_stag_3"] = "Kill 3 Stag",
                ["pickup_mushroom_10"] = "Collect 10 Mushrooms",
                ["loot_crate_normal_2"] = "Loot 3 Normal Crate",
                ["kill_boar_3"] = "Kill 3 Boar",
                ["extract_stone_4000"] = "Gather 4000 Stone",
                ["extract_metal.ore_2000"] = "Gather 2000 Metal Ore",
                ["extract_sulfur_2000"] = "Gather 2000 Sulfur Ore",
                ["craft_rocket_hv"] = "Craft a 3 HV Rocket",
                ["craft_pump"] = "Craft a Pump Shotgun",
                ["craft_icepick"] = "Craft a Salvaged Icepick",
                ["craft_semiauto"] = "Craft a Semi-Automatic Rifle",
                ["craft_satchel"] = "Craft a 4 Satchel Charge",
                ["craft_smg"] = "Craft a Custom SMG",
                ["craft_electric_furnace"] = "Craft a 3 Electric furnace",
                ["fishcatch_catfish_10"] = "Catch 10 Catfish",
                ["extract_troutsmall_10"] = "Catch 10 Small Trout",
                ["kill_wolf_3"] = "Kill 3 Wolf",
                ["kill_bear_3"] = "Kill 3 Bear",
                ["kill_crocodile_1"] = "Kill 1 Crocodile",
                ["loot_crate_normal"] = "Loot 3 Military Crate",
                ["loot_codelockedhackablecrate_1"] = "Loot Locked Crate",
                ["kill_players_20"] = "Kill 20 Players",
                ["loot_codelockedhackablecrate_hard"] = "Loot 3 Locked Crate",
                ["kill_scientistnpc_heavy_10"] = "Kill 10 heavy Scientists",
                ["kill_barrel_40"] = "Break 40 Barrels",
                ["cardswipe_blue_2"] = "Use a Blue Keycard 2 times",
                ["extract_sulfur_6000"] = "Gather 6000 Sulfur Ore",
                ["craft_explosive_timed"] = "Craft a ",
                ["craft_ak"] = "Craft a Assault Rifle",
                ["craft_bolt"] = "Craft a Bolt Action Rifle",
                ["craft_ammo_explosive"] = "Craft a 128 Explosive 5.56 Rifle Ammo",
                ["craft_door_toptier"] = "Craft a Armored Double Door",
                ["kill_players_50"] = "Kill 50 players",
                ["cardswipe_red_2"] = "Use a Red Keycard 2 times",
                ["fishcatch_orangeroughy"] = "Catch Orange Roughy",
                ["itemuse_pie_hunters"] = "Eat Hunter pie",
                ["itemuse_pie_bear"] = "Eat Bear pie",
                ["kill_tiger_10"] = "Kill 10 Tigers",
                ["bradleyapc"] = "Destroy a Bradley APC",
                ["kill_scientistnpc_heavy_20"] = "Kill 20 Heavy Scientists"
            };
            _UISettings = EnsureUIFileExists();
            _allMissions = EnsureMissionsFileExists();
            foreach (var mission in _allMissions)
            {
                DefaultMessages.TryAdd(mission.TaskID, "");
            }

            foreach (var hints in _UISettings.Hints)
            {
                DefaultMessages.TryAdd(hints, "");
            }

            lang.RegisterMessages(DefaultMessages, this);
        }

        private string GetLang(string key, string userId, params object[] args)
        {
            var message = lang.GetMessage(key, this, userId);
            if (message == key)
            {
                message = lang.GetMessageByLanguage(key, this, "en");
                return args.Length > 0 ? string.Format(message, args) : message;
            }
            else
                return args.Length > 0 ? string.Format(message, args) : message;
        }
    }

    #endregion

    #region Server-Panel

    partial class BattlePassReborn
    {
        [PluginReference] private Plugin ServerPanel;
        private int _serverPanelCategoryID = -1;

        private void OnReceiveCategoryInfo(int categoryID)
        {
            _serverPanelCategoryID = categoryID;
        }

        private void OnServerPanelClosed(BasePlayer player)
        {
            CuiHelper.DestroyUi(player, "UI.Server.Panel.Content.Plugin");
            CuiHelper.DestroyUi(player, "UI.Server.Panel.BattlePassReborn.Footer");
        }

        private void OnServerPanelCategoryPage(BasePlayer player, int category, int page)
        {
            if (category != _serverPanelCategoryID)
            {
                CuiHelper.DestroyUi(player, "UI.Server.Panel.Content.Plugin");
                CuiHelper.DestroyUi(player, "UI.Server.Panel.BattlePassReborn.Footer");
            }
        }

        private CuiElementContainer API_OpenPlugin(BasePlayer player)
        {
            var container = new CuiElementContainer();

            container.Add(new CuiPanel
            {
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" },
                Image = { Color = "0 0 0 0" }
            }, "UI.Server.Panel.Content", "UI.Server.Panel.Content.Plugin");

            if (_config.General.AvailableBPPermission &&
                !permission.UserHasPermission(player.UserIDString, _config.General.BPPermission))
                return container;

            var isPremium = permission.UserHasPermission(player.UserIDString, _config.General.PREMIUMRewardPermission);
            var data = LoadPlayerData(player.userID);
            var settings = _allLevels.FirstOrDefault(p => p.LevelID == data.Level + 1);
            if (settings == null)
            {
                settings = _allLevels.LastOrDefault();
            }

            var expPerLevel = settings.ExpForLevel > 0 ? settings.ExpForLevel : _config.General.ExpPerLevel;

            BattlePassRebornUI.Builder.Root
                root = new BattlePassRebornUI.Builder.Root("UI.Server.Panel.Content.Plugin");
            {
                BattlePassRebornUI.Builder.Element suCepD = root.AddContainer(
                    anchorMin: "0 0",
                    anchorMax: "1 1",
                    offsetMin: "0 0",
                    offsetMax: "0 0",
                    name: "Main");
                if (!string.IsNullOrEmpty(_UISettings.BackGroundURL))
                {
                    BattlePassRebornUI.Builder.Element BRBagG = suCepD.AddImage(
                        content: GetImage("BG_BRBagG"),
                        material: "",
                        anchorMin: "0 0",
                        anchorMax: "1 1",
                        offsetMin: "0 0",
                        offsetMax: "0 0",
                        name: "BG");
                }

                BattlePassRebornUI.Builder.Element KpZTtS = suCepD.AddImage(
                    content: GetImage("img_shadow_KpZTtS"),
                    material: "",
                    anchorMin: "0 1",
                    anchorMax: "0 1",
                    offsetMin: "0 -241",
                    offsetMax: "440 -87",
                    name: "img_shadow");
                {
                    BattlePassRebornUI.Builder.Element EqwEyw = suCepD.AddContainer(
                        anchorMin: "0.5 0.5",
                        anchorMax: "0.5 0.5",
                        offsetMin: "-597 -268",
                        offsetMax: "597 268",
                        name: "content");
                    BattlePassRebornUI.Builder.Element UNOaIT = EqwEyw.AddText(
                        text: GetLang("TEXT_BP", player.UserIDString),
                        font: BattlePassRebornUI.Builder.Font.RobotoCondensedBold,
                        fontSize: 40,
                        align: TextAnchor.MiddleLeft,
                        overflow: VerticalWrapMode.Overflow,
                        anchorMin: "0 1",
                        anchorMax: "0 1",
                        offsetMin: "0 -47",
                        offsetMax: "357 0",
                        name: "Title");
                    BattlePassRebornUI.Builder.Element gEhUKg = EqwEyw.AddText(
                        text: GetLang("TEXT_DESCR", player.UserIDString),
                        font: BattlePassRebornUI.Builder.Font.RobotoCondensedBold,
                        fontSize: 16,
                        align: TextAnchor.MiddleLeft,
                        overflow: VerticalWrapMode.Overflow,
                        anchorMin: "0 1",
                        anchorMax: "0 1",
                        offsetMin: "0 -114",
                        offsetMax: "357 -57",
                        name: "description");
                    {
                        BattlePassRebornUI.Builder.Element HgMZGJ = EqwEyw.AddContainer(
                            anchorMin: "0 1",
                            anchorMax: "0 1",
                            offsetMin: "0 -300",
                            offsetMax: "357 -144",
                            name: "premium_baner");
                        BattlePassRebornUI.Builder.Element jpGRHv = HgMZGJ.AddImage(
                            content: GetImage("Image_jpGRHv"),
                            material: "",
                            anchorMin: "0 0",
                            anchorMax: "1 1",
                            offsetMin: "0 0",
                            offsetMax: "0 0",
                            name: "Image");
                        BattlePassRebornUI.Builder.Element zMbNCU = HgMZGJ.AddText(
                            text: GetLang("TEXT_PP", player.UserIDString),
                            color: "0.6156863 0.8117647 0.2705882 1",
                            font: BattlePassRebornUI.Builder.Font.RobotoCondensedBold,
                            fontSize: 20,
                            align: TextAnchor.MiddleLeft,
                            overflow: VerticalWrapMode.Overflow,
                            anchorMin: "0 1",
                            anchorMax: "0 1",
                            offsetMin: "20 -43",
                            offsetMax: "225 -20",
                            name: "premium_title");
                        BattlePassRebornUI.Builder.Element EfkgJN = HgMZGJ.AddText(
                            text: GetLang("TEXT_PREMIUMDESCR", player.UserIDString),
                            font: BattlePassRebornUI.Builder.Font.RobotoCondensedBold,
                            fontSize: 12,
                            align: TextAnchor.UpperLeft,
                            overflow: VerticalWrapMode.Overflow,
                            anchorMin: "0 1",
                            anchorMax: "0 1",
                            offsetMin: "20 -144",
                            offsetMax: "225 -51",
                            name: "premium_description");
                        {
                            BattlePassRebornUI.Builder.Element diXquO = HgMZGJ.AddButton(
                                command: "UI_PremiumPassBaner",
                                color: _UISettings.PremBanner.ColorPanel,
                                sprite: "",
                                material: "",
                                imageType: UnityEngine.UI.Image.Type.Simple,
                                anchorMin: "0 1",
                                anchorMax: "0 1",
                                offsetMin: _UISettings.PremBanner.offsetMin,
                                offsetMax: _UISettings.PremBanner.offsetMax,
                                name: "premium_btn");
                            BattlePassRebornUI.Builder.Element svSTIf = diXquO.AddText(
                                text: GetLang("TEXT_BUYPASS", player.UserIDString),
                                color: _UISettings.PremBanner.ColorText,
                                font: BattlePassRebornUI.Builder.Font.RobotoCondensedBold,
                                fontSize: 10,
                                align: TextAnchor.MiddleCenter,
                                overflow: VerticalWrapMode.Truncate,
                                anchorMin: "0.5 0.5",
                                anchorMax: "0.5 0.5",
                                offsetMin: "-60 -13",
                                offsetMax: "60 13",
                                name: "Text");
                        }
                    }
                    {
                        BattlePassRebornUI.Builder.Element xYuKDj = EqwEyw.AddContainer(
                            anchorMin: "0 1",
                            anchorMax: "0 1",
                            offsetMin: "397 -300",
                            offsetMax: "1194 0",
                            name: "challenges");
                        BattlePassRebornUI.Builder.Element qqKgoS = xYuKDj.AddPanel(
                            sprite: "",
                            material: "",
                            color: "0 0 0 0.7014",
                            imageType: UnityEngine.UI.Image.Type.Simple,
                            anchorMin: "0 0",
                            anchorMax: "1 1",
                            offsetMin: "0 0",
                            offsetMax: "0 0",
                            name: "challenges_bg");
                        BattlePassRebornUI.Builder.Element xYuKTT = xYuKDj.AddContainer(
                            anchorMin: "0 0",
                            anchorMax: "1 1",
                            offsetMin: "0 0",
                            offsetMax: "0 0",
                            name: "challenges_text");
                        BattlePassRebornUI.Builder.Element fapmNb = xYuKDj.AddText(
                            text: GetLang("TEXT_DAILYCHALL", player.UserIDString),
                            font: BattlePassRebornUI.Builder.Font.RobotoCondensedBold,
                            fontSize: 20,
                            align: TextAnchor.UpperLeft,
                            overflow: VerticalWrapMode.Overflow,
                            anchorMin: "0 1",
                            anchorMax: "0 1",
                            offsetMin: "12 -47",
                            offsetMax: "520 -17",
                            name: "challenges_title");

                        if (_config.Tasks.RefreshTask)
                        {
                            var countRefreshes = isPremium
                                ? _config.Tasks.TaskRefreshPremiumCount
                                : _config.Tasks.TaskRefreshCount;
                            BattlePassRebornUI.Builder.Element oUbrBa = xYuKTT.AddText(
                                text: GetLang("TEXT_DAILYREROLLS", player.UserIDString, data.CountRefresh,
                                    countRefreshes),
                                font: BattlePassRebornUI.Builder.Font.RobotoCondensedBold,
                                fontSize: 12,
                                align: TextAnchor.UpperRight,
                                overflow: VerticalWrapMode.Overflow,
                                anchorMin: "0 1",
                                anchorMax: "0 1",
                                offsetMin: "576 -44",
                                offsetMax: "776 -24",
                                name: "rerolls_info");
                        }

                        {
                            BattlePassRebornUI.Builder.Element GvTOGj = xYuKDj.AddContainer(
                                anchorMin: "0 1",
                                anchorMax: "0 1",
                                offsetMin: "12 -288",
                                offsetMax: "784 -57",
                                name: "challenges_container");
                            int easyMinY = -69, easyMaxY = 0, easyMinX = 0, easyMaxX = 244;
                            int mediumMinY = -69, mediumMaxY = 0, mediumMinX = 264, mediumMaxX = 508;
                            int hardMinY = -69, hardMaxY = 0, hardMinX = 528, hardMaxX = 772;
                            int i = 0;

                            foreach (var task in data.Tasks)
                            {
                                var mission = _allMissions.FirstOrDefault(x => x.TaskID == task.TaskID);
                                if (mission == null)
                                    continue;
                                int minY = 0, maxY = 0, minX = 0, maxX = 0;
                                string bg_Image = "", status = task.Completed ? "status_fScNCW" : "status_UBWXHE";

                                switch (mission.Difficulty)
                                {
                                    case TaskDifficulty.Easy:
                                        minY = easyMinY;
                                        maxY = easyMaxY;
                                        minX = easyMinX;
                                        maxX = easyMaxX;
                                        easyMinY -= 81;
                                        easyMaxY -= 81;
                                        bg_Image = "bg_UkKjRE";
                                        break;

                                    case TaskDifficulty.Medium:
                                        minY = mediumMinY;
                                        maxY = mediumMaxY;
                                        minX = mediumMinX;
                                        maxX = mediumMaxX;
                                        mediumMinY -= 81;
                                        mediumMaxY -= 81;
                                        bg_Image = "bg_PeBYrm";
                                        break;

                                    case TaskDifficulty.Hard:
                                        minY = hardMinY;
                                        maxY = hardMaxY;
                                        minX = hardMinX;
                                        maxX = hardMaxX;
                                        hardMinY -= 81;
                                        hardMaxY -= 81;
                                        bg_Image = "bg_xGzFMA";
                                        break;

                                    default:
                                        continue;
                                }

                                BattlePassRebornUI.Builder.Element qJtrzc = GvTOGj.AddContainer(
                                    anchorMin: "0 1",
                                    anchorMax: "0 1",
                                    offsetMin: $"{minX} {minY}",
                                    offsetMax: $"{maxX} {maxY}",
                                    name: $"easy_challenges_card_task{i}");
                                BattlePassRebornUI.Builder.Element UkKjRE = qJtrzc.AddImage(
                                    content: GetImage(bg_Image),
                                    material: "",
                                    anchorMin: "0 0",
                                    anchorMax: "1 1",
                                    offsetMin: "0 0",
                                    offsetMax: "0 0",
                                    name: $"bg_task{i}");
                                BattlePassRebornUI.Builder.Element QODSLl = qJtrzc.AddText(
                                    text: GetLang(mission.TaskID, player.UserIDString),
                                    font: BattlePassRebornUI.Builder.Font.RobotoCondensedBold,
                                    fontSize: 12,
                                    align: TextAnchor.UpperLeft,
                                    overflow: VerticalWrapMode.Overflow,
                                    anchorMin: "0 1",
                                    anchorMax: "0 1",
                                    offsetMin: "13 -27",
                                    offsetMax: "183 -13",
                                    name: $"callenges_name_task{i}");
                                BattlePassRebornUI.Builder.Element iRfTPg = qJtrzc.AddText(
                                    text: $"{task.Progress}/{mission.TargetCount}",
                                    color: "0.7058824 0.7058824 0.7058824 1",
                                    font: BattlePassRebornUI.Builder.Font.RobotoCondensedRegular,
                                    fontSize: 10,
                                    align: TextAnchor.UpperLeft,
                                    overflow: VerticalWrapMode.Overflow,
                                    anchorMin: "0 1",
                                    anchorMax: "0 1",
                                    offsetMin: "13 -61",
                                    offsetMax: "183 -49",
                                    name: $"callenges_info_otional_task{i}");
                                BattlePassRebornUI.Builder.Element fScNCW = qJtrzc.AddImage(
                                    content: GetImage(status),
                                    material: "",
                                    anchorMin: "0 1",
                                    anchorMax: "0 1",
                                    offsetMin: "189 -55",
                                    offsetMax: "231 -13",
                                    name: $"status_task{i}");
                                {
                                    if (!task.Completed && _config.Tasks.RefreshTask)
                                    {
                                        BattlePassRebornUI.Builder.Element zvCIHY = qJtrzc.AddButton(
                                            command: $"UI_REFRESH_TASK {task.TaskID}",
                                            color: "1 1 1 0.0999",
                                            sprite: "",
                                            material: "",
                                            imageType: UnityEngine.UI.Image.Type.Simple,
                                            anchorMin: "0 1",
                                            anchorMax: "0 1",
                                            offsetMin: "224 -61",
                                            offsetMax: "236 -49",
                                            name: $"reroll_task{i}");
                                        BattlePassRebornUI.Builder.Element zZtJnt = zvCIHY.AddImage(
                                            content: GetImage("Image_zZtJnt"),
                                            material: "",
                                            anchorMin: "0 0",
                                            anchorMax: "1 1",
                                            offsetMin: "0 0",
                                            offsetMax: "0 0",
                                            name: $"Image_task{i}");
                                    }
                                }
                                i++;
                                if (i == 9)
                                    break;
                            }
                        }
                        var countMission = isPremium ? _config.Tasks.DailyTasksPremium : _config.Tasks.DailyTasks;
                        if (data.CountCompletedTask >= countMission && _config.Tasks.DailyTasks != 0)
                        {
                            BattlePassRebornUI.Builder.Element YiMLYM = xYuKDj.AddPanel(
                                sprite: "",
                                material: "",
                                color: "0 0 0 0.9377",
                                imageType: UnityEngine.UI.Image.Type.Simple,
                                anchorMin: "0 0",
                                anchorMax: "1 1",
                                offsetMin: "0 0",
                                offsetMax: "0 0",
                                name: $"task_block");
                            BattlePassRebornUI.Builder.Element eUmWKQ = YiMLYM.AddText(
                                text: GetLang("TEXT_TASK_BLOCK", player.UserIDString),
                                font: BattlePassRebornUI.Builder.Font.RobotoCondensedBold,
                                fontSize: 20,
                                align: TextAnchor.MiddleCenter,
                                overflow: VerticalWrapMode.Overflow,
                                anchorMin: "0 0.5",
                                anchorMax: "1 0.5",
                                offsetMin: "0 -40",
                                offsetMax: "0 0",
                                name: $"Text_task_block");
                            BattlePassRebornUI.Builder.Element iUQHgg = YiMLYM.AddImage(
                                content: GetImage("Image_iUQHgg"),
                                material: "",
                                anchorMin: "0.5 0.5",
                                anchorMax: "0.5 0.5",
                                offsetMin: "-16 10",
                                offsetMax: "16 42",
                                name: $"Image_task_block");
                        }
                    }
                    {
                        BattlePassRebornUI.Builder.Element njbGtl = EqwEyw.AddContainer(
                            anchorMin: "0 0",
                            anchorMax: "1 0",
                            offsetMin: "0 0",
                            offsetMax: "0 206",
                            name: "reward_container");
                        {
                            BattlePassRebornUI.Builder.Element PQxotn = njbGtl.AddContainer(
                                anchorMin: "0 1",
                                anchorMax: "1 1",
                                offsetMin: "0 -26",
                                offsetMax: "0 0",
                                name: "progress_info");
                            BattlePassRebornUI.Builder.Element ZYiSSE = PQxotn.AddText(
                                text: GetLang("TEXT_TIER", player.UserIDString, data.Level),
                                font: BattlePassRebornUI.Builder.Font.RobotoCondensedBold,
                                fontSize: 12,
                                align: TextAnchor.MiddleLeft,
                                overflow: VerticalWrapMode.Overflow,
                                anchorMin: "0 1",
                                anchorMax: "0 1",
                                offsetMin: "10 -23",
                                offsetMax: "80 0",
                                name: "next_tier");
                            BattlePassRebornUI.Builder.Element ZcKrvn = PQxotn.AddText(
                                text: $"{data.Exp}/{expPerLevel}",
                                font: BattlePassRebornUI.Builder.Font.RobotoCondensedBold,
                                fontSize: 12,
                                align: TextAnchor.MiddleRight,
                                overflow: VerticalWrapMode.Overflow,
                                anchorMin: "0 1",
                                anchorMax: "0 1",
                                offsetMin: "80 -23",
                                offsetMax: "160 0",
                                name: "point");
                            BattlePassRebornUI.Builder.Element cMKVKe = PQxotn.AddText(
                                text: GetLang("TEXT_XP", player.UserIDString),
                                font: BattlePassRebornUI.Builder.Font.RobotoCondensedBold,
                                fontSize: 12,
                                align: TextAnchor.MiddleLeft,
                                overflow: VerticalWrapMode.Overflow,
                                anchorMin: "0 1",
                                anchorMax: "0 1",
                                offsetMin: "165 -23",
                                offsetMax: "190 0",
                                name: "XP");
                            {
                                BattlePassRebornUI.Builder.Element AeeJUL = PQxotn.AddContainer(
                                    anchorMin: "0 0",
                                    anchorMax: "1 0",
                                    offsetMin: "0 0",
                                    offsetMax: "0 3",
                                    name: "progess_bar");
                                BattlePassRebornUI.Builder.Element bFpBNN = AeeJUL.AddPanel(
                                    sprite: "",
                                    material: "",
                                    color: "0.05882353 0.0627451 0.0627451 0.7977",
                                    imageType: UnityEngine.UI.Image.Type.Simple,
                                    anchorMin: "0 0",
                                    anchorMax: "1 1",
                                    offsetMin: "0 0",
                                    offsetMax: "0 0",
                                    name: "bg");
                                BattlePassRebornUI.Builder.Element VQXSTy = AeeJUL.AddPanel(
                                    sprite: "",
                                    material: "",
                                    color: "0.3647059 0.4470588 0.2196078 1",
                                    imageType: UnityEngine.UI.Image.Type.Simple,
                                    anchorMin: "0 0",
                                    anchorMax: "0 1",
                                    offsetMin: "0 0",
                                    offsetMax: $"{1194 * data.Exp / expPerLevel} 0",
                                    name: "Panel");
                            }
                        }
                        {
                            BattlePassRebornUI.Builder.Element sQXNjh = njbGtl.AddContainer(
                                anchorMin: "0 0",
                                anchorMax: "1 0",
                                offsetMin: "0 0",
                                offsetMax: "0 172",
                                name: "reward_scroll");

                            var totalRewardsCount = _allLevels.Sum(lvl =>
                            {
                                var nonPremium = lvl.RewardList.FirstOrDefault(r => !r.IsPremium);
                                var premium = lvl.RewardList.FirstOrDefault(r => r.IsPremium);
                                return (nonPremium != null ? 1 : 0) + (premium != null ? 1 : 0);
                            });

                            var scrollRoot = sQXNjh.AddPanel(
                                sprite: "",
                                material: "",
                                color: "1 1 1 0",
                                anchorMin: "0 0",
                                anchorMax: "1 1",
                                offsetMin: "0 0",
                                offsetMax: "0 0",
                                name: "ScrollRootRewards");

                            scrollRoot.Components.AddScrollView(
                                horizontal: true,
                                vertical: false,
                                inertia: true,
                                movementType: ScrollRect.MovementType.Elastic,
                                decelerationRate: 0.1f,
                                elasticity: 0.1f,
                                scrollSensitivity: 10f,
                                horizontalScrollbar: new CuiScrollbar
                                {
                                    Invert = true,
                                    HandleColor = "#FFFFFFFF",
                                    HighlightColor = "#AAAAAAFF",
                                    PressedColor = "#888888FF",
                                    TrackColor = "#00000080",
                                    HandleSprite = "assets/content/ui/ui.background.tiletex.psd",
                                    TrackSprite = "assets/content/ui/ui.background.tiletex.psd",
                                    AutoHide = true,
                                    Size = -1
                                },
                                anchorMin: "0 1",
                                anchorMax: "1 1",
                                offsetMin: "0 0",
                                offsetMax: $"{1194 * (int)(totalRewardsCount / 11)} 0");

                            var scrollRootPanel = scrollRoot.AddPanel(
                                sprite: "",
                                material: "",
                                color: "1 1 1 0",
                                anchorMin: "0 0",
                                anchorMax: "1 1",
                                offsetMin: "0 0",
                                offsetMax: "0 0",
                                name: "ScrollRootPanel");

                            {
                                int i = 0, minX = 0;
                                foreach (var lvl in _allLevels)
                                {
                                    var firstNonPremium = lvl.RewardList.FirstOrDefault(r => !r.IsPremium);
                                    var firstPremium = lvl.RewardList.FirstOrDefault(r => r.IsPremium);

                                    var selectedRewards = new List<LevelReward>();
                                    if (firstNonPremium != null) selectedRewards.Add(firstNonPremium);
                                    if (firstPremium != null) selectedRewards.Add(firstPremium);

                                    if (firstNonPremium == null && firstPremium == null)
                                        continue;

                                    bool lvlHeigher = lvl.LevelID > data.Level;
                                    string colorPanelTier = lvlHeigher
                                        ? "0.1333333 0.1333333 0.1333333 1"
                                        : "0.3058824 0.3058824 0.3058824 1";
                                    var rewardInStorage =
                                        data.RewardsReceived.FirstOrDefault(x => x.Level == lvl.LevelID);

                                    if (selectedRewards.Count == 1)
                                    {
                                        BattlePassRebornUI.Builder.Element dxdGnY = scrollRoot.AddContainer(
                                            anchorMin: "0 1",
                                            anchorMax: "0 1",
                                            offsetMin: $"{minX} -86",
                                            offsetMax: $"{minX + 100} 86",
                                            name: $"reward_solo_reward{i}");
                                        {
                                            BattlePassRebornUI.Builder.Element XLrInu = dxdGnY.AddContainer(
                                                anchorMin: "0 1",
                                                anchorMax: "0 1",
                                                offsetMin: "0 -140",
                                                offsetMax: "100 0",
                                                name: $"Container_reward{i}");
                                            BattlePassRebornUI.Builder.Element eFJxML = XLrInu.AddPanel(
                                                sprite: "",
                                                material: "",
                                                color: "0 0 0 0.7014",
                                                imageType: UnityEngine.UI.Image.Type.Simple,
                                                anchorMin: "0 0",
                                                anchorMax: "1 1",
                                                offsetMin: "0 0",
                                                offsetMax: "0 0",
                                                name: $"bg_reward{i}");
                                            BattlePassRebornUI.Builder.Element vawAMJ = XLrInu.AddText(
                                                text: selectedRewards[0].IsPremium
                                                    ? (string.IsNullOrEmpty(lvl.generalDisplayNamePremium)
                                                        ? selectedRewards[0].displayName
                                                        : lvl.generalDisplayNamePremium)
                                                    : (string.IsNullOrEmpty(lvl.generalDisplayName)
                                                        ? selectedRewards[0].displayName
                                                        : lvl.generalDisplayName),
                                                color: "0.9254902 0.8901961 0.8588235 1",
                                                font: BattlePassRebornUI.Builder.Font.RobotoCondensedBold,
                                                fontSize: 12,
                                                align: TextAnchor.MiddleCenter,
                                                overflow: VerticalWrapMode.Overflow,
                                                anchorMin: "0 1",
                                                anchorMax: "0 1",
                                                offsetMin: "6 -35",
                                                offsetMax: "94 -6",
                                                name: $"reward_title_reward{i}");
                                            var img = selectedRewards[0].IsPremium
                                                ? (string.IsNullOrEmpty(lvl.ImagePremium)
                                                    ? (string.IsNullOrEmpty(selectedRewards[0].img) ? selectedRewards[0].ShortName : selectedRewards[0].img)
                                                    : lvl.ImagePremium)
                                                : (string.IsNullOrEmpty(lvl.ImageDefault)
                                                    ? (string.IsNullOrEmpty(selectedRewards[0].img) ? selectedRewards[0].ShortName : selectedRewards[0].img)
                                                    : lvl.ImageDefault);
                                            if (img.StartsWith("https") || string.IsNullOrEmpty(img) || img != selectedRewards[0].ShortName)
                                            {
                                                BattlePassRebornUI.Builder.Element ihqtOs = XLrInu.AddImage(
                                                    content: GetImage(img),
                                                    material: "",
                                                    anchorMin: "0 1",
                                                    anchorMax: "0 1",
                                                    offsetMin: "10 -121",
                                                    offsetMax: "90 -41",
                                                    name: $"Image_reward{i}");
                                            }
                                            else
                                            {
                                                BattlePassRebornUI.Builder.Element ihqtOs = XLrInu.AddIcon(
                                                    itemId: _shortNameToItemID[img],
                                                    material: "",
                                                    anchorMin: "0 1",
                                                    anchorMax: "0 1",
                                                    offsetMin: "10 -121",
                                                    offsetMax: "90 -41",
                                                    name: $"Image_reward{i}");
                                            }

                                            BattlePassRebornUI.Builder.Element hRTfcj = XLrInu.AddText(
                                                text: $"x{selectedRewards[0].Amount}",
                                                color: "0.9254902 0.8901961 0.8588235 1",
                                                font: BattlePassRebornUI.Builder.Font.RobotoCondensedBold,
                                                align: TextAnchor.MiddleRight,
                                                overflow: VerticalWrapMode.Overflow,
                                                anchorMin: "0 1",
                                                anchorMax: "0 1",
                                                offsetMin: "44 -134",
                                                offsetMax: "94 -120",
                                                name: $"amound_reward{i}");
                                        }
                                        if (lvlHeigher || (selectedRewards[0].IsPremium == true && !isPremium))
                                        {
                                            BattlePassRebornUI.Builder.Element YiMLYM = dxdGnY.AddPanel(
                                                sprite: "",
                                                material: "",
                                                color: "0 0 0 0.7977",
                                                imageType: UnityEngine.UI.Image.Type.Simple,
                                                anchorMin: "0 0",
                                                anchorMax: "1 1",
                                                offsetMin: "0 0",
                                                offsetMax: "0 0",
                                                name: $"premium_block_reward{i}");
                                            BattlePassRebornUI.Builder.Element eUmWKQ = YiMLYM.AddText(
                                                text: selectedRewards[0].IsPremium
                                                    ? GetLang("TEXT_PREMIUM", player.UserIDString)
                                                    : "",
                                                font: BattlePassRebornUI.Builder.Font.RobotoCondensedBold,
                                                fontSize: 12,
                                                align: TextAnchor.MiddleCenter,
                                                overflow: VerticalWrapMode.Overflow,
                                                anchorMin: "0 1",
                                                anchorMax: "1 1",
                                                offsetMin: "0 -101",
                                                offsetMax: "0 -83",
                                                name: $"Text_reward{i}");
                                            BattlePassRebornUI.Builder.Element iUQHgg = YiMLYM.AddImage(
                                                content: GetImage("Image_iUQHgg"),
                                                material: "",
                                                anchorMin: "0.5 1",
                                                anchorMax: "0.5 1",
                                                offsetMin: "-16 -75",
                                                offsetMax: "16 -43",
                                                name: $"Image_reward1{i}");
                                        }
                                        else if (rewardInStorage != null)
                                        {
                                            BattlePassRebornUI.Builder.Element YiMLYM = dxdGnY.AddPanel(
                                                sprite: "",
                                                material: "",
                                                color: "0 0 0 0.7977",
                                                imageType: UnityEngine.UI.Image.Type.Simple,
                                                anchorMin: "0 0",
                                                anchorMax: "1 1",
                                                offsetMin: "0 0",
                                                offsetMax: "0 0",
                                                name: $"con_reward_solo_reward{i}");
                                            BattlePassRebornUI.Builder.Element eUmWKQ = YiMLYM.AddText(
                                                text: selectedRewards[0].IsPremium
                                                    ? GetLang("TEXT_PREMIUM", player.UserIDString)
                                                    : "",
                                                font: BattlePassRebornUI.Builder.Font.RobotoCondensedBold,
                                                fontSize: 12,
                                                align: TextAnchor.MiddleCenter,
                                                overflow: VerticalWrapMode.Overflow,
                                                anchorMin: "0 1",
                                                anchorMax: "1 1",
                                                offsetMin: "0 -101",
                                                offsetMax: "0 -83",
                                                name: $"Text_reward_solo_reward{i}");
                                            BattlePassRebornUI.Builder.Element iUQHgg = YiMLYM.AddImage(
                                                content: GetImage("status_fScNCW"),
                                                material: "",
                                                anchorMin: "0.5 1",
                                                anchorMax: "0.5 1",
                                                offsetMin: "-16 -75",
                                                offsetMax: "16 -43",
                                                name: $"Image_reward_solo_reward{i}");
                                        }
                                        else
                                        {
                                            var priv = selectedRewards[0].IsPremium ? "PREM" : "NOPREM";
                                            BattlePassRebornUI.Builder.Element ehBQth = dxdGnY.AddButton(
                                                command: $"UI_TAKE {lvl.LevelID} {priv} reward_solo_reward{i}",
                                                color: "0.1333333 0.1333333 0.1333333 0",
                                                imageType: UnityEngine.UI.Image.Type.Simple,
                                                anchorMin: "0 0",
                                                anchorMax: "1 1",
                                                offsetMin: "0 0",
                                                offsetMax: "0 0",
                                                name: $"Btn take_reward{i}");
                                        }

                                        {
                                            BattlePassRebornUI.Builder.Element mVlOmT = dxdGnY.AddPanel(
                                                sprite: "",
                                                material: "",
                                                color: colorPanelTier,
                                                imageType: UnityEngine.UI.Image.Type.Simple,
                                                anchorMin: "0 0",
                                                anchorMax: "1 0",
                                                offsetMin: "0 0",
                                                offsetMax: "0 26",
                                                name: $"tier_info_reward{i}");
                                            BattlePassRebornUI.Builder.Element wOFwgJ = mVlOmT.AddText(
                                                text: GetLang("TEXT_TIER", player.UserIDString, lvl.LevelID),
                                                color: "0.9254902 0.8901961 0.8588235 1",
                                                font: BattlePassRebornUI.Builder.Font.RobotoCondensedBold,
                                                fontSize: 12,
                                                align: TextAnchor.MiddleCenter,
                                                overflow: VerticalWrapMode.Overflow,
                                                anchorMin: "0 0",
                                                anchorMax: "1 1",
                                                offsetMin: "0 0",
                                                offsetMax: "0 0",
                                                name: $"Text_reward1{i}");
                                        }
                                        minX += 112;
                                    }

                                    if (selectedRewards.Count == 2)
                                    {
                                        BattlePassRebornUI.Builder.Element IRWPmn = scrollRoot.AddContainer(
                                            anchorMin: "0 1",
                                            anchorMax: "0 1",
                                            offsetMin: $"{minX} -86",
                                            offsetMax: $"{minX + 212} 86",
                                            name: $"reward_duo{i}");
                                        {
                                            BattlePassRebornUI.Builder.Element tYOkDv = IRWPmn.AddContainer(
                                                anchorMin: "0 1",
                                                anchorMax: "0 1",
                                                offsetMin: "0 -140",
                                                offsetMax: "100 0",
                                                name: $"reward_reward{i}");
                                            BattlePassRebornUI.Builder.Element awjeKe = tYOkDv.AddPanel(
                                                sprite: "",
                                                material: "",
                                                color: "0 0 0 0.7014",
                                                imageType: UnityEngine.UI.Image.Type.Simple,
                                                anchorMin: "0 0",
                                                anchorMax: "1 1",
                                                offsetMin: "0 0",
                                                offsetMax: "0 0",
                                                name: $"bg_reward{i}");
                                            BattlePassRebornUI.Builder.Element CmPwut = tYOkDv.AddText(
                                                text: string.IsNullOrEmpty(lvl.generalDisplayName)
                                                    ? selectedRewards[0].displayName
                                                    : lvl.generalDisplayName,
                                                color: "0.9254902 0.8901961 0.8588235 1",
                                                font: BattlePassRebornUI.Builder.Font.RobotoCondensedBold,
                                                fontSize: 12,
                                                align: TextAnchor.MiddleCenter,
                                                overflow: VerticalWrapMode.Overflow,
                                                anchorMin: "0 1",
                                                anchorMax: "0 1",
                                                offsetMin: "6 -35",
                                                offsetMax: "94 -6",
                                                name: $"reward_title_reward{i}");

                                            var img = string.IsNullOrEmpty(lvl.ImageDefault)
                                                ? (string.IsNullOrEmpty(selectedRewards[0].img) ? selectedRewards[0].ShortName : selectedRewards[0].img)
                                                : lvl.ImageDefault;
                                            if (img.StartsWith("https") || string.IsNullOrEmpty(img) || img != selectedRewards[0].ShortName)
                                            {
                                                BattlePassRebornUI.Builder.Element ihqtOs = tYOkDv.AddImage(
                                                    content: GetImage(img),
                                                    material: "",
                                                    anchorMin: "0 1",
                                                    anchorMax: "0 1",
                                                    offsetMin: "10 -121",
                                                    offsetMax: "90 -41",
                                                    name: $"Image_reward{i}");
                                            }
                                            else
                                            {
                                                BattlePassRebornUI.Builder.Element ihqtOs = tYOkDv.AddIcon(
                                                    itemId: _shortNameToItemID[img],
                                                    material: "",
                                                    anchorMin: "0 1",
                                                    anchorMax: "0 1",
                                                    offsetMin: "10 -121",
                                                    offsetMax: "90 -41",
                                                    name: $"Image_reward{i}");
                                            }

                                            BattlePassRebornUI.Builder.Element RObhOQ = tYOkDv.AddText(
                                                text: $"x{selectedRewards[0].Amount}",
                                                color: "0.9254902 0.8901961 0.8588235 1",
                                                font: BattlePassRebornUI.Builder.Font.RobotoCondensedBold,
                                                align: TextAnchor.MiddleRight,
                                                overflow: VerticalWrapMode.Overflow,
                                                anchorMin: "0 1",
                                                anchorMax: "0 1",
                                                offsetMin: "44 -134",
                                                offsetMax: "94 -120",
                                                name: $"amound_reward{i}");
                                            if (lvlHeigher || (selectedRewards[0].IsPremium == true && !isPremium))
                                            {
                                                BattlePassRebornUI.Builder.Element YiMLYM = tYOkDv.AddPanel(
                                                    sprite: "",
                                                    material: "",
                                                    color: "0 0 0 0.7977",
                                                    imageType: UnityEngine.UI.Image.Type.Simple,
                                                    anchorMin: "0 0",
                                                    anchorMax: "1 1",
                                                    offsetMin: "0 0",
                                                    offsetMax: "0 0",
                                                    name: $"premium_block_reward{i}");
                                                BattlePassRebornUI.Builder.Element eUmWKQ = YiMLYM.AddText(
                                                    text: selectedRewards[0].IsPremium
                                                        ? GetLang("TEXT_PREMIUM", player.UserIDString)
                                                        : "",
                                                    font: BattlePassRebornUI.Builder.Font.RobotoCondensedBold,
                                                    fontSize: 12,
                                                    align: TextAnchor.MiddleCenter,
                                                    overflow: VerticalWrapMode.Overflow,
                                                    anchorMin: "0 1",
                                                    anchorMax: "1 1",
                                                    offsetMin: "0 -101",
                                                    offsetMax: "0 -83",
                                                    name: $"Text_reward{i}");
                                                BattlePassRebornUI.Builder.Element iUQHgg = YiMLYM.AddImage(
                                                    content: GetImage("Image_iUQHgg"),
                                                    material: "",
                                                    anchorMin: "0.5 1",
                                                    anchorMax: "0.5 1",
                                                    offsetMin: "-16 -75",
                                                    offsetMax: "16 -43",
                                                    name: $"Image_reward1{i}");
                                            }
                                            else if (rewardInStorage != null && rewardInStorage.DefaultReward == true)
                                            {
                                                BattlePassRebornUI.Builder.Element YiMLYM = tYOkDv.AddPanel(
                                                    sprite: "",
                                                    material: "",
                                                    color: "0 0 0 0.7977",
                                                    imageType: UnityEngine.UI.Image.Type.Simple,
                                                    anchorMin: "0 0",
                                                    anchorMax: "1 1",
                                                    offsetMin: "0 0",
                                                    offsetMax: "0 0",
                                                    name: $"con_reward_reward{i}");
                                                BattlePassRebornUI.Builder.Element eUmWKQ = YiMLYM.AddText(
                                                    text: selectedRewards[0].IsPremium
                                                        ? GetLang("TEXT_PREMIUM", player.UserIDString)
                                                        : "",
                                                    font: BattlePassRebornUI.Builder.Font.RobotoCondensedBold,
                                                    fontSize: 12,
                                                    align: TextAnchor.MiddleCenter,
                                                    overflow: VerticalWrapMode.Overflow,
                                                    anchorMin: "0 1",
                                                    anchorMax: "1 1",
                                                    offsetMin: "0 -101",
                                                    offsetMax: "0 -83",
                                                    name: $"Text_reward_reward{i}");
                                                BattlePassRebornUI.Builder.Element iUQHgg = YiMLYM.AddImage(
                                                    content: GetImage("status_fScNCW"),
                                                    material: "",
                                                    anchorMin: "0.5 1",
                                                    anchorMax: "0.5 1",
                                                    offsetMin: "-16 -75",
                                                    offsetMax: "16 -43",
                                                    name: $"Image_reward_reward{i}");
                                            }
                                            else
                                            {
                                                var priv = selectedRewards[0].IsPremium ? "PREM" : "NOPREM";
                                                BattlePassRebornUI.Builder.Element ehBQth = tYOkDv.AddButton(
                                                    command: $"UI_TAKE {lvl.LevelID} {priv} reward_reward{i}",
                                                    color: "0.1333333 0.1333333 0.1333333 0",
                                                    imageType: UnityEngine.UI.Image.Type.Simple,
                                                    anchorMin: "0 0",
                                                    anchorMax: "1 1",
                                                    offsetMin: "0 0",
                                                    offsetMax: "0 0",
                                                    name: $"Btn take_reward{i}");
                                            }
                                        }
                                        {
                                            BattlePassRebornUI.Builder.Element jGhbIs = IRWPmn.AddContainer(
                                                anchorMin: "0 1",
                                                anchorMax: "0 1",
                                                offsetMin: "112 -140",
                                                offsetMax: "212 0",
                                                name: $"reward_need_premium_reward{i}");
                                            BattlePassRebornUI.Builder.Element EHCQvG = jGhbIs.AddPanel(
                                                sprite: "",
                                                material: "",
                                                color: "0 0 0 0.7014",
                                                imageType: UnityEngine.UI.Image.Type.Simple,
                                                anchorMin: "0 0",
                                                anchorMax: "1 1",
                                                offsetMin: "0 0",
                                                offsetMax: "0 0",
                                                name: $"bg_reward2{i}");
                                            BattlePassRebornUI.Builder.Element fDqMVp = jGhbIs.AddText(
                                                text: string.IsNullOrEmpty(lvl.generalDisplayNamePremium)
                                                    ? selectedRewards[1].displayName
                                                    : lvl.generalDisplayNamePremium,
                                                color: "0.9254902 0.8901961 0.8588235 1",
                                                font: BattlePassRebornUI.Builder.Font.RobotoCondensedBold,
                                                fontSize: 12,
                                                align: TextAnchor.MiddleCenter,
                                                overflow: VerticalWrapMode.Overflow,
                                                anchorMin: "0 1",
                                                anchorMax: "0 1",
                                                offsetMin: "6 -35",
                                                offsetMax: "94 -6",
                                                name: $"reward_title_reward2{i}");

                                            var img = string.IsNullOrEmpty(lvl.ImagePremium)
                                                ? (string.IsNullOrEmpty(selectedRewards[1].img) ? selectedRewards[1].ShortName : selectedRewards[1].img)
                                                : lvl.ImagePremium;
                                            if (img.StartsWith("https") || string.IsNullOrEmpty(img) || img != selectedRewards[1].ShortName)
                                            {
                                                BattlePassRebornUI.Builder.Element ihqtOs = jGhbIs.AddImage(
                                                    content: GetImage(img),
                                                    material: "",
                                                    anchorMin: "0 1",
                                                    anchorMax: "0 1",
                                                    offsetMin: "10 -121",
                                                    offsetMax: "90 -41",
                                                    name: $"Image_reward2{i}");
                                            }
                                            else
                                            {
                                                BattlePassRebornUI.Builder.Element ihqtOs = jGhbIs.AddIcon(
                                                    itemId: _shortNameToItemID[img],
                                                    material: "",
                                                    anchorMin: "0 1",
                                                    anchorMax: "0 1",
                                                    offsetMin: "10 -121",
                                                    offsetMax: "90 -41",
                                                    name: $"Image_reward2{i}");
                                            }

                                            BattlePassRebornUI.Builder.Element OKyGAR = jGhbIs.AddText(
                                                text: $"x{selectedRewards[1].Amount}",
                                                color: "0.9254902 0.8901961 0.8588235 1",
                                                font: BattlePassRebornUI.Builder.Font.RobotoCondensedBold,
                                                align: TextAnchor.MiddleRight,
                                                overflow: VerticalWrapMode.Overflow,
                                                anchorMin: "0 1",
                                                anchorMax: "0 1",
                                                offsetMin: "44 -134",
                                                offsetMax: "94 -120",
                                                name: $"amound_reward2{i}");
                                            if (lvlHeigher || (selectedRewards[1].IsPremium == true && !isPremium))
                                            {
                                                BattlePassRebornUI.Builder.Element YiMLYM = jGhbIs.AddPanel(
                                                    sprite: "",
                                                    material: "",
                                                    color: "0 0 0 0.7977",
                                                    imageType: UnityEngine.UI.Image.Type.Simple,
                                                    anchorMin: "0 0",
                                                    anchorMax: "1 1",
                                                    offsetMin: "0 0",
                                                    offsetMax: "0 0",
                                                    name: $"premium_block_reward2{i}");
                                                BattlePassRebornUI.Builder.Element eUmWKQ = YiMLYM.AddText(
                                                    text: selectedRewards[1].IsPremium
                                                        ? GetLang("TEXT_PREMIUM", player.UserIDString)
                                                        : "",
                                                    font: BattlePassRebornUI.Builder.Font.RobotoCondensedBold,
                                                    fontSize: 12,
                                                    align: TextAnchor.MiddleCenter,
                                                    overflow: VerticalWrapMode.Overflow,
                                                    anchorMin: "0 1",
                                                    anchorMax: "1 1",
                                                    offsetMin: "0 -101",
                                                    offsetMax: "0 -83",
                                                    name: $"Text_reward2{i}");
                                                BattlePassRebornUI.Builder.Element iUQHgg = YiMLYM.AddImage(
                                                    content: GetImage("Image_iUQHgg"),
                                                    material: "",
                                                    anchorMin: "0.5 1",
                                                    anchorMax: "0.5 1",
                                                    offsetMin: "-16 -75",
                                                    offsetMax: "16 -43",
                                                    name: $"Image_reward3{i}");
                                            }
                                            else if (rewardInStorage != null && rewardInStorage.PremiumReward == true)
                                            {
                                                BattlePassRebornUI.Builder.Element YiMLYM = jGhbIs.AddPanel(
                                                    sprite: "",
                                                    material: "",
                                                    color: "0 0 0 0.7977",
                                                    imageType: UnityEngine.UI.Image.Type.Simple,
                                                    anchorMin: "0 0",
                                                    anchorMax: "1 1",
                                                    offsetMin: "0 0",
                                                    offsetMax: "0 0",
                                                    name: $"con_reward_need_premium_reward{i}");
                                                BattlePassRebornUI.Builder.Element eUmWKQ = YiMLYM.AddText(
                                                    text: selectedRewards[1].IsPremium
                                                        ? GetLang("TEXT_PREMIUM", player.UserIDString)
                                                        : "",
                                                    font: BattlePassRebornUI.Builder.Font.RobotoCondensedBold,
                                                    fontSize: 12,
                                                    align: TextAnchor.MiddleCenter,
                                                    overflow: VerticalWrapMode.Overflow,
                                                    anchorMin: "0 1",
                                                    anchorMax: "1 1",
                                                    offsetMin: "0 -101",
                                                    offsetMax: "0 -83",
                                                    name: $"Text_reward_need_premium_reward{i}");
                                                BattlePassRebornUI.Builder.Element iUQHgg = YiMLYM.AddImage(
                                                    content: GetImage("status_fScNCW"),
                                                    material: "",
                                                    anchorMin: "0.5 1",
                                                    anchorMax: "0.5 1",
                                                    offsetMin: "-16 -75",
                                                    offsetMax: "16 -43",
                                                    name: $"Image_reward_need_premium_reward{i}");
                                            }
                                            else
                                            {
                                                var priv = selectedRewards[1].IsPremium ? "PREM" : "NOPREM";
                                                BattlePassRebornUI.Builder.Element ehBQth = jGhbIs.AddButton(
                                                    command:
                                                    $"UI_TAKE {lvl.LevelID} {priv} reward_need_premium_reward{i}",
                                                    color: "0.1333333 0.1333333 0.1333333 0",
                                                    imageType: UnityEngine.UI.Image.Type.Simple,
                                                    anchorMin: "0 0",
                                                    anchorMax: "1 1",
                                                    offsetMin: "0 0",
                                                    offsetMax: "0 0",
                                                    name: $"Btn take_reward2{i}");
                                            }
                                        }
                                        {
                                            BattlePassRebornUI.Builder.Element CbmTIb = IRWPmn.AddPanel(
                                                sprite: "",
                                                material: "",
                                                color: colorPanelTier,
                                                imageType: UnityEngine.UI.Image.Type.Simple,
                                                anchorMin: "0 0",
                                                anchorMax: "1 0",
                                                offsetMin: "0 0",
                                                offsetMax: "0 26",
                                                name: $"tier_info_reward2{i}");
                                            BattlePassRebornUI.Builder.Element pouZlZ = CbmTIb.AddText(
                                                text: GetLang("TEXT_TIER", player.UserIDString, lvl.LevelID),
                                                color: "0.9254902 0.8901961 0.8588235 1",
                                                font: BattlePassRebornUI.Builder.Font.RobotoCondensedBold,
                                                fontSize: 12,
                                                align: TextAnchor.MiddleCenter,
                                                overflow: VerticalWrapMode.Overflow,
                                                anchorMin: "0 0",
                                                anchorMax: "1 1",
                                                offsetMin: "0 0",
                                                offsetMax: "0 0",
                                                name: $"Text_reward3{i}");
                                        }

                                        minX += 224;
                                    }

                                    i++;
                                }
                            }
                        }
                    }
                }
            }

            container.Add(new CuiPanel
            {
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 0" },
                Image = { Color = "0 0 0 0" }
            }, "UI.Server.Panel", "UI.Server.Panel.BattlePassReborn.Footer");

            BattlePassRebornUI.Builder.Root rootFooter =
                new BattlePassRebornUI.Builder.Root("UI.Server.Panel.BattlePassReborn.Footer");
            {
                {
                    BattlePassRebornUI.Builder.Element YByHIP = rootFooter.AddContainer(
                        anchorMin: "0 0",
                        anchorMax: "1 0",
                        offsetMin: "0 0",
                        offsetMax: "0 40",
                        name: "Footer");
                    BattlePassRebornUI.Builder.Element BqHenx = YByHIP.AddPanel(
                        sprite: "",
                        material: "",
                        color: "0.05882353 0.05882353 0.05882353 1",
                        imageType: UnityEngine.UI.Image.Type.Simple,
                        anchorMin: "0 0",
                        anchorMax: "1 1",
                        offsetMin: "0 0",
                        offsetMax: "0 0",
                        name: "bg");
                    BattlePassRebornUI.Builder.Element ieeMqn = YByHIP.AddImage(
                        content: GetImage("Image_ieeMqn"),
                        material: "",
                        anchorMin: "0 1",
                        anchorMax: "0 1",
                        offsetMin: "43 -27",
                        offsetMax: "57 -13",
                        name: "Image");
                    BattlePassRebornUI.Builder.Element grvsUO = YByHIP.AddText(
                        text: GetLang(_UISettings.Hints[_rand.Next(_UISettings.Hints.Count)], player.UserIDString),
                        color: "0.9254902 0.8901961 0.8588235 1",
                        font: BattlePassRebornUI.Builder.Font.RobotoCondensedBold,
                        fontSize: 12,
                        align: TextAnchor.MiddleLeft,
                        overflow: VerticalWrapMode.Overflow,
                        anchorMin: "0 0",
                        anchorMax: "0 1",
                        offsetMin: "66 0",
                        offsetMax: "866 0",
                        name: "Text");

                    BattlePassRebornUI.Builder.Element kHSdpd = YByHIP.AddImage(
                        content: player.UserIDString,
                        material: "",
                        anchorMin: "1 1",
                        anchorMax: "1 1",
                        offsetMin: "-68 -33",
                        offsetMax: "-42 -7",
                        name: "player_avatar");
                    {
                        BattlePassRebornUI.Builder.Element GOMQfQ = YByHIP.AddPanel(
                            sprite: "",
                            material: "",
                            color: "0.1333333 0.1333333 0.1333333 1",
                            imageType: UnityEngine.UI.Image.Type.Simple,
                            anchorMin: "1 1",
                            anchorMax: "1 1",
                            offsetMin: "-174 -33",
                            offsetMax: "-79 -7",
                            name: "player_premium_status_off");
                        BattlePassRebornUI.Builder.Element TpHnoo = GOMQfQ.AddText(
                            text: GetLang("TEXT_PREMIUM", player.UserIDString),
                            color: "1 1 1 0.7354",
                            font: BattlePassRebornUI.Builder.Font.RobotoCondensedBold,
                            fontSize: 12,
                            align: TextAnchor.MiddleCenter,
                            overflow: VerticalWrapMode.Overflow,
                            anchorMin: "0 0",
                            anchorMax: "1 1",
                            offsetMin: "0 0",
                            offsetMax: "0 0",
                            name: "Text");
                    }
                    {
                        BattlePassRebornUI.Builder.Element EYcaGT = YByHIP.AddPanel(
                            sprite: "",
                            material: "",
                            color: "0.1333333 0.1333333 0.1333333 1",
                            imageType: UnityEngine.UI.Image.Type.Simple,
                            anchorMin: "1 1",
                            anchorMax: "1 1",
                            offsetMin: "-174 -33",
                            offsetMax: "-79 -7",
                            name: "player_premium_status_on");
                        if (isPremium)
                        {
                            BattlePassRebornUI.Builder.Element BwnjeJ = EYcaGT.AddImage(
                                content: GetImage("Image_BwnjeJ"),
                                material: "",
                                anchorMin: "0 0",
                                anchorMax: "1 1",
                                offsetMin: "0 0",
                                offsetMax: "0 0",
                                name: "Image");
                        }

                        BattlePassRebornUI.Builder.Element RJmGVJ = EYcaGT.AddText(
                            text: GetLang("TEXT_PREMIUM", player.UserIDString),
                            color: isPremium ? "0.1333333 0.1333333 0.1333333 1" : "0.73 0.69 0.66 1",
                            font: BattlePassRebornUI.Builder.Font.RobotoCondensedBold,
                            fontSize: 12,
                            align: TextAnchor.MiddleCenter,
                            overflow: VerticalWrapMode.Overflow,
                            anchorMin: "0 0",
                            anchorMax: "1 1",
                            offsetMin: "0 0",
                            offsetMax: "0 0",
                            name: "Text");
                    }

                    {
                        BattlePassRebornUI.Builder.Element ehBQth = YByHIP.AddButton(
                            command: "UI_STORAGE",
                            color: "0.1333333 0.1333333 0.1333333 1",
                            imageType: UnityEngine.UI.Image.Type.Simple,
                            anchorMin: "1 1",
                            anchorMax: "1 1",
                            offsetMin: "-259 -33",
                            offsetMax: "-184 -7",
                            name: "Btn storage");
                        BattlePassRebornUI.Builder.Element hRNrFI = ehBQth.AddText(
                            text: GetLang("TEXT_STORAGE", player.UserIDString),
                            color: "0.7294118 0.6941177 0.6588235 1",
                            font: BattlePassRebornUI.Builder.Font.RobotoCondensedBold,
                            fontSize: 10,
                            align: TextAnchor.MiddleLeft,
                            overflow: VerticalWrapMode.Truncate,
                            anchorMin: "0 1",
                            anchorMax: "0 1",
                            offsetMin: "27 -27",
                            offsetMax: "70 0",
                            name: "Text");
                        BattlePassRebornUI.Builder.Element OxvJwN = ehBQth.AddImage(
                            content: GetImage("Frame"),
                            material: "",
                            anchorMin: "0 1",
                            anchorMax: "0 1",
                            offsetMin: "9 -20",
                            offsetMax: "22 -7",
                            name: "Image");
                        BattlePassRebornUI.Builder.Element qFZEBk = ehBQth.AddPanel(
                            sprite: "",
                            material: "",
                            color: "0.1137255 0.1137255 0.1137255 1",
                            imageType: UnityEngine.UI.Image.Type.Simple,
                            anchorMin: "0 0",
                            anchorMax: "1 0",
                            offsetMin: "0 0",
                            offsetMax: "0 1",
                            name: "Panel");
                    }
                }
            }

            NextTick(() =>
            {
                root.Render(player);
                rootFooter.Render(player);
            });
            return container;
        }
        private void PutInContainer(ref CuiElementContainer container, List<BattlePassReborn.BattlePassRebornUI.Builder.Element> elements)
        {
            foreach (var check in elements)
            {
                container.Add(check);
                PutInContainer(ref container,check.Container);
            }
            
        }
        
    }
   

    #endregion

    #region UI Library

    partial class BattlePassReborn
    {
        public class BattlePassRebornUI
        {
            public class Constants
            {
                public static class Defaults
                {
                    public const string VectorZero = "0 0";
                    public const string VectorOne = "1 1";
                    public const string Color = "1 1 1 1";
                    public const string OutlineColor = "0 0 0 1";
                    public const string Sprite = "";
                    public const string Material = "";
                    public const string IconMaterial = "";
                    public const Image.Type ImageType = Image.Type.Simple;
                    public const Builder.Font Font = Builder.Font.RobotoCondensedRegular;
                    public const int FontSize = 14;
                    public const TextAnchor Align = TextAnchor.UpperLeft;
                    public const VerticalWrapMode VerticalOverflow = VerticalWrapMode.Overflow;
                    public const InputField.LineType LineType = InputField.LineType.SingleLine;
                }

                public class Materials
                {
                    public const string InGameBlur = "assets/content/ui/uibackgroundblur-ingamemenu.mat";
                    public const string NoticeBlur = "assets/content/ui/uibackgroundblur-notice.mat";
                    public const string BackgroundBlur = "assets/content/ui/uibackgroundblur.mat";
                    public const string Icon = "assets/icons/iconmaterial.mat";
                }

                public class Colors
                {
                    public const string Transparent = "0 0 0 0";

                    #region Blur

                    public const string Blur_07 = "0.09 0.09 0.09 0.2934";

                    #endregion
                }
            }


            public class Builder
            {
                public enum Font
                {
                    RobotoCondensedBold,
                    RobotoCondensedRegular,
                    RobotoMonoRegular,
                    DroidSansMono,
                    PermanentMarker,
                    PressStart2PRegular,
                    LSD,
                    NotoSansArabicBold,
                    NotoSansArabicRegular,
                    NotoSansHebrewBold,
                }

                private static readonly Dictionary<Font, string> FontToString = new()
                {
                    { Font.RobotoCondensedBold, "RobotoCondensed-Bold.ttf" },
                    { Font.RobotoCondensedRegular, "RobotoCondensed-Regular.ttf" },
                    { Font.RobotoMonoRegular, "RobotoMono-Regular.ttf" },
                    { Font.DroidSansMono, "DroidSansMono.ttf" },
                    { Font.PermanentMarker, "PermanentMarker.ttf" },
                    { Font.PressStart2PRegular, "PressStart2P-Regular.ttf" },
                    { Font.LSD, "lcd.ttf" },
                    { Font.NotoSansArabicBold, "_nonenglish/arabic/notosansarabic-bold.ttf" },
                    { Font.NotoSansArabicRegular, "_nonenglish/arabic/notosansarabic-regular.ttf" },
                    { Font.NotoSansHebrewBold, "_nonenglish/notosanshebrew-bold.ttf" },
                };

                public enum InputType
                {
                    None,
                    Default,
                    HudMenuInput
                }

                private static readonly Dictionary<TextAnchor, string> TextAnchorToString =
                    new()
                    {
                        { TextAnchor.UpperLeft, TextAnchor.UpperLeft.ToString() },
                        { TextAnchor.UpperCenter, TextAnchor.UpperCenter.ToString() },
                        { TextAnchor.UpperRight, TextAnchor.UpperRight.ToString() },
                        { TextAnchor.MiddleLeft, TextAnchor.MiddleLeft.ToString() },
                        { TextAnchor.MiddleCenter, TextAnchor.MiddleCenter.ToString() },
                        { TextAnchor.MiddleRight, TextAnchor.MiddleRight.ToString() },
                        { TextAnchor.LowerLeft, TextAnchor.LowerLeft.ToString() },
                        { TextAnchor.LowerCenter, TextAnchor.LowerCenter.ToString() },
                        { TextAnchor.LowerRight, TextAnchor.LowerRight.ToString() }
                    };

                private static readonly Dictionary<VerticalWrapMode, string> VWMToString =
                    new()
                    {
                        { VerticalWrapMode.Truncate, VerticalWrapMode.Truncate.ToString() },
                        { VerticalWrapMode.Overflow, VerticalWrapMode.Overflow.ToString() },
                    };

                private static readonly Dictionary<Image.Type, string> ImageTypeToString =
                    new()
                    {
                        { Image.Type.Simple, Image.Type.Simple.ToString() },
                        { Image.Type.Sliced, Image.Type.Sliced.ToString() },
                        { Image.Type.Tiled, Image.Type.Tiled.ToString() },
                        { Image.Type.Filled, Image.Type.Filled.ToString() },
                    };

                private static readonly Dictionary<InputField.LineType, string> LineTypeToString =
                    new()
                    {
                        { InputField.LineType.MultiLineNewline, InputField.LineType.MultiLineNewline.ToString() },
                        { InputField.LineType.MultiLineSubmit, InputField.LineType.MultiLineSubmit.ToString() },
                        { InputField.LineType.SingleLine, InputField.LineType.SingleLine.ToString() },
                    };

                private static readonly Dictionary<ScrollRect.MovementType, string> MovementTypeToString =
                    new()
                    {
                        { ScrollRect.MovementType.Unrestricted, ScrollRect.MovementType.Unrestricted.ToString() },
                        { ScrollRect.MovementType.Elastic, ScrollRect.MovementType.Elastic.ToString() },
                        { ScrollRect.MovementType.Clamped, ScrollRect.MovementType.Clamped.ToString() },
                    };


                private static readonly Dictionary<TimerFormat, string> TimerFormatToString =
                    new()
                    {
                        { TimerFormat.None, TimerFormat.None.ToString() },
                        { TimerFormat.SecondsHundreth, TimerFormat.SecondsHundreth.ToString() },
                        { TimerFormat.MinutesSeconds, TimerFormat.MinutesSeconds.ToString() },
                        { TimerFormat.MinutesSecondsHundreth, TimerFormat.MinutesSecondsHundreth.ToString() },
                        { TimerFormat.HoursMinutes, TimerFormat.HoursMinutes.ToString() },
                        { TimerFormat.HoursMinutesSeconds, TimerFormat.HoursMinutesSeconds.ToString() },
                        {
                            TimerFormat.HoursMinutesSecondsMilliseconds,
                            TimerFormat.HoursMinutesSecondsMilliseconds.ToString()
                        },
                        { TimerFormat.HoursMinutesSecondsTenths, TimerFormat.HoursMinutesSecondsTenths.ToString() },
                        { TimerFormat.DaysHoursMinutes, TimerFormat.DaysHoursMinutes.ToString() },
                        { TimerFormat.DaysHoursMinutesSeconds, TimerFormat.DaysHoursMinutesSeconds.ToString() },
                        { TimerFormat.Custom, TimerFormat.Custom.ToString() },
                    };


                public static Color GetColor(string colorStr)
                {
                    return ColorEx.Parse(colorStr);
                }

                public static string GetColorString(Color color)
                {
                    return string.Format("{0} {1} {2} {3}", color.r, color.g, color.b, color.a);
                }

                public static void AddUI(Connection connection, string json)
                {
                    // CHANGE: Migrated from ClientRPCEx to ClientRPC with RpcTarget.Player for latest Rust API compatibility, removed explicit generic argument
                    CommunityEntity.ServerInstance.ClientRPC(RpcTarget.Player("AddUI", connection), json);
                }

                private static void SerializeType(ICuiComponent component, JsonWriter jsonWriter)
                {
                    jsonWriter.WritePropertyName("type");
                    jsonWriter.WriteValue(component.Type);
                }

                private static void SerializeField(string key, object value, object defaultValue, JsonWriter jsonWriter)
                {
                    if (value != null && !value.Equals(defaultValue))
                    {
                        if (value is string && defaultValue != null && string.IsNullOrEmpty(value as string))
                            return;

                        jsonWriter.WritePropertyName(key);

                        if (value is ICuiComponent)
                            SerializeComponent(value as ICuiComponent, jsonWriter);
                        else
                            jsonWriter.WriteValue(value ?? defaultValue);
                    }
                }


                private static void SerializeField(string key, CuiScrollbar scrollbar, JsonWriter jsonWriter)
                {
                    const string defaultHandleSprite = "assets/content/ui/ui.rounded.tga";
                    const string defaultHandleColor = "0.15 0.15 0.15 1";
                    const string defaultHighlightColor = "0.17 0.17 0.17 1";
                    const string defaultPressedColor = "0.2 0.2 0.2 1";
                    const string defaultTrackSprite = "assets/content/ui/ui.background.tile.psd";
                    const string defaultTrackColor = "0.09 0.09 0.09 1";

                    if (scrollbar == null)
                        return;

                    jsonWriter.WritePropertyName(key);
                    jsonWriter.WriteStartObject();
                    SerializeField("invert", scrollbar.Invert, false, jsonWriter);
                    SerializeField("autoHide", scrollbar.AutoHide, false, jsonWriter);
                    SerializeField("handleSprite", scrollbar.HandleSprite, defaultHandleSprite, jsonWriter);
                    SerializeField("size", scrollbar.Size, 20f, jsonWriter);
                    SerializeField("handleColor", scrollbar.HandleColor, defaultHandleColor, jsonWriter);
                    SerializeField("highlightColor", scrollbar.HighlightColor, defaultHighlightColor, jsonWriter);
                    SerializeField("pressedColor", scrollbar.PressedColor, defaultPressedColor, jsonWriter);
                    SerializeField("trackSprite", scrollbar.TrackSprite, defaultTrackSprite, jsonWriter);
                    SerializeField("trackColor", scrollbar.TrackColor, defaultTrackColor, jsonWriter);
                    jsonWriter.WriteEndObject();
                }

                private static void SerializeComponent(ICuiComponent IComponent, JsonWriter jsonWriter)
                {
                    const string vector2zero = "0 0";
                    const string vector2one = "1 1";
                    const string colorWhite = "1 1 1 1";
                    const string backgroundTile = "assets/content/ui/ui.background.tile.psd";
                    const string iconMaterial = "assets/icons/iconmaterial.mat";
                    const string fontBold = "RobotoCondensed-Bold.ttf";
                    const string defaultOutlineDistance = "1.0 -1.0";

                    void SerializeType() => Builder.SerializeType(IComponent, jsonWriter);

                    void SerializeField(string key, object value, object defaultValue) =>
                        Builder.SerializeField(key, value, defaultValue, jsonWriter);

                    void SerializeScrollbar(string key, CuiScrollbar value) =>
                        Builder.SerializeField(key, value, jsonWriter);

                    switch (IComponent.Type)
                    {
                        case "RectTransform":
                        {
                            CuiRectTransformComponent component = IComponent as CuiRectTransformComponent;
                            jsonWriter.WriteStartObject();
                            SerializeType();
                            SerializeField("anchormin", component.AnchorMin, vector2zero);
                            SerializeField("anchormax", component.AnchorMax, vector2one);
                            SerializeField("offsetmin", component.OffsetMin, vector2zero);
                            SerializeField("offsetmax", component.OffsetMax, vector2one);
                            jsonWriter.WriteEndObject();
                            break;
                        }
                        case "UnityEngine.UI.Image":
                        {
                            CuiImageComponent component = IComponent as CuiImageComponent;
                            jsonWriter.WriteStartObject();
                            SerializeType();
                            SerializeField("color", component.Color, colorWhite);
                            SerializeField("sprite", component.Sprite, backgroundTile);
                            SerializeField("material", component.Material, iconMaterial);
                            SerializeField("imagetype", ImageTypeToString[component.ImageType],
                                ImageTypeToString[Image.Type.Simple]);
                            SerializeField("png", component.Png, null);
                            SerializeField("itemid", component.ItemId, 0);
                            SerializeField("skinid", component.SkinId, 0UL);
                            SerializeField("fadeIn", component.FadeIn, 0f);
                            jsonWriter.WriteEndObject();
                            break;
                        }
                        case "UnityEngine.UI.RawImage":
                        {
                            CuiRawImageComponent component = IComponent as CuiRawImageComponent;
                            jsonWriter.WriteStartObject();
                            SerializeType();
                            SerializeField("color", component.Color, colorWhite);
                            SerializeField("sprite", component.Sprite, backgroundTile);
                            SerializeField("material", component.Material, iconMaterial);
                            SerializeField("url", component.Url, null);
                            SerializeField("png", component.Png, null);
                            SerializeField("steamid", component.SteamId, null);
                            SerializeField("fadeIn", component.FadeIn, 0f);
                            jsonWriter.WriteEndObject();
                            break;
                        }
                        case "UnityEngine.UI.Text":
                        {
                            CuiTextComponent component = IComponent as CuiTextComponent;
                            jsonWriter.WriteStartObject();
                            SerializeType();
                            SerializeField("text", component.Text, null);
                            SerializeField("font", component.Font, fontBold);
                            SerializeField("fontSize", component.FontSize, 14);
                            SerializeField("align", TextAnchorToString[component.Align],
                                TextAnchorToString[TextAnchor.UpperLeft]);
                            SerializeField("color", component.Color, colorWhite);
                            SerializeField("verticalOverflow", VWMToString[component.VerticalOverflow],
                                VWMToString[VerticalWrapMode.Truncate]);
                            SerializeField("fadeIn", component.FadeIn, 0f);
                            jsonWriter.WriteEndObject();
                            break;
                        }
                        case "UnityEngine.UI.Button":
                        {
                            CuiButtonComponent component = IComponent as CuiButtonComponent;
                            jsonWriter.WriteStartObject();
                            SerializeType();
                            SerializeField("color", component.Color, colorWhite);
                            SerializeField("sprite", component.Sprite, backgroundTile);
                            SerializeField("material", component.Material, iconMaterial);
                            SerializeField("imagetype", ImageTypeToString[component.ImageType],
                                ImageTypeToString[Image.Type.Simple]);
                            SerializeField("command", component.Command, null);
                            SerializeField("close", component.Close, null);
                            SerializeField("fadeIn", component.FadeIn, 0f);
                            jsonWriter.WriteEndObject();
                            break;
                        }
                        case "UnityEngine.UI.InputField":
                        {
                            CuiInputFieldComponent component = IComponent as CuiInputFieldComponent;
                            jsonWriter.WriteStartObject();
                            SerializeType();
                            SerializeField("text", component.Text, null);
                            SerializeField("font", component.Font, fontBold);
                            SerializeField("fontSize", component.FontSize, 14);
                            SerializeField("align", TextAnchorToString[component.Align],
                                TextAnchorToString[TextAnchor.UpperLeft]);
                            SerializeField("color", component.Color, colorWhite);
                            SerializeField("command", component.Command, null);
                            SerializeField("characterLimit", component.CharsLimit, 0);
                            SerializeField("lineType", LineTypeToString[component.LineType],
                                LineTypeToString[InputField.LineType.SingleLine]);
                            SerializeField("readOnly", component.ReadOnly, false);
                            SerializeField("password", component.IsPassword, false);
                            SerializeField("needsKeyboard", component.NeedsKeyboard, false);
                            SerializeField("hudMenuInput", component.HudMenuInput, false);
                            SerializeField("autofocus", component.Autofocus, false);
                            jsonWriter.WriteEndObject();
                            break;
                        }
                        case "UnityEngine.UI.ScrollView":
                        {
                            CuiScrollViewComponent component = IComponent as CuiScrollViewComponent;
                            jsonWriter.WriteStartObject();
                            SerializeType();
                            SerializeField("contentTransform", component.ContentTransform, null);
                            SerializeField("horizontal", component.Horizontal, false);
                            SerializeField("vertical", component.Vertical, false);
                            SerializeField("movementType", MovementTypeToString[component.MovementType],
                                MovementTypeToString[ScrollRect.MovementType.Clamped]);
                            SerializeField("elasticity", component.Elasticity, 0.1f);
                            SerializeField("inertia", component.Inertia, false);
                            SerializeField("decelerationRate", component.DecelerationRate, 0.135f);
                            SerializeField("scrollSensitivity", component.ScrollSensitivity, 1f);
                            SerializeScrollbar("horizontalScrollbar", component.HorizontalScrollbar);
                            SerializeScrollbar("verticalScrollbar", component.VerticalScrollbar);
                            jsonWriter.WriteEndObject();
                            break;
                        }
                        case "UnityEngine.UI.Outline":
                        {
                            CuiOutlineComponent component = IComponent as CuiOutlineComponent;
                            jsonWriter.WriteStartObject();
                            SerializeType();
                            SerializeField("color", component.Color, colorWhite);
                            SerializeField("distance", component.Distance, defaultOutlineDistance);
                            SerializeField("useGraphicAlpha", component.UseGraphicAlpha, false);
                            jsonWriter.WriteEndObject();
                            break;
                        }
                        case "Countdown":
                        {
                            CuiCountdownComponent component = IComponent as CuiCountdownComponent;
                            jsonWriter.WriteStartObject();
                            SerializeType();
                            SerializeField("endTime", component.EndTime, 0f);
                            SerializeField("startTime", component.StartTime, 0f);
                            SerializeField("step", component.Step, 1f);
                            SerializeField("interval", component.Interval, 1f);
                            SerializeField("timerFormat", TimerFormatToString[component.TimerFormat],
                                TimerFormatToString[TimerFormat.None]);
                            SerializeField("numberFormat", component.NumberFormat, "0.####");
                            SerializeField("destroyIfDone", component.DestroyIfDone, true);
                            SerializeField("command", component.Command, null);
                            SerializeField("fadeIn", component.FadeIn, 0f);
                            jsonWriter.WriteEndObject();
                            break;
                        }
                        case "NeedsKeyboard":
                        case "NeedsCursor":
                        {
                            jsonWriter.WriteStartObject();
                            SerializeType();
                            jsonWriter.WriteEndObject();
                            break;
                        }
                    }
                }


                [JsonObject(MemberSerialization.OptIn)]
                public class Element : CuiElement
                {
                    public new string Name { get; set; } = null;

                    public Element ParentElement { get; set; }
                    public virtual List<Element> Container => ParentElement?.Container;
                    public ComponentList Components { get; set; } = new();

                    [JsonProperty("name")]
                    public string JsonName
                    {
                        get
                        {
                            if (Name == null)
                            {
                                string result = this.GetHashCode().ToString();
                                if (ParentElement != null)
                                    result.Insert(0, ParentElement.JsonName);
                                return result.GetHashCode().ToString();
                            }

                            return Name;
                        }
                    }

                    public Element()
                    {
                    }

                    public Element(Element parent)
                    {
                        AssignParent(parent);
                    }

                    public Builder.Element AssignParent(Element parent)
                    {
                        if (parent == null)
                            return this;

                        ParentElement = parent;
                        Parent = ParentElement.JsonName;
                        return this;
                    }

                    public Element AddDestroy(string elementName)
                    {
                        this.DestroyUi = elementName;
                        return this;
                    }

                    public Element AddDestroySelfAttribute()
                    {
                        return AddDestroy(this.Name);
                    }

                    public virtual void WriteJson(JsonWriter jsonWriter)
                    {
                        jsonWriter.WriteStartObject();
                        jsonWriter.WritePropertyName("name");
                        jsonWriter.WriteValue(this.JsonName);
                        if (!string.IsNullOrEmpty(Parent))
                        {
                            jsonWriter.WritePropertyName("parent");
                            jsonWriter.WriteValue(this.Parent);
                        }

                        if (!string.IsNullOrEmpty(this.DestroyUi))
                        {
                            jsonWriter.WritePropertyName("destroyUi");
                            jsonWriter.WriteValue(this.DestroyUi);
                        }

                        if (this.Update)
                        {
                            jsonWriter.WritePropertyName("update");
                            jsonWriter.WriteValue(this.Update);
                        }

                        if (this.FadeOut > 0f)
                        {
                            jsonWriter.WritePropertyName("fadeOut");
                            jsonWriter.WriteValue(this.FadeOut);
                        }

                        jsonWriter.WritePropertyName("components");
                        jsonWriter.WriteStartArray();
                        for (int i = 0; i < this.Components.Count; i++)
                        {
                            SerializeComponent(this.Components[i], jsonWriter);
                        }

                        jsonWriter.WriteEndArray();
                        jsonWriter.WriteEndObject();
                    }

                    public Element Add(Element element)
                    {
                        if (element.ParentElement == null)
                            element.AssignParent(this);
                        Container.Add(element);
                        return element;
                    }

                    public Element AddEmpty(string name = null)
                    {
                        return Add(new Element(this) { Name = name });
                    }

                    public Element AddUpdateElement(string name = null)
                    {
                        Element element = AddEmpty(name);
                        element.Parent = null;
                        element.Update = true;
                        return element;
                    }

                    public Element AddText(
                        string text,
                        string color = Constants.Defaults.Color,
                        Builder.Font font = Constants.Defaults.Font,
                        int fontSize = Constants.Defaults.FontSize,
                        TextAnchor align = Constants.Defaults.Align,
                        VerticalWrapMode overflow = Constants.Defaults.VerticalOverflow,
                        string anchorMin = Constants.Defaults.VectorZero,
                        string anchorMax = Constants.Defaults.VectorOne,
                        string offsetMin = Constants.Defaults.VectorZero,
                        string offsetMax = Constants.Defaults.VectorZero,
                        string name = null)
                    {
                        return Add(ElementContructor.CreateText(text, color, font, fontSize, align, overflow, anchorMin,
                            anchorMax, offsetMin, offsetMax, name));
                    }

                    public Element AddOutlinedText(
                        string text,
                        string color = Constants.Defaults.Color,
                        Builder.Font font = Constants.Defaults.Font,
                        int fontSize = Constants.Defaults.FontSize,
                        TextAnchor align = Constants.Defaults.Align,
                        VerticalWrapMode overflow = Constants.Defaults.VerticalOverflow,
                        string outlineColor = Constants.Defaults.OutlineColor,
                        int outlineWidth = 1,
                        string anchorMin = Constants.Defaults.VectorZero,
                        string anchorMax = Constants.Defaults.VectorOne,
                        string offsetMin = Constants.Defaults.VectorZero,
                        string offsetMax = Constants.Defaults.VectorZero,
                        string name = null)
                    {
                        return Add(ElementContructor.CreateOutlinedText(text, color, font, fontSize, align, overflow,
                            outlineColor, outlineWidth, anchorMin, anchorMax, offsetMin, offsetMax, name));
                    }

                    public Element AddInputfield(
                        string command = null,
                        string text = "",
                        string color = Constants.Defaults.Color,
                        Builder.Font font = Constants.Defaults.Font,
                        int fontSize = Constants.Defaults.FontSize,
                        TextAnchor align = Constants.Defaults.Align,
                        InputField.LineType lineType = Constants.Defaults.LineType,
                        Builder.InputType inputType = Builder.InputType.Default,
                        bool @readonly = false,
                        bool autoFocus = false,
                        bool isPassword = false,
                        int charsLimit = 0,
                        string anchorMin = Constants.Defaults.VectorZero,
                        string anchorMax = Constants.Defaults.VectorOne,
                        string offsetMin = Constants.Defaults.VectorZero,
                        string offsetMax = Constants.Defaults.VectorZero,
                        string name = null)
                    {
                        return Add(ElementContructor.CreateInputfield(command, text, color, font, fontSize, align,
                            lineType,
                            inputType, @readonly, autoFocus, isPassword, charsLimit, anchorMin, anchorMax, offsetMin,
                            offsetMax, name));
                    }

                    public Element AddPanel(
                        string color = Constants.Defaults.Color,
                        string sprite = Constants.Defaults.Sprite,
                        string material = Constants.Defaults.Material,
                        Image.Type imageType = Constants.Defaults.ImageType,
                        string anchorMin = Constants.Defaults.VectorZero,
                        string anchorMax = Constants.Defaults.VectorOne,
                        string offsetMin = Constants.Defaults.VectorZero,
                        string offsetMax = Constants.Defaults.VectorZero,
                        bool cursorEnabled = false,
                        bool keyboardEnabled = false,
                        string name = null)
                    {
                        return Add(ElementContructor.CreatePanel(color, sprite, material, imageType, anchorMin,
                            anchorMax,
                            offsetMin, offsetMax, cursorEnabled, keyboardEnabled, name));
                    }

                    public Element AddBlurPanel(
                        string anchorMin = Constants.Defaults.VectorZero,
                        string anchorMax = Constants.Defaults.VectorOne,
                        string offsetMin = Constants.Defaults.VectorZero,
                        string offsetMax = Constants.Defaults.VectorZero,
                        bool cursorEnabled = false,
                        bool keyboardEnabled = false,
                        string name = null,
                        string color = Constants.Colors.Blur_07)
                    {
                        string baseName = string.IsNullOrEmpty(name) ? Guid.NewGuid().ToString() : name;

                        // зададим родительскую панель для корректной работы .Destroy на BlurPanel
                        Element parent = ElementContructor.CreatePanel(
                            color: Constants.Colors.Transparent,
                            anchorMin: anchorMin,
                            anchorMax: anchorMax,
                            offsetMin: offsetMin,
                            offsetMax: offsetMax,
                            cursorEnabled: cursorEnabled,
                            keyboardEnabled: keyboardEnabled,
                            name: baseName
                        );
                        Add(parent);

                        for (int i = 1; i <= 6; i++)
                        {
                            Element layer = ElementContructor.CreatePanel(
                                color: Constants.Colors.Blur_07,
                                material: (i % 2 == 1)
                                    ? Constants.Materials.BackgroundBlur
                                    : Constants.Materials.InGameBlur,
                                anchorMin: anchorMin,
                                anchorMax: anchorMax,
                                offsetMin: offsetMin,
                                offsetMax: offsetMax,
                                cursorEnabled: cursorEnabled,
                                keyboardEnabled: keyboardEnabled,
                                name: $"{baseName}_{i}"
                            );
                            parent.Add(layer);
                        }

                        return parent;
                    }

                    public Element AddButton(
                        string command = null,
                        string close = null,
                        string color = Constants.Defaults.Color,
                        string sprite = Constants.Defaults.Sprite,
                        string material = Constants.Defaults.Material,
                        Image.Type imageType = Constants.Defaults.ImageType,
                        string anchorMin = Constants.Defaults.VectorZero,
                        string anchorMax = Constants.Defaults.VectorOne,
                        string offsetMin = Constants.Defaults.VectorZero,
                        string offsetMax = Constants.Defaults.VectorZero,
                        string name = null)
                    {
                        return Add(ElementContructor.CreateButton(command, close, color, sprite, material, imageType,
                            anchorMin, anchorMax, offsetMin, offsetMax, name));
                    }

                    public Element AddImage(
                        string content,
                        string color = Constants.Defaults.Color,
                        string material = null,
                        string anchorMin = Constants.Defaults.VectorZero,
                        string anchorMax = Constants.Defaults.VectorOne,
                        string offsetMin = Constants.Defaults.VectorZero,
                        string offsetMax = Constants.Defaults.VectorZero,
                        string name = null,
                        string sprite = null)
                    {
                        return Add(ElementContructor.CreateImage(content, color, material, anchorMin, anchorMax,
                            offsetMin,
                            offsetMax, name, sprite));
                    }

                    public Element AddHImage(
                        string content,
                        string color = Constants.Defaults.Color,
                        string anchorMin = Constants.Defaults.VectorZero,
                        string anchorMax = Constants.Defaults.VectorOne,
                        string offsetMin = Constants.Defaults.VectorZero,
                        string offsetMax = Constants.Defaults.VectorZero,
                        string name = null)
                    {
                        return AddImage(content, color, Constants.Defaults.IconMaterial, anchorMin, anchorMax,
                            offsetMin,
                            offsetMax,
                            name);
                    }

                    public Element AddIcon(
                        int itemId,
                        ulong skin = 0,
                        string color = Constants.Defaults.Color,
                        string sprite = Constants.Defaults.Sprite,
                        string material = Constants.Defaults.IconMaterial,
                        Image.Type imageType = Constants.Defaults.ImageType,
                        string anchorMin = Constants.Defaults.VectorZero,
                        string anchorMax = Constants.Defaults.VectorOne,
                        string offsetMin = Constants.Defaults.VectorZero,
                        string offsetMax = Constants.Defaults.VectorZero,
                        string name = null)
                    {
                        return Add(ElementContructor.CreateIcon(itemId, skin, color, sprite, material, imageType,
                            anchorMin,
                            anchorMax, offsetMin, offsetMax, name));
                    }

                    public Element AddContainer(
                        string anchorMin = Constants.Defaults.VectorZero,
                        string anchorMax = Constants.Defaults.VectorOne,
                        string offsetMin = Constants.Defaults.VectorZero,
                        string offsetMax = Constants.Defaults.VectorZero,
                        string name = null)
                    {
                        return Add(ElementContructor.CreateContainer(anchorMin, anchorMax, offsetMin, offsetMax, name));
                    }

                    public Element WithRect(
                        string anchorMin = Constants.Defaults.VectorZero,
                        string anchorMax = Constants.Defaults.VectorOne,
                        string offsetMin = Constants.Defaults.VectorZero,
                        string offsetMax = Constants.Defaults.VectorZero)
                    {
                        if (this.Components.Count > 0)
                            this.Components.RemoveAll(c => c is CuiRectTransformComponent);
                        this.Components.Add(new CuiRectTransformComponent()
                        {
                            AnchorMin = anchorMin,
                            AnchorMax = anchorMax,
                            OffsetMin = offsetMin,
                            OffsetMax = offsetMax
                        });
                        return this;
                    }

                    public Builder.Element WithFade(
                        float @in = 0f,
                        float @out = 0f)
                    {
                        this.FadeOut = @out;
                        foreach (ICuiComponent component in this.Components)
                        {
                            if (component is CuiRawImageComponent rawImage)
                                rawImage.FadeIn = @in;
                            else if (component is CuiImageComponent image)
                                image.FadeIn = @in;
                            else if (component is CuiButtonComponent button)
                                button.FadeIn = @in;
                            else if (component is CuiTextComponent text)
                                text.FadeIn = @in;
                            else if (component is CuiCountdownComponent countdown)
                                countdown.FadeIn = @in;
                        }

                        return this;
                    }

                    public void AddComponents(params ICuiComponent[] components)
                    {
                        this.Components.AddRange(components);
                    }

                    public Builder.Element WithComponents(params ICuiComponent[] components)
                    {
                        AddComponents(components);
                        return this;
                    }

                    public Builder.Element CreateChild(string name = null, params ICuiComponent[] components)
                    {
                        return Builder.Element.Create(name, components).AssignParent(this);
                    }

                    public static Builder.Element Create(string name = null, params ICuiComponent[] components)
                    {
                        return new Builder.Element()
                        {
                            Name = name
                        }.WithComponents(components);
                    }

                    public class ComponentList : List<ICuiComponent>
                    {
                        private Dictionary<Type, ICuiComponent> typeToComponent = new();

                        public T Get<T>() where T : ICuiComponent
                        {
                            if (typeToComponent.TryGetValue(typeof(T), out ICuiComponent component))
                                return (T)component;
                            return default(T);
                        }

                        public new void Add(ICuiComponent item)
                        {
                            base.Add(item);
                            typeToComponent.Add(item.GetType(), item);
                        }

                        public new void Remove(ICuiComponent item)
                        {
                            base.Remove(item);
                            typeToComponent.Remove(item.GetType());
                        }

                        public new void Clear()
                        {
                            base.Clear();
                            typeToComponent.Clear();
                        }


                        public ComponentList AddImage(
                            string color = Constants.Defaults.Color,
                            string sprite = Constants.Defaults.Sprite,
                            string material = Constants.Defaults.Material,
                            Image.Type imageType = Constants.Defaults.ImageType,
                            int itemId = 0,
                            ulong skinId = 0UL)
                        {
                            Add(new CuiImageComponent
                            {
                                Color = color,
                                Sprite = sprite,
                                Material = material,
                                ImageType = imageType,
                                ItemId = itemId,
                                SkinId = skinId,
                            });
                            return this;
                        }

                        public ComponentList AddRawImage(
                            string content,
                            string color = Constants.Defaults.Color,
                            string sprite = Constants.Defaults.Sprite,
                            string material = Constants.Defaults.IconMaterial)
                        {
                            CuiRawImageComponent rawImageComponent = new CuiRawImageComponent
                            {
                                Color = color,
                                Sprite = sprite,
                                Material = material,
                            };
                            if (!string.IsNullOrEmpty(content))
                            {
                                if (content.Contains("://"))
                                    rawImageComponent.Url = content;
                                else if (content.IsNumeric())
                                {
                                    if (content.IsSteamId())
                                        rawImageComponent.SteamId = content;
                                    else
                                        rawImageComponent.Png = content;
                                }
                            }

                            Add(rawImageComponent);
                            return this;
                        }

                        public ComponentList AddButton(
                            string command = null,
                            string close = null,
                            string color = Constants.Defaults.Color,
                            string sprite = Constants.Defaults.Sprite,
                            string material = Constants.Defaults.Material,
                            Image.Type imageType = Constants.Defaults.ImageType)
                        {
                            Add(new CuiButtonComponent
                            {
                                Command = command,
                                Close = close,
                                Color = color,
                                Sprite = sprite,
                                Material = material,
                                ImageType = imageType,
                            });
                            return this;
                        }

                        public ComponentList AddText(
                            string text,
                            string color = Constants.Defaults.Color,
                            Builder.Font font = Constants.Defaults.Font,
                            int fontSize = Constants.Defaults.FontSize,
                            TextAnchor align = Constants.Defaults.Align,
                            VerticalWrapMode overflow = Constants.Defaults.VerticalOverflow)
                        {
                            Add(new CuiTextComponent
                            {
                                Text = text,
                                Color = color,
                                Font = FontToString[font],
                                FontSize = fontSize,
                                Align = align,
                                VerticalOverflow = overflow
                            });
                            return this;
                        }

                        public ComponentList AddInputfield(
                            string command = null,
                            string text = "",
                            string color = Constants.Defaults.Color,
                            Builder.Font font = Constants.Defaults.Font,
                            int fontSize = Constants.Defaults.FontSize,
                            TextAnchor align = Constants.Defaults.Align,
                            InputField.LineType lineType = Constants.Defaults.LineType,
                            Builder.InputType inputType = Builder.InputType.Default,
                            bool @readonly = false,
                            bool autoFocus = false,
                            bool isPassword = false,
                            int charsLimit = 0)
                        {
                            Add(new CuiInputFieldComponent
                            {
                                Command = command,
                                Text = text,
                                Color = color,
                                Font = FontToString[font],
                                FontSize = fontSize,
                                Align = align,
                                NeedsKeyboard = inputType == InputType.Default,
                                HudMenuInput = inputType == InputType.HudMenuInput,
                                Autofocus = autoFocus,
                                ReadOnly = @readonly,
                                CharsLimit = charsLimit,
                                IsPassword = isPassword,
                                LineType = lineType
                            });
                            return this;
                        }

                        public ComponentList AddScrollView(
                            bool horizontal = false,
                            CuiScrollbar horizontalScrollbar = null,
                            bool vertical = false,
                            CuiScrollbar verticalScrollbar = null,
                            bool inertia = false,
                            ScrollRect.MovementType movementType = ScrollRect.MovementType.Clamped,
                            float decelerationRate = 0.135f,
                            float elasticity = 0.1f,
                            float scrollSensitivity = 1f,
                            string anchorMin = "0 0",
                            string anchorMax = "1 1",
                            string offsetMin = "0 0",
                            string offsetMax = "0 0")
                        {
                            Add(new CuiScrollViewComponent()
                            {
                                ContentTransform =
                                    new CuiRectTransformComponent()
                                    {
                                        AnchorMin = anchorMin,
                                        AnchorMax = anchorMax,
                                        OffsetMin = offsetMin,
                                        OffsetMax = offsetMax
                                    },
                                Horizontal = horizontal,
                                HorizontalScrollbar = horizontalScrollbar,
                                Vertical = vertical,
                                VerticalScrollbar = verticalScrollbar,
                                Inertia = inertia,
                                DecelerationRate = decelerationRate,
                                Elasticity = elasticity,
                                ScrollSensitivity = scrollSensitivity,
                                MovementType = movementType,
                            });
                            return this;
                        }

                        public ComponentList AddOutline(
                            string color = Constants.Defaults.OutlineColor,
                            int width = 1)
                        {
                            Add(new CuiOutlineComponent
                            {
                                Color = color,
                                Distance = string.Format("{0} -{0}", width)
                            });
                            return this;
                        }

                        public ComponentList AddNeedsKeyboard()
                        {
                            Add(new CuiNeedsKeyboardComponent());
                            return this;
                        }

                        public ComponentList AddNeedsCursor()
                        {
                            Add(new CuiNeedsCursorComponent());
                            return this;
                        }

                        public ComponentList AddCountdown(
                            string command = null,
                            float endTime = 0,
                            float startTime = 0,
                            float step = 1,
                            float interval = 1f,
                            TimerFormat timerFormat = TimerFormat.None,
                            string numberFormat = "0.####",
                            bool destroyIfDone = true)
                        {
                            Add(new CuiCountdownComponent
                            {
                                Command = command,
                                EndTime = endTime,
                                StartTime = startTime,
                                Step = step,
                                Interval = interval,
                                TimerFormat = timerFormat,
                                NumberFormat = numberFormat,
                                DestroyIfDone = destroyIfDone
                            });
                            return this;
                        }
                    }
                }

                public static class ElementContructor
                {
                    public static Builder.Element CreateText(
                        string text,
                        string color = Constants.Defaults.Color,
                        Builder.Font font = Constants.Defaults.Font,
                        int fontSize = Constants.Defaults.FontSize,
                        TextAnchor align = Constants.Defaults.Align,
                        VerticalWrapMode overflow = Constants.Defaults.VerticalOverflow,
                        string anchorMin = Constants.Defaults.VectorZero,
                        string anchorMax = Constants.Defaults.VectorOne,
                        string offsetMin = Constants.Defaults.VectorZero,
                        string offsetMax = Constants.Defaults.VectorZero,
                        string name = null)
                    {
                        Builder.Element element = CreateContainer(anchorMin, anchorMax, offsetMin, offsetMax, name);
                        element.Components.AddText(text, color, font, fontSize, align, overflow);
                        return element;
                    }

                    public static Builder.Element CreateOutlinedText(
                        string text,
                        string color = Constants.Defaults.Color,
                        Builder.Font font = Constants.Defaults.Font,
                        int fontSize = Constants.Defaults.FontSize,
                        TextAnchor align = Constants.Defaults.Align,
                        VerticalWrapMode overflow = Constants.Defaults.VerticalOverflow,
                        string outlineColor = Constants.Defaults.OutlineColor,
                        int outlineWidth = 1,
                        string anchorMin = Constants.Defaults.VectorZero,
                        string anchorMax = Constants.Defaults.VectorOne,
                        string offsetMin = Constants.Defaults.VectorZero,
                        string offsetMax = Constants.Defaults.VectorZero,
                        string name = null)
                    {
                        Builder.Element element = CreateText(text, color, font, fontSize, align, overflow, anchorMin,
                            anchorMax,
                            offsetMin, offsetMax, name);
                        element.Components.AddOutline(outlineColor, outlineWidth);
                        return element;
                    }

                    public static Builder.Element CreateInputfield(
                        string command = null,
                        string text = "",
                        string color = Constants.Defaults.Color,
                        Builder.Font font = Constants.Defaults.Font,
                        int fontSize = Constants.Defaults.FontSize,
                        TextAnchor align = Constants.Defaults.Align,
                        InputField.LineType lineType = Constants.Defaults.LineType,
                        Builder.InputType inputType = Builder.InputType.Default,
                        bool @readonly = false,
                        bool autoFocus = false,
                        bool isPassword = false,
                        int charsLimit = 0,
                        string anchorMin = Constants.Defaults.VectorZero,
                        string anchorMax = Constants.Defaults.VectorOne,
                        string offsetMin = Constants.Defaults.VectorZero,
                        string offsetMax = Constants.Defaults.VectorZero,
                        string name = null)
                    {
                        Builder.Element element = CreateContainer(anchorMin, anchorMax, offsetMin, offsetMax, name);
                        element.Components.AddInputfield(command, text, color, font, fontSize, align, lineType,
                            inputType,
                            @readonly, autoFocus, isPassword, charsLimit);
                        return element;
                    }

                    public static Builder.Element CreateButton(
                        string command = null,
                        string close = null,
                        string color = Constants.Defaults.Color,
                        string sprite = Constants.Defaults.Sprite,
                        string material = Constants.Defaults.Material,
                        Image.Type imageType = Constants.Defaults.ImageType,
                        string anchorMin = Constants.Defaults.VectorZero,
                        string anchorMax = Constants.Defaults.VectorOne,
                        string offsetMin = Constants.Defaults.VectorZero,
                        string offsetMax = Constants.Defaults.VectorZero,
                        string name = null)
                    {
                        Builder.Element element = CreateContainer(anchorMin, anchorMax, offsetMin, offsetMax, name);
                        element.Components.AddButton(command, close, color, sprite, material, imageType);
                        return element;
                    }

                    public static Builder.Element CreatePanel(
                        string color = Constants.Defaults.Color,
                        string sprite = Constants.Defaults.Sprite,
                        string material = Constants.Defaults.Material,
                        Image.Type imageType = Constants.Defaults.ImageType,
                        string anchorMin = Constants.Defaults.VectorZero,
                        string anchorMax = Constants.Defaults.VectorOne,
                        string offsetMin = Constants.Defaults.VectorZero,
                        string offsetMax = Constants.Defaults.VectorZero,
                        bool cursorEnabled = false,
                        bool keyboardEnabled = false,
                        string name = null)
                    {
                        Element element = CreateContainer(anchorMin, anchorMax, offsetMin, offsetMax, name);
                        element.Components.AddImage(color, sprite, material, imageType);
                        if (cursorEnabled)
                            element.Components.AddNeedsCursor();
                        if (keyboardEnabled)
                            element.Components.AddNeedsKeyboard();
                        return element;
                    }

                    public static Builder.Element CreateImage(
                        string content,
                        string color = Constants.Defaults.Color,
                        string material = null,
                        string anchorMin = Constants.Defaults.VectorZero,
                        string anchorMax = Constants.Defaults.VectorOne,
                        string offsetMin = Constants.Defaults.VectorZero,
                        string offsetMax = Constants.Defaults.VectorZero,
                        string name = null,
                        string sprite = null)
                    {
                        Element element = CreateContainer(anchorMin, anchorMax, offsetMin, offsetMax, name);
                        element.Components.AddRawImage(content, color, sprite: sprite, material: material);
                        return element;
                    }

                    public static Builder.Element CreateIcon(
                        int itemId,
                        ulong skin = 0,
                        string color = Constants.Defaults.Color,
                        string sprite = Constants.Defaults.Sprite,
                        string material = Constants.Defaults.IconMaterial,
                        Image.Type imageType = Constants.Defaults.ImageType,
                        string anchorMin = Constants.Defaults.VectorZero,
                        string anchorMax = Constants.Defaults.VectorOne,
                        string offsetMin = Constants.Defaults.VectorZero,
                        string offsetMax = Constants.Defaults.VectorZero,
                        string name = null)
                    {
                        Element element = CreateContainer(anchorMin, anchorMax, offsetMin, offsetMax, name);
                        element.Components.AddImage(color, sprite, material, imageType, itemId, skin);
                        return element;
                    }

                    public static Element CreateContainer(
                        string anchorMin = Constants.Defaults.VectorZero,
                        string anchorMax = Constants.Defaults.VectorOne,
                        string offsetMin = Constants.Defaults.VectorZero,
                        string offsetMax = Constants.Defaults.VectorZero,
                        string name = null)
                    {
                        return Element.Create(name).WithRect(anchorMin, anchorMax, offsetMin, offsetMax);
                    }
                }


                public class Root : Element
                {
                    public bool wasRendered = false;
                    private static StringBuilder stringBuilder = new();

                    public Root()
                    {
                        Name = string.Empty;
                    }

                    public Root(string rootObjectName = "Overlay")
                    {
                        Name = rootObjectName;
                    }

                    public override List<Element> Container { get; } = new();

                    public string ToJson(List<Element> elements)
                    {
                        stringBuilder.Clear();
                        try
                        {
                            using (StringWriter stringWriter = new StringWriter(stringBuilder))
                            {
                                using (JsonWriter jsonWriter = new JsonTextWriter(stringWriter))
                                {
                                    jsonWriter.WriteStartArray();
                                    foreach (Element element in elements)
                                        element.WriteJson(jsonWriter);
                                    jsonWriter.WriteEndArray();
                                }
                            }

                            return stringBuilder.ToString().Replace("\\n", "\n");
                        }
                        catch (Exception ex)
                        {
                            UnityEngine.Debug.LogError(ex.Message + "\n" + ex.StackTrace);
                            return string.Empty;
                        }
                    }

                    public string ToJson()
                    {
                        return ToJson(Container);
                    }

                    public void Render(Connection connection)
                    {
                        if (connection == null || !connection.connected)
                            return;

                        wasRendered = true;
                        Builder.AddUI(connection, ToJson(Container));
                    }

                    public void Render(BasePlayer player)
                    {
                        Render(player.Connection);
                    }

                    public void Update(Connection connection)
                    {
                        foreach (Element element in Container)
                            element.Update = true;
                        Builder.AddUI(connection, ToJson(Container));
                    }

                    public void Update(BasePlayer player)
                    {
                        Update(player.Connection);
                    }
                }
            }
        }
    }

    #endregion
}

#region LINQ

namespace Oxide.Plugins.BattlePassRebornExtensionMethods
{
    public static class ExtensionMethods
    {
        public static bool Any<TSource>(this IEnumerable<TSource> source, Func<TSource, bool> predicate)
        {
            using (var enumerator = source.GetEnumerator())
                while (enumerator.MoveNext())
                    if (predicate(enumerator.Current))
                        return true;
            return false;
        }

        public static float Sum<TSource>(this IEnumerable<TSource> source, Func<TSource, float> selector)
        {
            float sum = 0f;
            using (var enumerator = source.GetEnumerator())
            {
                while (enumerator.MoveNext())
                {
                    sum += selector(enumerator.Current);
                }
            }
            return sum;
        }

        public static float Sum(this IEnumerable<float> source)
        {
            float sum = 0f;
            using (var enumerator = source.GetEnumerator())
            {
                while (enumerator.MoveNext())
                {
                    sum += enumerator.Current;
                }
            }
            return sum;
        }

        public static double Sum<TSource>(this IEnumerable<TSource> source, Func<TSource, double> selector)
        {
            double sum = 0.0;
            using (var enumerator = source.GetEnumerator())
            {
                while (enumerator.MoveNext())
                {
                    sum += selector(enumerator.Current);
                }
            }
            return sum;
        }

        public static double Sum(this IEnumerable<double> source)
        {
            double sum = 0.0;
            using (var enumerator = source.GetEnumerator())
            {
                while (enumerator.MoveNext())
                {
                    sum += enumerator.Current;
                }
            }
            return sum;
        }

        public static bool Any<TSource>(this IEnumerable<TSource> source)
        {
            using (var enumerator = source.GetEnumerator())
            {
                return enumerator.MoveNext();
            }
        }

        public static List<TSource> Take<TSource>(this IEnumerable<TSource> source, int count)
        {
            if (count <= 0)
                return new List<TSource>();

            List<TSource> result = new List<TSource>();
            int taken = 0;

            using (var enumerator = source.GetEnumerator())
            {
                while (enumerator.MoveNext() && taken < count)
                {
                    result.Add(enumerator.Current);
                    taken++;
                }
            }

            return result;
        }

        public static List<TSource> Concat<TSource>(this IEnumerable<TSource> first, IEnumerable<TSource> second)
        {
            List<TSource> result = new List<TSource>();

            using (var enumerator = first.GetEnumerator())
                while (enumerator.MoveNext())
                    result.Add(enumerator.Current);

            using (var enumerator = second.GetEnumerator())
                while (enumerator.MoveNext())
                    result.Add(enumerator.Current);

            return result;
        }

        public static TSource LastOrDefault<TSource>(this IList<TSource> source)
        {
            return source.Count > 0 ? source[source.Count - 1] : default(TSource);
        }

        public static TSource LastOrDefault<TSource>(this IEnumerable<TSource> source)
        {
            using (var enumerator = source.GetEnumerator())
            {
                if (!enumerator.MoveNext())
                    return default(TSource);

                TSource last = enumerator.Current;
                while (enumerator.MoveNext())
                    last = enumerator.Current;

                return last;
            }
        }

        public static Dictionary<TKey, TSource> ToDictionary<TSource, TKey>(this IEnumerable<TSource> source,
            Func<TSource, TKey> keySelector)
        {
            var dictionary = new Dictionary<TKey, TSource>();
            using (var enumerator = source.GetEnumerator())
            {
                while (enumerator.MoveNext())
                {
                    TSource item = enumerator.Current;
                    TKey key = keySelector(item);
                    dictionary[key] = item;
                }
            }

            return dictionary;
        }

        public static Dictionary<TKey, TValue> ToDictionary<TSource, TKey, TValue>(this IEnumerable<TSource> source,
            Func<TSource, TKey> keySelector, Func<TSource, TValue> valueSelector)
        {
            var dictionary = new Dictionary<TKey, TValue>();
            using (var enumerator = source.GetEnumerator())
            {
                while (enumerator.MoveNext())
                {
                    TSource item = enumerator.Current;
                    TKey key = keySelector(item);
                    TValue value = valueSelector(item);
                    dictionary[key] = value;
                }
            }

            return dictionary;
        }

        public static HashSet<TSource> Where<TSource>(this IEnumerable<TSource> source, Func<TSource, bool> predicate)
        {
            HashSet<TSource> result = new HashSet<TSource>();

            using (var enumerator = source.GetEnumerator())
                while (enumerator.MoveNext())
                    if (predicate(enumerator.Current))
                        result.Add(enumerator.Current);
            return result;
        }

        public static List<TSource> WhereList<TSource>(this IEnumerable<TSource> source, Func<TSource, bool> predicate)
        {
            List<TSource> result = new List<TSource>();
            using (var enumerator = source.GetEnumerator())
                while (enumerator.MoveNext())
                    if (predicate(enumerator.Current))
                        result.Add(enumerator.Current);
            return result;
        }

        public static TSource FirstOrDefault<TSource>(this IEnumerable<TSource> source, Func<TSource, bool> predicate)
        {
            using (var enumerator = source.GetEnumerator())
                while (enumerator.MoveNext())
                    if (predicate(enumerator.Current))
                        return enumerator.Current;
            return default(TSource);
        }

        public static HashSet<TResult> Select<TSource, TResult>(this IEnumerable<TSource> source,
            Func<TSource, TResult> predicate)
        {
            HashSet<TResult> result = new HashSet<TResult>();
            using (var enumerator = source.GetEnumerator())
                while (enumerator.MoveNext())
                    result.Add(predicate(enumerator.Current));
            return result;
        }

        public static List<TResult> Select<TSource, TResult>(this IList<TSource> source,
            Func<TSource, TResult> predicate)
        {
            List<TResult> result = new List<TResult>();
            for (int i = 0; i < source.Count; i++)
            {
                TSource element = source[i];
                result.Add(predicate(element));
            }

            return result;
        }

        public static bool IsExists(this BaseNetworkable entity) => entity != null && !entity.IsDestroyed;

        public static bool IsRealPlayer(this BasePlayer player) => player != null && player.userID.IsSteamId();

        public static List<TSource> OrderBy<TSource>(this IEnumerable<TSource> source, Func<TSource, float> predicate)
        {
            List<TSource> result = source.ToList();
            for (int i = 0; i < result.Count; i++)
            {
                for (int j = 0; j < result.Count - 1; j++)
                {
                    if (predicate(result[j]) > predicate(result[j + 1]))
                    {
                        TSource z = result[j];
                        result[j] = result[j + 1];
                        result[j + 1] = z;
                    }
                }
            }

            return result;
        }

        public static List<TSource> ToList<TSource>(this IEnumerable<TSource> source)
        {
            List<TSource> result = new List<TSource>();
            using (var enumerator = source.GetEnumerator())
                while (enumerator.MoveNext())
                    result.Add(enumerator.Current);
            return result;
        }

        public static List<TSource> Shuffle<TSource>(this IEnumerable<TSource> source)
        {
            List<TSource> result = source.ToList();

            for (int i = 0; i < result.Count; i++)
            {
                int j = UnityEngine.Random.Range(0, i + 1);
                var temp = result[j];
                result[j] = result[i];
                result[i] = temp;
            }

            return result;
        }

        public static HashSet<TSource> ToHashSet<TSource>(this IEnumerable<TSource> source)
        {
            HashSet<TSource> result = new HashSet<TSource>();
            using (var enumerator = source.GetEnumerator())
                while (enumerator.MoveNext())
                    result.Add(enumerator.Current);
            return result;
        }

        public static HashSet<T> OfType<T>(this IEnumerable<BaseNetworkable> source)
        {
            HashSet<T> result = new HashSet<T>();
            using (var enumerator = source.GetEnumerator())
                while (enumerator.MoveNext())
                    if (enumerator.Current is T)
                        result.Add((T)(object)enumerator.Current);
            return result;
        }

        public static TSource Max<TSource>(this IEnumerable<TSource> source, Func<TSource, float> predicate)
        {
            TSource result = source.ElementAt(0);
            float resultValue = predicate(result);
            using (var enumerator = source.GetEnumerator())
            {
                while (enumerator.MoveNext())
                {
                    TSource element = enumerator.Current;
                    float elementValue = predicate(element);
                    if (elementValue > resultValue)
                    {
                        result = element;
                        resultValue = elementValue;
                    }
                }
            }

            return result;
        }

        public static TSource Min<TSource>(this IEnumerable<TSource> source, Func<TSource, float> predicate)
        {
            TSource result = source.ElementAt(0);
            float resultValue = predicate(result);
            using (var enumerator = source.GetEnumerator())
            {
                while (enumerator.MoveNext())
                {
                    TSource element = enumerator.Current;
                    float elementValue = predicate(element);
                    if (elementValue < resultValue)
                    {
                        result = element;
                        resultValue = elementValue;
                    }
                }
            }

            return result;
        }

        public static TSource ElementAt<TSource>(this IEnumerable<TSource> source, int index)
        {
            int movements = 0;
            using (var enumerator = source.GetEnumerator())
            {
                while (enumerator.MoveNext())
                {
                    if (movements == index) return enumerator.Current;
                    movements++;
                }
            }

            return default(TSource);
        }

        public static TSource First<TSource>(this IList<TSource> source) => source[0];

        public static TSource Last<TSource>(this IList<TSource> source) => source[source.Count - 1];

        public static bool IsEqualVector3(this Vector3 a, Vector3 b) => Vector3.Distance(a, b) < 0.1f;

        public static List<TSource> OrderByQuickSort<TSource>(this List<TSource> source, Func<TSource, float> predicate)
        {
            return source.QuickSort(predicate, 0, source.Count - 1);
        }

        private static List<TSource> QuickSort<TSource>(this List<TSource> source, Func<TSource, float> predicate,
            int minIndex, int maxIndex)
        {
            if (minIndex >= maxIndex) return source;

            int pivotIndex = minIndex - 1;
            for (int i = minIndex; i < maxIndex; i++)
            {
                if (predicate(source[i]) < predicate(source[maxIndex]))
                {
                    pivotIndex++;
                    source.Replace(pivotIndex, i);
                }
            }

            pivotIndex++;
            source.Replace(pivotIndex, maxIndex);

            QuickSort(source, predicate, minIndex, pivotIndex - 1);
            QuickSort(source, predicate, pivotIndex + 1, maxIndex);

            return source;
        }

        private static void Replace<TSource>(this IList<TSource> source, int x, int y)
        {
            TSource t = source[x];
            source[x] = source[y];
            source[y] = t;
        }
    }
}

#endregion