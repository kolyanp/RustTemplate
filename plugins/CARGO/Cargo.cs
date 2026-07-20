// Requires: Npc15
// Reference: Rust.Platform.Steam
using Facepunch;
using Facepunch.Math;
using HarmonyLib;
using MySqlConnector;
using Network;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Database;
using Oxide.Core.Libraries;
using Oxide.Core.Plugins;
using Oxide.Game.Rust.Cui;
using Rust.Platform.Steam;
using Steamworks;
using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using UnityEngine;
using UnityEngine.UI;
using Connection = Network.Connection;
using ConnectionState = System.Data.ConnectionState;
using LT = Oxide.Plugins.Cargo.LocalizationText;
using Epoch = Facepunch.Math.Epoch;
using ProtoBuf;

namespace Oxide.Plugins
{
    [Info("Cargo", "0xF_fixKolyan", "1.1.4")]
    partial class Cargo : RustPlugin
    {
        #region References
        [PluginReference]
        private Plugin Economics;
        #endregion

        #region Consts
        public const string PERMISSSION_ADMIN = "cargo.admin";
        #endregion

        #region Variables
        private static Cargo PluginInstance;
        private static MapImage mapImage;
        public static ItemCategory[] ValidCategories { get; private set; }
        private List<CargoNPC> NPCs = new List<CargoNPC>();
        private bool serverInitialized = false;

        #region Database
        private Oxide.Core.MySql.Libraries.MySql databaseProvider;
        private Oxide.Core.Database.Connection databaseConnection;
        #endregion

        #region Servers
        private Server currentServer;
        private Dictionary<int, Server> serverList = new Dictionary<int, Server>();
        #endregion

        #region Collections
        public PlayerAssociatedCollection<BasePlayer, Player> playerCollection = new PlayerAssociatedCollection<BasePlayer, Player>();
        private PlayerIDAssociatedCollection<Translator15> translators = new PlayerIDAssociatedCollection<Translator15>();
        #endregion

        #region LocalizationText
        private static LocalizationText ФОРМАТ_УВЕДОМЛЕНИЕ_ПРИБЫЛ_ГРУЗ = "Только что был доставлен груз с сервера \"{0}\" на сервер \"{1}\"";
        private static LocalizationText НЕДОСТАТОЧНО_СРЕДСТВ_ДЛЯ_ОПЛАТЫ_СТОИМОСТИ_ХРАНЕНИЯ = "Недостаточно средств для оплаты стоимости хранения за слот.";
        private static LocalizationText УДАЛИТЬ_КОНТЕЙНЕР_С_ПРЕДМЕТАМИ = "В контейнере есть предметы!\nДля удаления контейнера вместе с ними - нажмите кнопку удаления ещё раз";
        private static LocalizationText НЕЛЬЗЯ_ПЕРЕМЕЩАТЬ_ПРЕДМЕТЫ = "Нельзя перемещать предметы пока контейнер в пути!";
        #endregion
        #endregion

        #region Enums
        public enum ContainerState
        {
            Idle,
            InTransit,
            Delivered,
            Completed
        }

        public enum DeliveryVariant
        {
            Standard,
            Express
        }

        public enum RemappingType
        {
            Reset,
            New
        }

        public enum Currency
        {
            Coins,
            Scrap
        }

        #endregion

        #region Interfaces
        public interface IPaymentProvider
        {
            float GetBalance(BasePlayer player);
            bool AddBalance(BasePlayer player, float amount);
            bool TakeBalance(BasePlayer player, float amount, List<Item> collect);
        }
        #endregion

        #region Hooks
        void Init()
        {
            PluginInstance = this;

            permission.RegisterPermission(PERMISSSION_ADMIN, this);
        }

        void OnServerInitialized(bool initial)
        {
            serverInitialized = true;

            if (Economics == null)
            {
                PrintError("Economics plugin missing!");
                Interface.Oxide.UnloadPlugin(this.Name);
                return;
            }

            PopulateMapImageCache();

            if (!InitializeDatabase())
            {
                Interface.Oxide.UnloadPlugin(this.Name);
                return;
            }

            // Получаем категории что используются в предметах
            ValidCategories = Enum.GetValues(typeof(ItemCategory)).Cast<ItemCategory>().Where(category => ItemManager.itemList.FirstOrDefault(x => x.category == category)).ToArray();

            timer.Every(5f, CheckDatabase);
            PrewarmLabels();

            SpawnNPC();
        }

        void Unload()
        {
            Npc15.DespawnAll<CargoNPC>();

            playerCollection.Clear();
        }

        void Loaded()
        {
            foreach (Type type in this.GetType().GetNestedTypes(BindingFlags.DeclaredOnly | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            {
                object[] attribute = type.GetCustomAttributes(typeof(HarmonyPatch), false);
                if (attribute.Length >= 1)
                {
                    PatchClassProcessor patchClassProcessor = this.HarmonyInstance.CreateClassProcessor(type);
                    patchClassProcessor.Patch();
                }
            }
        }

        protected override void LoadDefaultMessages()
        {
            LocalizationText.LoadDefaultMessages(this);
        }

        void OnPlayerSleepEnded(BasePlayer player)
        {
            if (player == null || player.IsNpc || !player.IsConnected)
                return;

            var list = Query(__Sql(@"
    SELECT pt.id, pt.wearable_data, pt.metabolism_data, sp.x, sp.y, sp.z, sp.name, sp.id as spawnpoint_id
    FROM player_transfer pt
    LEFT JOIN spawnpoints sp ON pt.spawnpointId = sp.id
    WHERE pt.ownerId = @0 AND pt.serverId = @1 AND pt.readed = 0
    LIMIT 1;", player.userID.Get(), this.currentServer.DatabaseId));

            if (list == null || list.Count == 0)
                return;

            {
                byte[] data = (byte[])list[0]["wearable_data"];
                ProtoBuf.ItemContainer container = ProtoBuf.ItemContainer.Deserialize(data);
                container.InspectUids(new RemapperUID(RemappingType.New).Map);
                player.inventory.containerWear.Load(container);
            }

            {
                byte[] data = (byte[])list[0]["metabolism_data"];
                ProtoBuf.PlayerMetabolism metabolism = ProtoBuf.PlayerMetabolism.Deserialize(data);
                player.metabolism.Load(metabolism);
                player.metabolism.isDirty = true;
                player.SendNetworkUpdate();
            }

            Vector3 teleportPosition = Vector3.zero;
            if (list[0]["x"] is float x && list[0]["y"] is float y && list[0]["z"] is float z)
            {
                teleportPosition = new Vector3(x, y, z);
                if (teleportPosition != default(Vector3))
                    player.Teleport(teleportPosition);
            }

            // Логируем успешное прибытие игрока
            if (list[0]["spawnpoint_id"] != null && !Convert.IsDBNull(list[0]["spawnpoint_id"]))
            {
                int spawnpointId = (int)list[0]["spawnpoint_id"];
                string spawnpointName = list[0]["name"] as string;

                DiscordLogger.LogPlayerArrival(
                    player.userID,
                    player.displayName,
                    currentServer,
                    teleportPosition,
                    spawnpointId,
                    spawnpointName
                );
            }

            NonQuery(__Sql("UPDATE player_transfer SET readed = 1 WHERE id = @0", list[0]["id"]));
        }


        void OnPlayerDisconnected(BasePlayer basePlayer, string reason)
        {
            Player player = playerCollection.Get(basePlayer);
            if (player != null)
                player.OnPlayerDisconnected();
        }

        object OnMaxStackable(Item item)
        {
            if (item == null || item.info == null)
                return null;

            ItemContainer container = item.parent;
            if (container == null)
                return null;
            if (container.entityOwner is not CargoNPC)
                return null;

            if (databaseConfig.ItemStackable.TryGetValue(item.info.itemid, out int stack))
                return stack;

            return null;
        }


        // Запреты на действия с предметом (включает выкидывание предмета)
        object OnItemAction(Item item, string action, BasePlayer player)
        {
            return CanMoveItem(item, player.inventory, default, -1, 1, ItemMoveModifier.None);
        }

        // Запреты на перемещение
        object CanMoveItem(Item item, PlayerInventory playerInventory, ItemContainerId targetContainer, int targetSlot, int amount, ItemMoveModifier itemMoveModifier)
        {
            PlayerLoot loot = playerInventory.loot;
            if (loot.entitySource is not CargoNPC)
                return null;

            ItemContainer parentContainer = item.parent;
            if (parentContainer == null)
                return false;

            if (parentContainer.IsLocked())
                return false;

            Player player = playerCollection.Get(playerInventory.baseEntity);
            if (player == null)
                return false;

            IPaymentProvider paymentProvider = player.paymentProvider;
            if (paymentProvider == null)
                return false;

            // Если не хватает денег на оплату хранение за слот
            ContainerInfo containerInfo = player.FindContainer(parentContainer.uid);
            if (containerInfo != null)
            {
                float storageCost = PriceCalculator.GetStorageCost(containerInfo, player.UI.CurrentCurrency);
                if (storageCost > 0)
                {
                    float storageCostForItem = (storageCost / containerInfo.Slots / item.amount) * amount;
                    if (paymentProvider.GetBalance(playerInventory.baseEntity) < storageCostForItem)
                    {
                        playerInventory.baseEntity.ShowToast(GameTip.Styles.Red_Normal, НЕДОСТАТОЧНО_СРЕДСТВ_ДЛЯ_ОПЛАТЫ_СТОИМОСТИ_ХРАНЕНИЯ.Localize(playerInventory.baseEntity), true);
                        return false;
                    }
                }
            }
            return null;
        }
        #endregion

        #region Patches

        [HarmonyPatch(typeof(SteamPlatform), "OnSteamConnected")]
        private static class SteamPlatform_OnSteamConnected_Patch
        {
            private static void Postfix(SteamPlatform __instance)
            {
                PluginInstance.RegisterServerInDb();
            }

        }


        [HarmonyPatch(typeof(ConstructionErrors), "Log")]
        private static class ConstructionErrors_Log_Patch
        {
            private static void Postfix(BasePlayer __0, string __1)
            {
                PlayerLoot loot = __0.inventory.loot;
                if (loot.entitySource is not CargoNPC)
                    return;

                ItemContainer container = loot.containers[0];
                if (container == null)
                    return;

                Player player = PluginInstance.playerCollection.Get(__0);
                if (player == null)
                    return;

                ContainerInfo containerInfo = player.FindContainer(container.uid);
                if (containerInfo != null)
                {

                    if (containerInfo.State == ContainerState.InTransit)
                        __0.ShowToast(GameTip.Styles.Red_Normal, НЕЛЬЗЯ_ПЕРЕМЕЩАТЬ_ПРЕДМЕТЫ.Localize(__0), true);
                }
            }
        }
        #endregion

        #region Methods


        #region SQL
        public static Sql __Sql(string sql, params object[] args) => Sql.Builder.Append(sql, args);

        public static List<Dictionary<string, object>> Query(Sql sql, Action<List<Dictionary<string, object>>> callback = null)
        {
            var provider = PluginInstance.databaseProvider;
            if (provider == null)
                return null;

            var connection = PluginInstance.databaseConnection;
            if (connection == null)
                return null;


            if (callback != null)
            {
                provider.Query(sql, connection, callback);
                return null;
            }

            List<Dictionary<string, object>> list = new List<Dictionary<string, object>>();
            using (MySqlConnection sqlConnection = new MySqlConnection(connection.ConnectionString))
            {
                sqlConnection.Open();

                using (var cmd = sqlConnection.CreateCommand())
                {
                    cmd.CommandText = sql.SQL;
                    cmd.CommandTimeout = 5;
                    Sql.AddParams(cmd, sql.Arguments, "@");
                    using (MySqlDataReader mySqlDataReader = cmd.ExecuteReader())
                    {
                        while (mySqlDataReader.Read() && (!connection.ConnectionPersistent || sqlConnection.State != ConnectionState.Closed && sqlConnection.State != ConnectionState.Broken))
                        {
                            Dictionary<string, object> dictionary = new Dictionary<string, object>();
                            for (int i = 0; i < mySqlDataReader.FieldCount; i++)
                                dictionary.Add(mySqlDataReader.GetName(i), mySqlDataReader.GetValue(i));
                            list.Add(dictionary);
                        }
                    }
                }
            }
            return list;
        }

        public static void NonQuery(Sql sql, Action<int> callback = null)
        {
            var provider = PluginInstance.databaseProvider;
            if (provider == null)
                return;

            var connection = PluginInstance.databaseConnection;
            if (connection == null)
                return;

            provider.ExecuteNonQuery(sql, connection, callback);
        }
        #endregion

        private void RegisterServerInDb()
        {
            string serverIP = GetServerIP();
            if (serverIP.StartsWith("0.0.0.0"))
                return;

            NonQuery(__Sql("INSERT IGNORE INTO servers (ip, name) VALUES (@0, @1);", serverIP, ConVar.Server.hostname));

            if (serverInitialized)
                SendServerMapToDB();

            PluginInstance.LoadServerListFromDB();
        }

        private void SendServerMapToDB()
        {
            string serverIP = GetServerIP();
            if (serverIP.StartsWith("0.0.0.0") || mapImage == null)
                return;

            NonQuery(__Sql(
                @"
                REPLACE INTO `maps` (`serverId`, `width`, `height`, `size`, `background`, `data`)
                SELECT `id`, @1, @2, @3, @4, @5
                FROM `servers`
                WHERE `ip` = @0;
                ", serverIP, mapImage.width, mapImage.height, World.Size, mapImage.backgroundHex, mapImage.data));
        }

        public static void PopulateMapImageCache()
        {

            if (mapImage != null)
                return;

            mapImage = new MapImage();
            string fileName = string.Format("map_{0}_{1}.png", World.Size, World.Seed);
            string fullPath = Path.GetFullPath(Path.Combine(System.Environment.CurrentDirectory, fileName));
            if (File.Exists(fullPath))
            {
                using (System.Drawing.Bitmap bitmap = new System.Drawing.Bitmap(fullPath))
                {
                    mapImage.width = bitmap.Width;
                    mapImage.height = bitmap.Height;
                    mapImage.data = File.ReadAllBytes(fullPath);
                    var firstPixel = bitmap.GetPixel(0, 0); // ocean color
                    mapImage.backgroundHex = "#" + ColorUtility.ToHtmlStringRGB(new Color32(firstPixel.R, firstPixel.G, firstPixel.B, firstPixel.A));
                }
            }
            else
            {
                RenderMap(ref mapImage);
                if (mapImage.data != null)
                    File.WriteAllBytes(fullPath, mapImage.data);
            }

        }

        private static void RenderMap(ref MapImage mapImage)
        {
            try
            {
                Color color;
                mapImage.data = MapImageRenderer.Render(out mapImage.width, out mapImage.height, out color, 1f / (World.Size / 600f), true, false, 0);
                mapImage.backgroundHex = "#" + ColorUtility.ToHtmlStringRGB(color);
            }
            catch (Exception arg)
            {
                Debug.LogError(string.Format("Exception thrown when rendering map: {0}", arg));
            }
        }

        // Проверка базы данных, для обновления и обработки событий
        private void CheckDatabase()
        {

            // Обновление списка серверов
            {
                Query(__Sql(
                @"
                   SELECT 1 FROM servers WHERE (SELECT SUM(id) FROM servers) != @0 && (SELECT COUNT(*) FROM spawnpoints) != @1
                ", serverList.Sum(s => s.Key), serverList.Sum(s => s.Value.SpawnPoints.Count)), rows =>
                {
                    if (rows != null && rows.Count > 0)
                        LoadServerListFromDB();
                });
            }

            // Обновление конфига
            {
                Query(__Sql(
                 @"
                   SELECT json FROM config WHERE hash != @0
                ", databaseConfig?.Hash), rows =>
                 {
                     if (rows != null && rows.Count > 0)
                     {
                         databaseConfig = DatabaseConfiguration.Load((string)rows[0]["json"]);
                         Puts("Config Updated.");
                     }
                 });
            }


            // Смена статуса контейнеров чье время доставки уже подошло, а так же отправление уведомлений игроку если он на этом сервере
            {
                if (this.currentServer != null)
                {
                    Query(__Sql(
                        @"
                            SELECT *
                            FROM containers
                            WHERE `state` = @0
                              AND `to-server-id` = @1
                              AND `timestamp` + JSON_EXTRACT(
                                  (SELECT json FROM config WHERE id = 1),
                                  CONCAT('$.', '""Время в секундах для вариантов доставки"".""', `variant`, '""')) < UNIX_TIMESTAMP();
                        ", ContainerState.InTransit.ToString(), this.currentServer.DatabaseId), rows =>
                        {
                            if (rows != null)
                            {
                                foreach (Dictionary<string, object> values in rows)
                                {
                                    ContainerInfo containerInfo = new ContainerInfo(values);
                                    containerInfo.ToDelivered();
                                    containerInfo.Save();
                                    SendNotificationAboutDelivered(containerInfo);
                                }
                            }
                        });
                }
            }

            // Уведомления игроку о прибытии
            if (BasePlayer.activePlayerList.Count > 0)
            {
                Query(__Sql(
                @"SELECT c.* 
                    FROM containers c
                    RIGHT JOIN notifications n
                    ON c.id = n.containerId AND c.ownerId = n.userId
                    WHERE n.readed = 0 AND c.ownerId IN (@0)", BasePlayer.activePlayerList.Select(p => p.userID.Get())), rows =>
                {
                    if (rows != null)
                    {
                        foreach (Dictionary<string, object> values in rows)
                        {
                            ContainerInfo containerInfo = new ContainerInfo(values);
                            SendNotificationAboutDelivered(containerInfo);
                        }
                    }
                });
            }
        }

        // Уведомление о прибытии груза
        private void SendNotificationAboutDelivered(ContainerInfo containerInfo)
        {
            BasePlayer basePlayer = BasePlayer.FindByID(containerInfo.OwnerId);
            if (basePlayer == null)
                return;

            basePlayer.ChatMessage(string.Format(ФОРМАТ_УВЕДОМЛЕНИЕ_ПРИБЫЛ_ГРУЗ.Localize(basePlayer), Server.Get(containerInfo.SourceServerId)?.Name, Server.Get(containerInfo.DestinationServerId.Value)?.Name));
            SendLocalEffect("assets/bundled/prefabs/fx/invite_notice.prefab", basePlayer.Connection);

            Player player = playerCollection.Get(basePlayer);
            if (player != null)
            {
                PlayerUI playerUI = player.UI;
                if (playerUI != null)
                {
                    if (playerUI.IsActive && playerUI.CurrentCategory == PlayerUI.Category.Main)
                        playerUI.UpdateBodyWithFetch();
                }
            }

            NonQuery(__Sql("UPDATE notifications SET readed = 1 WHERE containerId = @0", containerInfo.DatabaseId));
        }

        private bool InitializeDatabase()
        {
            try
            {
                Configuration.DatabaseCredentials credentials = config.DbCredentials;
                if (credentials.Password == "enterpassword")
                {
                    Debug.LogError("Database credentials has not been configured!");
                    return false;
                }


                Oxide.Core.MySql.Libraries.MySql mysql = Interface.Oxide.GetLibrary<Oxide.Core.MySql.Libraries.MySql>();
                this.databaseProvider = mysql;
                this.databaseConnection = mysql.OpenDb(credentials.Address, credentials.Port, credentials.Name, credentials.Username, credentials.Password, this);
                if (this.databaseConnection == null || this.databaseConnection.Con == null)
                {
                    Debug.LogError("Couldn't open MySQL Database: " + this.databaseConnection.Con.State.ToString());
                    return false;
                }

                Puts("Database opened: " + credentials.Address);

                // Создание всех таблиц

                NonQuery(__Sql(
                        @"CREATE TABLE IF NOT EXISTS `config` (
                          `id` int NOT NULL AUTO_INCREMENT,
                          `json` text,
                          `hash` int DEFAULT NULL,
                          PRIMARY KEY (`id`)
                        ) AUTO_INCREMENT=3 DEFAULT CHARSET=utf8mb4;

                        CREATE TABLE IF NOT EXISTS `containers` (
                              `id` int NOT NULL AUTO_INCREMENT,
                              `ownerId` bigint NOT NULL,
                              `data` blob NOT NULL,
                              `slots` int NOT NULL DEFAULT '0',
                              `capacity` int NOT NULL,
                              `from-server-id` int NOT NULL,
                              `to-server-id` int DEFAULT NULL,
                              `save-protocol` int NOT NULL,
                              `variant` varchar(12) DEFAULT 'Stardard',
                              `state` varchar(12) DEFAULT NULL,
                              `timestamp` int DEFAULT NULL,
                              PRIMARY KEY (`id`),
                              KEY `SERVER_ID_idx` (`from-server-id`,`to-server-id`),
                              KEY `OWNER_ID_idx` (`ownerId`)
                            ) DEFAULT CHARSET=utf8mb4;

                        CREATE TABLE IF NOT EXISTS `notifications` (
                          `id` int NOT NULL AUTO_INCREMENT,
                          `userId` bigint NOT NULL,
                          `containerId` int NOT NULL,
                          `readed` tinyint(1) NOT NULL DEFAULT '0',
                          PRIMARY KEY (`id`),
                          UNIQUE KEY `UNIQUE` (`containerId`,`userId`)
                        ) DEFAULT CHARSET=utf8mb4;

                        CREATE TABLE IF NOT EXISTS `servers` (
                          `id` int NOT NULL AUTO_INCREMENT,
                          `ip` varchar(45) NOT NULL,
                          `name` tinytext,
                          PRIMARY KEY (`id`),
                          UNIQUE KEY `ip` (`ip`)
                        ) AUTO_INCREMENT=419 DEFAULT CHARSET=utf8mb4;

                        CREATE TABLE IF NOT EXISTS `player_transfer` (
                          `id` int NOT NULL AUTO_INCREMENT,
                          `ownerId` bigint NOT NULL,
                          `serverId` int NOT NULL,
                          `spawnpointId` int DEFAULT NULL,
                          `wearable_data` blob NOT NULL,
                          `metabolism_data` blob NOT NULL,
                          `readed` tinyint NOT NULL DEFAULT '0',
                          PRIMARY KEY (`id`)) DEFAULT CHARSET=utf8mb4;

                        CREATE TABLE IF NOT EXISTS `maps` (
                          `serverId` int NOT NULL,
                          `width` int NOT NULL,
                          `height` int NOT NULL,
                          `size` int NOT NULL,
                          `background` varchar(16) NOT NULL,
                          `data` mediumblob NOT NULL,
                          PRIMARY KEY (`serverId`),
                          CONSTRAINT `MAP_FROM_SERVER` FOREIGN KEY (`serverId`) REFERENCES `servers` (`id`)
                        ) DEFAULT CHARSET=utf8mb4;

                        CREATE TABLE IF NOT EXISTS `spawnpoints` (
                          `id` int NOT NULL AUTO_INCREMENT,
                          `serverId` int NOT NULL,
                          `x` float DEFAULT '0',
                          `y` float DEFAULT '0',
                          `z` float DEFAULT '0',
                          `name` tinytext,
                          PRIMARY KEY (`id`),
                          KEY `SERVER_SPAWNPOINTS_idx` (`serverId`),
                          CONSTRAINT `SERVER_SPAWNPOINTS` FOREIGN KEY (`serverId`) REFERENCES `servers` (`id`)
                        ) AUTO_INCREMENT=6 DEFAULT CHARSET=utf8mb4;"));


                // Добавление триггера

                NonQuery(__Sql(
                        @"
                    DROP TRIGGER IF EXISTS DeliveredTrigger;
                    CREATE TRIGGER  DeliveredTrigger
                    AFTER UPDATE ON containers
                    FOR EACH ROW
                    BEGIN
                        IF NEW.state = 'Delivered' AND OLD.state = 'InTransit' THEN
                            INSERT IGNORE INTO notifications (userId, containerId)
                            VALUES (NEW.ownerId, NEW.id);
                        END IF;
                    END;"));

                RegisterServerInDb();

                databaseConfig = DatabaseConfiguration.Load();
                return true;
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogException(ex);
                return false;
            }
        }

        // Получаем и заполняем список серверов
        private bool LoadServerListFromDB()
        {
            bool loadResult = LoadServerListFromDB(Query(__Sql(
                   @"
                SELECT 
                    s.id AS server_id,
                    s.ip AS server_ip,
                    s.name AS server_name,
                    m.width AS map_width,
                    m.height AS map_height,
                    m.background AS map_background,
                    m.data AS map_data,
                    m.size AS world_size,
                    GROUP_CONCAT(CONCAT(sp.id, ' [', sp.x, ',', sp.y, ',', sp.z, '] (', IFNULL(sp.name, ''), ')') SEPARATOR ' | ') AS spawnpoints
                FROM 
                    servers s
                LEFT JOIN 
                    maps m ON s.id = m.serverId
                LEFT JOIN 
                    spawnpoints sp ON s.id = sp.serverId
                GROUP BY 
                    s.id;
                ")));

            if (loadResult)
            {
                foreach (Server server in serverList.Values)
                {
                    if (server.Map == null)
                        continue;

                    server.Map.crc = FileStorage.server.Store(server.Map.data, FileStorage.Type.png, CommunityEntity.ServerInstance.net.ID);
                }
            }
            return loadResult;
        }

        // Собирает список из ответа БД
        private bool LoadServerListFromDB(List<Dictionary<string, object>> rows)
        {
            if (rows != null)
            {
                serverList.Clear();
                foreach (Dictionary<string, object> values in rows)
                {
                    Server server = Server.Get(values);
                    if (server != null)
                        serverList[server.DatabaseId] = server;
                }

                this.currentServer = Server.Get(GetServerIP());
                if (this.currentServer == null)
                {
                    PrintError("Сервер не был зарегистрирован в базе данных!");
                    return false;
                }
                Puts($"[{this.currentServer.DatabaseId}] {this.currentServer.Name}");

                return true;
            }
            return false;
        }

        // Прогрев мультиязычных текстов
        void PrewarmLabels()
        {
            RuntimeHelpers.RunClassConstructor(typeof(CargoNPC).TypeHandle);
            RuntimeHelpers.RunClassConstructor(typeof(PlayerUI).TypeHandle);
        }

        // Отправлка локального эффекта игроку
        private static void SendLocalEffect(string effectPath, Connection connection)
        {
            List<Connection> connections = Facepunch.Pool.Get<List<Connection>>();
            connections.Add(connection);
            Effect.server.Run(effectPath, connection.player as BasePlayer, targets: connections);
            Facepunch.Pool.FreeUnmanaged(ref connections);
        }

        // Получение серверного айпи вместе с портом
        private static string GetServerIP()
        {
            return string.Format("{0}:{1}", SteamServer.PublicIp, Net.sv.port);
        }

        // Проверка на наличие разрешения админа
        private static bool IsAdmin(string userId)
        {
            return PluginInstance.permission.UserHasPermission(userId, PERMISSSION_ADMIN);
        }

        // Спавн НПС по параметрам из конфига
        private void SpawnNPC()
        {
            Npc15.DespawnAll<CargoNPC>();
            NPCs.Clear();

            foreach (Configuration.NPC npcConfig in config.Npc)
            {
                var coord = npcConfig.Coord;
                if (coord == default((Vector3 position, Vector3 rotation)) || coord.position == default(Vector3))
                    continue;
                CargoNPC npc = Npc15.SpawnNPC<CargoNPC>(coord.position, coord.rotation, npc => npc.config = npcConfig);
                NPCs.Add(npc);
            }
        }
        #endregion

        #region Command Handlers

        [ChatCommand("cargoui")]
        private void cargoui(BasePlayer player)
        {
            if (!IsAdmin(player.UserIDString))
                return;

            Player pluginPlayer = this.playerCollection.GetOrCreate(player);
            if (pluginPlayer == null)
                return;

            pluginPlayer.UI.Open();
        }

        // Обработчик команд плагина
        [ConsoleCommand("cargonpc")]
        private void cargonpc(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null)
                return;

            Player pluginPlayer = this.playerCollection.Get(player);
            if (pluginPlayer == null)
                return;

            switch (arg.GetString(0))
            {
                case "destroy":
                    {
                        PlayerUI playerUI = pluginPlayer.UI;
                        if (playerUI == null)
                            break;
                        playerUI.Destroy();
                        break;
                    }
                case "dropdown-currency":
                    {
                        PlayerUI playerUI = pluginPlayer.UI;
                        if (playerUI == null)
                            break;

                        playerUI.AnotherCurrency();
                        return;
                    }
                case "select-currency":
                    {
                        PlayerUI playerUI = pluginPlayer.UI;
                        if (playerUI == null)
                            break;

                        playerUI.CurrentCurrency = (Currency)Enum.Parse(typeof(Currency), arg.GetString(1));
                        playerUI.Open();
                        break;
                    }
                case "set-spawnpoint":
                    {
                        PlayerUI playerUI = pluginPlayer.UI;
                        if (playerUI == null)
                            break;

                        if (playerUI.DestinationServer == null)
                        {
                            playerUI.CargoUI_Notification("Сначала выберите сервер.");
                            break;
                        }

                        if (arg.GetString(1) == "continue")
                        {
                            if (playerUI.SelectedSpawnPoint == null)
                                playerUI.CargoUI_Notification("Поскольку вы не выбрали точку - вы не будете отправлены.", fontSize: 15, destroyTime: 5f);

                            break;
                        }

                        if (playerUI.SelectedSpawnPoint != null)
                        {
                            playerUI.SelectedSpawnPoint = null;
                            playerUI.UpdateBody();
                            break;
                        }

                        playerUI.OrderMapSelector("Create Order Content", "ПРОДОЛЖИТЬ", "cargonpc set-spawnpoint continue");
                        break;
                    }

                case "select-spawnpoint":
                    {
                        PlayerUI playerUI = pluginPlayer.UI;
                        if (playerUI == null)
                            break;

                        if (playerUI.DestinationServer == null)
                            break;

                        int id = arg.GetInt(1);
                        if (id > 0)
                        {
                            playerUI.SelectedSpawnPoint = playerUI.DestinationServer.SpawnPoints.Find(sp => sp.Id == id);
                        }
                        else
                        {
                            if (arg.GetString(1) == "random")
                            {
                                SpawnPoint currentSpawnPoint = playerUI.SelectedSpawnPoint;
                                do
                                {
                                    playerUI.SelectedSpawnPoint = playerUI.DestinationServer.SpawnPoints.GetRandom();
                                }
                                while (playerUI.DestinationServer.SpawnPoints.Count > 1 && currentSpawnPoint == playerUI.SelectedSpawnPoint);
                            }
                            else
                            {
                                break;
                            }
                        }

                        playerUI.UpdateBody();
                        switch (playerUI.CurrentCategory)
                        {
                            case PlayerUI.Category.CreateOrder:
                                playerUI.OrderMapSelector("Create Order Content", "ПРОДОЛЖИТЬ", "cargonpc set-spawnpoint continue");
                                break;
                            case PlayerUI.Category.Travel:
                                playerUI.OrderMapSelector("Travel Content", "ОТПРАВИТЬСЯ", "cargonpc travel GO");
                                break;
                        }
                        break;
                    }

                case "travel":
                    {
                        PlayerUI playerUI = pluginPlayer.UI;
                        if (playerUI == null)
                            break;

                        switch (arg.GetString(1))
                        {
                            case "server-map":
                                {
                                    playerUI.DestinationServer = Server.Get(arg.GetInt(2));
                                    playerUI.OrderMapSelector("Travel Content", "ОТПРАВИТЬСЯ", "cargonpc travel GO");
                                    break;
                                }
                            case "set-spawnpoint":
                                {
                                    playerUI.DestinationServer = Server.Get(arg.GetInt(2));
                                    playerUI.OrderMapSelector("Travel Content", "ОТПРАВИТЬСЯ", "cargonpc travel GO");
                                    break;
                                }
                            case "GO":
                                {
                                    playerUI.OnTravelSendButtonClick();
                                    break;
                                }
                        }
                      
                        SendLocalEffect("assets/bundled/prefabs/fx/notice/loot.copy.fx.prefab", arg.Connection);
                        break;
                    }

                // Обновление чекбоксов на форме заказа со звуковым сопровождением 
                case "update-data":
                    {
                        PlayerUI playerUI = pluginPlayer.UI;
                        if (playerUI == null)
                            break;

                        string key = arg.GetString(1);
                        switch (key)
                        {
                            case "server":
                                playerUI.DestinationServer = Server.Get(arg.GetInt(2));
                                break;
                            case "delivery-variant":
                                playerUI.DeliveryVariant = (DeliveryVariant)arg.GetInt(2);
                                break;
                            case "hide-completed":
                                playerUI.HideCompleted = !playerUI.HideCompleted;
                                break;
                            default:
                                return;
                        }

                        playerUI.UpdateBody();
                        SendLocalEffect("assets/bundled/prefabs/fx/notice/loot.copy.fx.prefab", arg.Connection);
                        break;
                    }

                // Обновление категории в интерфейсе
                case "change-category":
                    {
                        PlayerUI playerUI = pluginPlayer.UI;
                        if (playerUI == null)
                            break;

                        playerUI.GoToCategory((PlayerUI.Category)arg.GetInt(1));
                        break;
                    }

                // Покупка контейнера
                case "buy-container":
                    {
                        pluginPlayer.TryBuyContainer();
                        break;
                    }

                // Покупка слота в контейнере заказа
                case "buy-slot":
                    {
                        pluginPlayer.TryBuyOrderContainerSlot();
                        break;
                    }

                // Открытие контейнера заказа
                case "open-order-container":
                    {
                        pluginPlayer.OpenOrderContainer();
                        break;
                    }

                // Принудительное завершение заказа
                case "force-complete-container":
                    {
                        int containerId = arg.GetInt(1, int.MinValue);
                        if (containerId < 0)
                            return;

                        ContainerInfo containerInfo = pluginPlayer.CachedGlobalContainers.Find(c => c.DatabaseId == containerId);
                        if (containerInfo == null)
                            return;

                        if (containerInfo.Slots > 0 && arg.GetInt(2) == 0)
                        {
                            pluginPlayer.UI.CargoUI_Notification(УДАЛИТЬ_КОНТЕЙНЕР_С_ПРЕДМЕТАМИ.Localize(player), 12);
                            CUI.Root root = new CUI.Root();
                            root.AddUpdateElement($"Cargo_DeleteButton_{containerId}").Components.AddButton(
                                        command: $"cargonpc force-complete-container {containerInfo.DatabaseId} 1",
                                        color: "0.6431373 0.1862745 0.1 1",
                                        material: "assets/icons/iconmaterial.mat");
                            root.Render(arg.Connection);
                            return;
                        }

                        containerInfo.ToCompleted();
                        containerInfo.Save();
                        pluginPlayer.UI.GoToCategory(PlayerUI.Category.Main);
                        break;
                    }

                // Осмотр контейнера
                case "inspect-container":
                    {
                        int containerId = arg.GetInt(1);
                        ContainerInfo containerInfo = pluginPlayer.CachedGlobalContainers.Find(c => c.DatabaseId == containerId);
                        if (containerInfo == null)
                            return;

                        pluginPlayer.OpenContainer(containerInfo);
                        break;
                    }

                // Нажатие на кнопку Отправить в форме
                case "send":
                    {
                        pluginPlayer.UI.OnSendButtonClick();
                        break;
                    }

                // Выборка предметов
                case "item-selector":
                    {
                        switch (arg.GetString(1))
                        {
                            case "select-category":
                                pluginPlayer.UI.selectorCategory = (ItemCategory)arg.GetInt(2);
                                pluginPlayer.UI.UpdateItemSelector();
                                break;
                            case "select-item":
                                if (pluginPlayer.UI.SelectorAction is Action<int> action)
                                    action.Invoke(arg.GetInt(2));
                                break;
                        }
                        break;
                    }

                // Вызов всплывающих меню
                case "popup":
                    {
                        switch (arg.GetString(1))
                        {
                            case "ContainerPrices":
                                pluginPlayer.UI.ContainerPricesPopup();
                                break;
                            case "ContainerProperties":
                                pluginPlayer.UI.ContainerProperties(arg.GetInt(2));
                                break;
                            case "DeliveryPricePerItem":
                                pluginPlayer.UI.DeliveryPricePerItemPopup();
                                break;
                            case "DeliveryPricePerCategory":
                                pluginPlayer.UI.DeliveryPricePerCategoryPopup();
                                break;
                            case "NpcItems":
                                pluginPlayer.UI.NpcItemsPopup();
                                break;
                            case "ItemBlacklist":
                                pluginPlayer.UI.ItemBlacklistPopup();
                                break;
                            case "ItemStacks":
                                pluginPlayer.UI.ItemStacksPopup();
                                break;
                        }
                        break;
                    }


                // Смена и обновление данных в настройках
                case "admin-settings":
                    {
                        if (!IsAdmin(player.UserIDString))
                            return;

                        switch (arg.GetString(1))
                        {
                            case "save-protocol":
                                {
                                    databaseConfig.VisibileOnlyCurrentProtocol = !databaseConfig.VisibileOnlyCurrentProtocol;
                                    break;
                                }
                            case "set-exchange-rate":
                                {
                                    config.ExchangeRate = arg.GetFloat(2);
                                    break;
                                }
                            case "NpcIndex":
                                {
                                    switch (arg.GetString(2))
                                    {
                                        case "prev":
                                            pluginPlayer.UI.NpcIndex = Mathf.Clamp(pluginPlayer.UI.NpcIndex - 1, 0, Mathf.Max(0, config.Npc.Count - 1));
                                            break;
                                        case "next":
                                            pluginPlayer.UI.NpcIndex = Mathf.Clamp(pluginPlayer.UI.NpcIndex + 1, 0, Mathf.Max(0, config.Npc.Count - 1));
                                            break;
                                    }
                                    break;
                                }
                            case "NpcList":
                                {
                                    switch (arg.GetString(2))
                                    {
                                        case "add":
                                            config.Npc.Add(new Configuration.NPC());
                                            pluginPlayer.UI.NpcIndex = config.Npc.Count - 1;
                                            SpawnNPC();
                                            break;
                                        case "remove":
                                            int index = arg.GetInt(3);
                                            if (index >= config.Npc.Count)
                                                break;

                                            config.Npc.RemoveAt(index);
                                            pluginPlayer.UI.NpcIndex = Mathf.Clamp(pluginPlayer.UI.NpcIndex - 1, 0, Mathf.Max(0, config.Npc.Count - 1));
                                            SpawnNPC();
                                            break;
                                    }
                                    break;
                                }
                            case "NpcName":
                                {
                                    Configuration.NPC npcConfig = config.Npc[pluginPlayer.UI.NpcIndex];

                                    npcConfig.Name = string.Join(" ", arg.Args.Skip(2));
                                    foreach (CargoNPC npc in BaseNetworkable.serverEntities.OfType<CargoNPC>())
                                    {
                                        npc.limitNetworking = true;
                                        npc.limitNetworking = false;
                                    }
                                    break;
                                }
                            case "SpawnPointIndex":
                                {
                                    switch (arg.GetString(2))
                                    {
                                        case "prev":
                                            pluginPlayer.UI.SpawnPointIndex = Mathf.Clamp(pluginPlayer.UI.SpawnPointIndex - 1, 0, Mathf.Max(0, currentServer.SpawnPoints.Count - 1));
                                            break;
                                        case "next":
                                            pluginPlayer.UI.SpawnPointIndex = Mathf.Clamp(pluginPlayer.UI.SpawnPointIndex + 1, 0, Mathf.Max(0, currentServer.SpawnPoints.Count - 1));
                                            break;
                                    }
                                    break;
                                }
                            case "SpawnPoints":
                                {
                                    Action<int> updateAction = rowsAffected =>
                                    {
                                        LoadServerListFromDB();
                                        pluginPlayer.UI.UpdateAdminContent();
                                    };

                                    switch (arg.GetString(2))
                                    {
                                        case "add":
                                            pluginPlayer.UI.SpawnPointIndex = currentServer.SpawnPoints.Count;
                                            NonQuery(__Sql("INSERT INTO spawnpoints (serverId) VALUES (@0)", PluginInstance.currentServer.DatabaseId), updateAction);
                                            break;
                                        case "remove":
                                            {
                                                int index = pluginPlayer.UI.SpawnPointIndex;
                                                if (index >= currentServer.SpawnPoints.Count)
                                                    return;

                                                SpawnPoint spawnPoint = currentServer.SpawnPoints[index];
                                                if (spawnPoint == null)
                                                    return;

                                                pluginPlayer.UI.SpawnPointIndex = Mathf.Max(0, index-1);

                                                NonQuery(__Sql("DELETE FROM spawnpoints WHERE id = @0", spawnPoint.Id), updateAction);
                                                break;
                                            }
                                        case "set-name":
                                            {
                                                int index = pluginPlayer.UI.SpawnPointIndex;
                                                if (index >= currentServer.SpawnPoints.Count)
                                                    return;

                                                SpawnPoint spawnPoint = currentServer.SpawnPoints[index];
                                                if (spawnPoint == null)
                                                    return;

                                                NonQuery(__Sql("UPDATE spawnpoints SET name = @1 WHERE id = @0", spawnPoint.Id, arg.GetString(3)), updateAction);
                                                break;
                                            }
                                        case "set-position":
                                            {
                                                int index = pluginPlayer.UI.SpawnPointIndex;
                                                if (index >= currentServer.SpawnPoints.Count)
                                                    return;

                                                SpawnPoint spawnPoint = currentServer.SpawnPoints[index];
                                                if (spawnPoint == null)
                                                    return;

                                                Vector3 position = player.transform.position;

                                                NonQuery(__Sql("UPDATE spawnpoints SET x = @1, y = @2, z = @3 WHERE id = @0", spawnPoint.Id, position.x, position.y, position.z), updateAction);
                                                break;
                                            }
                                        case "teleport":
                                            {
                                                int index = pluginPlayer.UI.SpawnPointIndex;
                                                if (index >= currentServer.SpawnPoints.Count)
                                                    return;

                                                SpawnPoint spawnPoint = currentServer.SpawnPoints[index];
                                                if (spawnPoint == null)
                                                    return;

                                                player.Teleport(spawnPoint.Position);
                                                pluginPlayer.UI.Destroy();
                                                break;
                                            }
                                    }
                                    return;
                                }
                            case "PlayerMaxActiveContainerCount":
                                {
                                    databaseConfig.PlayerMaxActiveContainerCount = arg.GetInt(2);
                                    break;
                                }
                            case "FreeStorageHours":
                                {
                                    databaseConfig.FreeStorageHours = arg.GetFloat(2);
                                    break;
                                }
                            case "StorageCostPerHour":
                                {
                                    databaseConfig.StorageCostPerHour = arg.GetFloat(2);
                                    break;
                                }
                            case "DeliveryVariantTimes":
                                {
                                    DeliveryVariant deliveryVariant = (DeliveryVariant)Enum.Parse(typeof(DeliveryVariant), arg.GetString(2));
                                    databaseConfig.DeliveryVariantTimes[deliveryVariant] = arg.GetFloat(3);
                                    break;
                                }
                            case "DeliveryVariantPrices":
                                {
                                    DeliveryVariant deliveryVariant = (DeliveryVariant)Enum.Parse(typeof(DeliveryVariant), arg.GetString(2));
                                    databaseConfig.DeliveryVariantPrices[deliveryVariant] = arg.GetFloat(3);
                                    break;
                                }
                            case "DeliveryPricePerCategory":
                                {
                                    ItemCategory itemCategory = (ItemCategory)arg.GetInt(2);
                                    databaseConfig.DeliveryPricePerCategory[itemCategory] = arg.GetFloat(3);
                                    pluginPlayer.UI.DeliveryPricePerCategoryPopup();
                                    return;
                                }
                            case "ContainerProperties":
                                {
                                    switch (arg.GetString(2))
                                    {
                                        case "set-price":
                                            {
                                                int index = arg.GetInt(3);
                                                databaseConfig.ContainerProperties[index].Price = arg.GetFloat(4);
                                                pluginPlayer.UI.ContainerProperties(index);
                                                return;
                                            }
                                        case "set-capacity":
                                            {
                                                int index = arg.GetInt(3);
                                                int value = arg.GetInt(5);
                                                DatabaseConfiguration.EachContainerProperties properties = databaseConfig.ContainerProperties[index];
                                                switch (arg.GetString(4))
                                                {
                                                    case "start":
                                                        properties.StartCapacity = value;
                                                        break;
                                                    case "max":
                                                        properties.MaxCapacity = value;
                                                        break;
                                                    default:
                                                        return;
                                                }
                                                pluginPlayer.UI.ContainerProperties(index);
                                                return;
                                            }
                                        case "set-slot-price":
                                            {
                                                int index = arg.GetInt(3);
                                                databaseConfig.ContainerProperties[index].SlotPrice = arg.GetInt(4);
                                                pluginPlayer.UI.ContainerProperties(index);
                                                return;
                                            }
                                        case "add-item":
                                            {
                                                databaseConfig.ContainerProperties.Add(new DatabaseConfiguration.EachContainerProperties());
                                                pluginPlayer.UI.ContainerPricesPopup();
                                                return;
                                            }
                                        case "remove-item":
                                            {
                                                databaseConfig.ContainerProperties.RemoveAt(arg.GetInt(3));
                                                pluginPlayer.UI.ContainerPricesPopup();
                                                return;
                                            }
                                    }
                                    break;
                                }
                            case "DeliveryPricePerItem":
                                {
                                    switch (arg.GetString(2))
                                    {
                                        case "set":
                                            {
                                                databaseConfig.DeliveryPricePerItem[arg.GetString(3)] = arg.GetFloat(4);
                                                pluginPlayer.UI.DeliveryPricePerItemPopup();
                                                return;
                                            }
                                        case "add-item":
                                            {
                                                pluginPlayer.UI.ItemSelector();
                                                pluginPlayer.UI.SelectorAction = new Action<int>(itemId =>
                                                {
                                                    ItemDefinition itemDefinition = ItemManager.FindItemDefinition(itemId);
                                                    if (itemDefinition == null || databaseConfig.DeliveryPricePerItem.ContainsKey(itemDefinition.shortname))
                                                        return;

                                                    databaseConfig.DeliveryPricePerItem.Add(itemDefinition.shortname, databaseConfig.DeliveryPricePerCategory.TryGetValue(itemDefinition.category, out float price) ? price : 1f);

                                                    pluginPlayer.UI.DeliveryPricePerItemPopup();
                                                });
                                                return;
                                            }
                                        case "remove-item":
                                            {
                                                if (databaseConfig.DeliveryPricePerItem.Remove(arg.GetString(3)))
                                                    pluginPlayer.UI.DeliveryPricePerItemPopup();
                                                return;
                                            }
                                    }
                                    break;
                                }
                            case "ItemStackable":
                                {
                                    switch (arg.GetString(2))
                                    {
                                        case "set":
                                            {
                                                databaseConfig.ItemStackable[arg.GetInt(3)] = arg.GetInt(4);
                                                pluginPlayer.UI.ItemStacksPopup();
                                                return;
                                            }
                                        case "add-item":
                                            {
                                                pluginPlayer.UI.ItemSelector();
                                                pluginPlayer.UI.SelectorAction = new Action<int>(itemId =>
                                                {
                                                    if (databaseConfig.ItemStackable.ContainsKey(itemId))
                                                        return;

                                                    ItemDefinition itemDefinition = ItemManager.FindItemDefinition(itemId);

                                                    databaseConfig.ItemStackable.Add(itemId, itemDefinition.stackable);
                                                    pluginPlayer.UI.ItemStacksPopup();
                                                });
                                                return;
                                            }
                                        case "remove-item":
                                            {
                                                if (databaseConfig.ItemStackable.Remove(arg.GetInt(3)))
                                                    pluginPlayer.UI.ItemStacksPopup();
                                                return;
                                            }
                                    }
                                    break;
                                }
                            case "NpcItems":
                                {
                                    int npcIndex = pluginPlayer.UI.NpcIndex;
                                    if (npcIndex >= config.Npc.Count)
                                        return;

                                    Configuration.NPC npcConfig = config.Npc[npcIndex];

                                    switch (arg.GetString(2))
                                    {
                                        case "set":
                                            {
                                                npcConfig.Items[arg.GetInt(3)] = arg.GetULong(4);
                                                pluginPlayer.UI.NpcItemsPopup();
                                                return;
                                            }
                                        case "add-item":
                                            {
                                                pluginPlayer.UI.ItemSelector();
                                                pluginPlayer.UI.SelectorAction = new Action<int>(itemId =>
                                                {
                                                    if (npcConfig.Items.ContainsKey(itemId))
                                                        return;

                                                    npcConfig.Items.Add(itemId, 0);
                                                    foreach (CargoNPC npc in NPCs)
                                                        npc.UpdateInventory();
                                                    pluginPlayer.UI.NpcItemsPopup();
                                                });
                                                return;
                                            }
                                        case "remove-item":
                                            {
                                                if (npcConfig.Items.Remove(arg.GetInt(3)))
                                                {
                                                    foreach (CargoNPC npc in NPCs)
                                                        npc.UpdateInventory();
                                                    pluginPlayer.UI.NpcItemsPopup();
                                                }
                                                return;
                                            }
                                    }
                                    break;
                                }
                            case "NpcCoord":
                                {
                                    switch (arg.GetString(2))
                                    {
                                        case "set":
                                            {
                                                config.Npc[pluginPlayer.UI.NpcIndex].Coord = (player.transform.position, player.eyes.rotation.eulerAngles);
                                                SpawnNPC();
                                                player.Teleport(player.transform.position + player.transform.forward);
                                                pluginPlayer.UI.Destroy();
                                                return;
                                            }
                                    }
                                    break;
                                }
                            case "ItemBlacklist":
                                {
                                    switch (arg.GetString(2))
                                    {
                                        case "add-item":
                                            {
                                                pluginPlayer.UI.ItemSelector();
                                                pluginPlayer.UI.SelectorAction = new Action<int>(itemId =>
                                                {
                                                    ItemDefinition itemDefinition = ItemManager.FindItemDefinition(itemId);
                                                    if (itemDefinition == null || databaseConfig.DeliveryPricePerItem.ContainsKey(itemDefinition.shortname))
                                                        return;

                                                    if (config.ItemBlacklist.Add(itemDefinition.shortname))
                                                        pluginPlayer.UI.ItemBlacklistPopup();
                                                });
                                                return;
                                            }
                                        case "remove-item":
                                            {
                                                if (config.ItemBlacklist.Remove(arg.GetString(3)))
                                                    pluginPlayer.UI.ItemBlacklistPopup();
                                                return;
                                            }
                                    }
                                    break;
                                }
                        }
                        pluginPlayer.UI.UpdateAdminContent();
                        break;
                    }
                default:
                    break;
            }
        }
        #endregion

        #region Classes

        public static class DiscordLogger
        {
            private static string webhookUrl => config?.DiscordWebhookUrl;
            private static bool isEnabled => config?.EnableDiscordLogging == true && !string.IsNullOrEmpty(webhookUrl) && webhookUrl != "https://discord.com/api/webhooks/YOUR_WEBHOOK_ID/YOUR_WEBHOOK_TOKEN";


            public static void LogPlayerArrival(ulong playerId, string playerName, Server server, Vector3 position, int spawnPointId, string spawnPointName)
            {
                if (!isEnabled) return;

                try
                {
                    string spawnPointInfo = $"ID: {spawnPointId}";
                    if (!string.IsNullOrEmpty(spawnPointName))
                        spawnPointInfo = $"**{spawnPointName}** (ID: {spawnPointId})";

                    var embed = new DiscordEmbed
                    {
                        title = "🎯 Успешное прибытие",
                        description = "Игрок успешно прибыл на сервер после телепортации с грузом",
                        color = 65280, // зеленый цвет
                        timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                        fields = new List<DiscordEmbedField>
            {
                new DiscordEmbedField { name = "👤 Игрок", value = $"{playerName} (`{playerId}`)", @inline = true },
                new DiscordEmbedField { name = "📍 Сервер", value = server.Name, @inline = true },
                new DiscordEmbedField { name = "🎯 Точка спавна", value = spawnPointInfo, @inline = true },
                new DiscordEmbedField { name = "📍 Позиция", value = GetGridPosition(position), @inline = true }
            }
                    };

                    SendDiscordMessage(embed);
                }
                catch (System.Exception ex)
                {
                    PluginInstance.PrintError($"Ошибка логирования прибытия: {ex.Message}");
                }
            }

            public static void LogPlayerTeleport(ulong playerId, string playerName, Server fromServer, Server toServer, SpawnPoint spawnPoint, Vector3 fromNpcPosition, List<ItemInfo> items, float totalCost, DeliveryVariant deliveryVariant)
            {
                if (!isEnabled) return;

                try
                {
                    var itemsList = string.Empty;
                    if (items.Count > 0)
                    {
                        itemsList = string.Join("\n", items.Take(10).Select(item => $"• {item.DisplayName} x{item.Amount}"));
                        if (items.Count > 10)
                            itemsList += $"\n... и еще {items.Count - 10} предметов";
                    }
                    else
                    {
                        itemsList = "Нет предметов";
                    }

                    var fields = new List<DiscordEmbedField>
            {
                new DiscordEmbedField { name = "👤 Игрок", value = $"{playerName} (`{playerId}`)", @inline = true },
                new DiscordEmbedField { name = "📍 Откуда", value = fromServer.Name, @inline = true },
                new DiscordEmbedField { name = "📍 Куда", value = toServer.Name, @inline = true },
                new DiscordEmbedField { name = "🎯 НПС Позиция", value = GetGridPosition(fromNpcPosition), @inline = true },
                new DiscordEmbedField { name = "🚀 Тип доставки", value = GetDeliveryVariantText(deliveryVariant), @inline = true },
                new DiscordEmbedField { name = "💰 Стоимость", value = $"{totalCost:F2}", @inline = true },
            };

                    // Информация о точке спавна
                    string spawnPointInfo;
                    if (spawnPoint != null)
                    {
                        spawnPointInfo = $"**{(!string.IsNullOrEmpty(spawnPoint.Name) ? spawnPoint.Name : "Безымянная точка")}**\n";
                        spawnPointInfo += $"ID: {spawnPoint.Id}\n";
                    }
                    else
                    {
                        spawnPointInfo = "Случайная точка спавна";
                    }

                    fields.Add(new DiscordEmbedField { name = "🎯 Точка спавна", value = spawnPointInfo, @inline = false });
                    fields.Add(new DiscordEmbedField { name = "📦 Груз", value = itemsList, @inline = false });

                    var embed = new DiscordEmbed
                    {
                        title = "✈️ Телепортация с грузом",
                        description = "Игрок отправился вместе с грузом на другой сервер",
                        color = 16776960, // золотой цвет
                        timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                        fields = fields,
                        footer = new DiscordEmbedFooter
                        {
                            text = "⚠️ Все предметы кроме одежды были удалены"
                        }
                    };

                    SendDiscordMessage(embed);
                }
                catch (System.Exception ex)
                {
                    PluginInstance.PrintError($"Ошибка логирования телепортации: {ex.Message}");
                }
            }

            public static void LogPlayerTeleport(ulong playerId, string playerName, Server fromServer, Server toServer, SpawnPoint spawnPoint, Vector3 fromNpcPosition)
            {
                if (!isEnabled) return;

                try
                {
                    var fields = new List<DiscordEmbedField>
                    {
                        new DiscordEmbedField { name = "👤 Игрок", value = $"{playerName} (`{playerId}`)", @inline = true },
                        new DiscordEmbedField { name = "📍 Откуда", value = fromServer.Name, @inline = true },
                        new DiscordEmbedField { name = "📍 Куда", value = toServer.Name, @inline = true },
                        new DiscordEmbedField { name = "🎯 НПС Позиция", value = GetGridPosition(fromNpcPosition), @inline = true },
                    };

                    // Информация о точке спавна
                    string spawnPointInfo;
                    if (spawnPoint != null)
                    {
                        spawnPointInfo = $"**{(!string.IsNullOrEmpty(spawnPoint.Name) ? spawnPoint.Name : "Безымянная точка")}**\n";
                        spawnPointInfo += $"ID: {spawnPoint.Id}\n";
                    }
                    else
                    {
                        spawnPointInfo = "Случайная точка спавна";
                    }

                    fields.Add(new DiscordEmbedField { name = "🎯 Точка спавна", value = spawnPointInfo, @inline = false });

                    var embed = new DiscordEmbed
                    {
                        title = "✈️ Телепортация на другой сервер",
                        description = "Игрок отправился на другой сервер",
                        color = 16776960, // золотой цвет
                        timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                        fields = fields,
                        footer = new DiscordEmbedFooter
                        {
                            text = "⚠️ Все предметы кроме одежды были удалены"
                        }
                    };

                    SendDiscordMessage(embed);
                }
                catch (System.Exception ex)
                {
                    PluginInstance.PrintError($"Ошибка логирования телепортации: {ex.Message}");
                }
            }

            // Вспомогательный метод для получения текста варианта доставки
            private static string GetDeliveryVariantText(DeliveryVariant variant)
            {
                return variant switch
                {
                    DeliveryVariant.Standard => "🚛 Стандартная",
                    DeliveryVariant.Express => "🚀 Экспресс",
                    _ => variant.ToString()
                };
            }

            public static void LogContainerInteraction(ulong playerId, string playerName, Server server, Vector3 npcPosition, List<ItemInfo> removedItems, List<ItemInfo> addedItems, ContainerInfo containerInfo)
            {
                if (!isEnabled) return;

                try
                {
                    var fields = new List<DiscordEmbedField>
                    {
                        new DiscordEmbedField { name = "👤 Игрок", value = $"{playerName} (`{playerId}`)", @inline = true },
                        new DiscordEmbedField { name = "📍 Сервер", value = server.Name, @inline = true },
                        new DiscordEmbedField { name = "🎯 НПС Позиция", value = GetGridPosition(npcPosition), @inline = true }
                    };

                    // Добавляем информацию о состоянии контейнера
                    string containerState = containerInfo.State switch
                    {
                        ContainerState.Idle => "Оформление заказа",
                        ContainerState.InTransit => "В пути",
                        ContainerState.Delivered => "Доставлено",
                        ContainerState.Completed => "Завершен",
                        _ => "Неизвестно"
                    };

                    fields.Add(new DiscordEmbedField { name = "📦 Статус контейнера", value = containerState, @inline = true });

                    // Если игрок забрал предметы
                    if (removedItems.Count > 0)
                    {
                        var removedList = string.Join("\n", removedItems.Take(10).Select(item => $"• {item.DisplayName} x{item.Amount}"));
                        if (removedItems.Count > 10)
                            removedList += $"\n... и еще {removedItems.Count - 10} предметов";

                        fields.Add(new DiscordEmbedField { name = "📤 Забрал предметы", value = removedList, @inline = false });
                    }

                    // Если игрок добавил предметы
                    if (addedItems.Count > 0)
                    {
                        var addedList = string.Join("\n", addedItems.Take(10).Select(item => $"• {item.DisplayName} x{item.Amount}"));
                        if (addedItems.Count > 10)
                            addedList += $"\n... и еще {addedItems.Count - 10} предметов";

                        fields.Add(new DiscordEmbedField { name = "📥 Добавил предметы", value = addedList, @inline = false });
                    }

                    // Определяем тип взаимодействия и цвет
                    string title;
                    int color;

                    if (removedItems.Count > 0 && addedItems.Count == 0)
                    {
                        title = "📤 Получение предметов из контейнера";
                        color = 65280; // зеленый
                    }
                    else if (removedItems.Count == 0 && addedItems.Count > 0)
                    {
                        title = "📥 Добавление предметов в контейнер";
                        color = 3447003; // синий
                    }
                    else if (removedItems.Count > 0 && addedItems.Count > 0)
                    {
                        title = "🔄 Обмен предметов в контейнере";
                        color = 16776960; // желтый
                    }
                    else
                    {
                        return; // Нет изменений - не логируем
                    }

                    var embed = new DiscordEmbed
                    {
                        title = title,
                        color = color,
                        timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                        fields = fields
                    };

                    SendDiscordMessage(embed);
                }
                catch (System.Exception ex)
                {
                    PluginInstance.PrintError($"Ошибка логирования взаимодействия с контейнером: {ex.Message}");
                }
            }

            public static void LogShipment(ulong playerId, string playerName, Server fromServer, Server toServer, Vector3 npcPosition, List<ItemInfo> items, float totalCost, DeliveryVariant deliveryVariant)
            {
                if (!isEnabled) return;

                try
                {
                    var itemsList = string.Join("\n", items.Take(10).Select(item => $"• {item.DisplayName} x{item.Amount}"));
                    if (items.Count > 10)
                        itemsList += $"\n... и еще {items.Count - 10} предметов";

                    var embed = new DiscordEmbed
                    {
                        title = "📦 Отправка груза",
                        color = 3447003, // синий цвет
                        timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                        fields = new List<DiscordEmbedField>
                {
                    new DiscordEmbedField { name = "👤 Игрок", value = $"{playerName} (`{playerId}`)", @inline = true },
                    new DiscordEmbedField { name = "📍 Откуда", value = fromServer.Name, @inline = true },
                    new DiscordEmbedField { name = "📍 Куда", value = toServer.Name, @inline = true },
                    new DiscordEmbedField { name = "🎯 НПС Позиция", value = GetGridPosition(npcPosition), @inline = true },
                    new DiscordEmbedField { name = "🚀 Тип доставки", value = deliveryVariant.ToString(), @inline = true },
                    new DiscordEmbedField { name = "💰 Стоимость", value = $"{totalCost:F2}", @inline = true },
                    new DiscordEmbedField { name = "📦 Предметы", value = itemsList.Length > 0 ? itemsList : "Нет предметов", @inline = false }
                }
                    };

                    SendDiscordMessage(embed);
                }
                catch (System.Exception ex)
                {
                    PluginInstance.PrintError($"Ошибка логирования отправки: {ex.Message}");
                }
            }

            public static void LogPickup(ulong playerId, string playerName, Server fromServer, Vector3 npcPosition, List<ItemInfo> items)
            {
                if (!isEnabled) return;

                try
                {
                    var itemsList = string.Join("\n", items.Take(10).Select(item => $"• {item.DisplayName} x{item.Amount}"));
                    if (items.Count > 10)
                        itemsList += $"\n... и еще {items.Count - 10} предметов";

                    var embed = new DiscordEmbed
                    {
                        title = "📥 Получение груза",
                        color = 65280, // зеленый цвет
                        timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                        fields = new List<DiscordEmbedField>
                {
                    new DiscordEmbedField { name = "👤 Игрок", value = $"{playerName} (`{playerId}`)", @inline = true },
                    new DiscordEmbedField { name = "📍 Сервер", value = fromServer.Name, @inline = true },
                    new DiscordEmbedField { name = "🎯 НПС Позиция", value = GetGridPosition(npcPosition), @inline = true },
                    new DiscordEmbedField { name = "📦 Предметы", value = itemsList.Length > 0 ? itemsList : "Нет предметов", @inline = false }
                }
                    };

                    SendDiscordMessage(embed);
                }
                catch (System.Exception ex)
                {
                    PluginInstance.PrintError($"Ошибка логирования получения: {ex.Message}");
                }
            }

            private static void SendDiscordMessage(DiscordEmbed embed)
            {
                if (!isEnabled) return;

                var payload = new DiscordPayload
                {
                    embeds = new List<DiscordEmbed> { embed }
                };

                string jsonPayload = JsonConvert.SerializeObject(payload);

                PluginInstance.webrequest.Enqueue(webhookUrl, jsonPayload, (code, response) =>
                {
                    if (code != 200 && code != 204)
                    {
                        PluginInstance.PrintWarning($"Discord webhook returned code {code}: {response}");
                    }
                }, PluginInstance, RequestMethod.POST, new Dictionary<string, string>
                {
                    ["Content-Type"] = "application/json"
                });
            }

            private static string GetGridPosition(Vector3 position)
            {
                return MapHelper.PositionToString(position);
            }

            // Классы для Discord API
            private class DiscordPayload
            {
                public string username { get; set; }
                public string avatar_url { get; set; }
                public List<DiscordEmbed> embeds { get; set; }
            }

            private class DiscordEmbedFooter
            {
                public string text { get; set; }
                public string icon_url { get; set; }
            }

            private class DiscordEmbed
            {
                public string title { get; set; }
                public string description { get; set; }
                public int color { get; set; }
                public string timestamp { get; set; }
                public List<DiscordEmbedField> fields { get; set; }
                public DiscordEmbedFooter footer { get; set; }
            }
            private class DiscordEmbedField
            {
                public string name { get; set; }
                public string value { get; set; }
                public bool @inline { get; set; }
            }

            // Вспомогательный класс для информации о предметах
            public class ItemInfo
            {
                public string DisplayName { get; set; }
                public int Amount { get; set; }
            }
        }

        public static class Vector3Extensions
        {
            public static Vector3 CenterPoint(IEnumerable<Vector3> points)
            {
                Vector3 sum = Vector3.zero;
                int count = 0;

                foreach (var point in points)
                {
                    sum += point;
                    count++;
                }

                return count > 0 ? sum / count : Vector3.zero;
            }
        }

        public class SpawnPoint
        {
            public int Id { get; }
            public Vector3 Position { get; }
            public string Name { get; }

            public SpawnPoint(int id, Vector3 position)
            {
                Id = id;
                Position = position;
            }

            public SpawnPoint(int id, Vector3 position, string name) : this(id, position)
            {
                Name = name;
            }

            public static List<SpawnPoint> ParseSpawnPoints(string spawnPointsString)
            {
                var spawnPoints = new List<SpawnPoint>();

                if (string.IsNullOrWhiteSpace(spawnPointsString))
                {
                    return spawnPoints; // Возвращаем пустой список, если входная строка пуста
                }

                // Разделяем по строковому разделителю " | "
                var points = spawnPointsString.Split(new[] { " | " }, StringSplitOptions.RemoveEmptyEntries);

                foreach (var point in points)
                {
                    try
                    {
                        var parts = point.Split(new[] { '[', ']', '(', ')' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length != 4 && parts.Length != 3) continue; // Skip invalid entries
                        int id = int.Parse(parts[0].Trim()); // Parse ID;
                        string name = parts.ElementAtOrDefault(3)?.Trim();
                        var coords = parts[1].Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                        Vector3 position = new Vector3(
                                float.Parse(coords[0].Trim()),
                                float.Parse(coords[1].Trim()),
                                float.Parse(coords[2].Trim())
                            );

                        SpawnPoint spawnPoint = new SpawnPoint(id, position, name);
                        spawnPoints.Add(spawnPoint);
                    }
                    catch (Exception)
                    {
                        // Обработка ошибки парсинга
                        Console.WriteLine($"Ошибка при обработке точки спауна: {point}");
                    }
                }

                return spawnPoints;
            }
        }

        public class MapImage
        {
            public int width;
            public int height;
            public string backgroundHex;
            public byte[] data;
            public uint crc;
        }

        // Сменяет айди содержимого контейнера
        private class RemapperUID
        {
            private RemappingType remappingType;
            public RemapperUID(RemappingType type)
            {
                this.remappingType = type;
            }

            public void Map(UidType type, ref ulong prevUid)
            {
                if (type == UidType.Clear)
                {
                    prevUid = 0UL;
                    return;
                }

                switch (this.remappingType)
                {
                    case RemappingType.Reset:
                        if (prevUid == 0UL)
                        {
                            return;
                        }
                        prevUid = 0UL;
                        break;
                    case RemappingType.New:
                        prevUid = Net.sv.TakeUID();
                        break;
                }
            }
        }

        // Модель сервера базы данных
        public class Server
        {
            public int DatabaseId { get; }
            public string IP { get; }
            public string Name { get; }

            public MapImage Map { get; private set; }
            public int WorldSize { get; private set; }
            public List<SpawnPoint> SpawnPoints { get; private set; }


            public Server(int id, string ip, string name)
            {
                this.DatabaseId = id;
                this.IP = ip;
                this.Name = name;
            }

            public static Server Get(Dictionary<string, object> values)
            {
                if (values["server_id"] is int id &&
                    values["server_ip"] is string ip &&
                    values["server_name"] is string name &&
                    values["map_width"] is int map_width &&
                    values["map_height"] is int map_height &&
                    values["map_background"] is string map_background &&
                    values["map_data"] is byte[] map_data &&
                    values["world_size"] is int world_size)
                {
                    return new Server(id, ip, name)
                    {
                        Map = new MapImage()
                        {
                            width = map_width,
                            height = map_height,
                            backgroundHex = map_background,
                            data = map_data,
                        },
                        WorldSize = world_size,
                        SpawnPoints = Convert.IsDBNull(values["spawnpoints"]) ? new List<SpawnPoint>() : SpawnPoint.ParseSpawnPoints((string)values["spawnpoints"])
                    };
                }
                return null;
            }

            public static Server Get(int id)
            {
                return PluginInstance.serverList.TryGetValue(id, out Server result) ? result : null;
            }

            public static Server Get(string ip)
            {
                return PluginInstance.serverList.Values.FirstOrDefault(s => s.IP == ip);
            }
        }


        #region Payment Providers
        private class EconomicsPaymentProvider : IPaymentProvider
        {
            private Plugin plugin => PluginInstance.Economics;

            public float GetBalance(BasePlayer player)
            {
                return Convert.ToSingle(plugin.Call("Balance", player.UserIDString));
            }

            public bool AddBalance(BasePlayer player, float amount)
            {
                var result = plugin.Call("Deposit", player.UserIDString, Convert.ToDouble(amount));
                return result is bool && (bool)result;
            }

            public bool TakeBalance(BasePlayer player, float amount, List<Item> collect)
            {
                var result = plugin.Call("Withdraw", player.UserIDString, Convert.ToDouble(amount));
                return result is bool && (bool)result;
            }
        }

        private class ScrapPaymentProvider : IPaymentProvider
        {
            private static readonly ItemDefinition SCRAP_DEFINITION = ItemManager.FindItemDefinition("scrap");

            public float GetBalance(BasePlayer player)
            {
                return player.inventory.GetAmount(SCRAP_DEFINITION);
            }

            public bool AddBalance(BasePlayer player, float amount)
            {
                return player.inventory.GiveItem(ItemManager.Create(SCRAP_DEFINITION, (int)amount));
            }

            public bool TakeBalance(BasePlayer player, float amount, List<Item> collect)
            {
                return player.inventory.Take(collect, SCRAP_DEFINITION.itemid, (int)amount) > 0;
            }
        }
        #endregion


        // Калькулятор калькулирует разные цены
        public static class PriceCalculator
        {
            public static float GetStorageCost(ContainerInfo containerInfo, Currency currency)
            {
                float multiplier = 1f;
                if (currency == Currency.Scrap)
                    multiplier = config.ExchangeRate;

                if (containerInfo == null)
                    return 0f;

                if (containerInfo.State == ContainerState.Idle || containerInfo.State == ContainerState.Delivered)
                {
                    int seconds = Epoch.Current - containerInfo.Timestamp;
                    float hours = seconds * 0.0002777f;
                    float paidHours = hours - databaseConfig.FreeStorageHours;
                    if (paidHours > 0)
                        return paidHours * databaseConfig.StorageCostPerHour * multiplier;
                }
                return 0f;
            }

            public static float GetDeliveryVariantPrice(DeliveryVariant variant, Currency currency)
            {
                float multiplier = 1f;
                if (currency == Currency.Scrap)
                    multiplier = config.ExchangeRate;

                if (databaseConfig.DeliveryVariantPrices.TryGetValue(variant, out float price))
                    return price * multiplier;
                return 0f;
            }

            public static float GetContentsPrice(ProtoBuf.ItemContainer container, Currency currency)
            {
                if (container == null || container.contents == null)
                    return 0f;

                float multiplier = 1f;
                if (currency == Currency.Scrap)
                    multiplier = config.ExchangeRate;

                float result = 0;
                foreach (ProtoBuf.Item item in container.contents)
                {
                    ItemDefinition itemDefinition = ItemManager.FindItemDefinition(item.itemid);
                    if (itemDefinition != null)
                    {
                        float price;
                        if (databaseConfig.DeliveryPricePerItem.TryGetValue(itemDefinition.shortname, out price) ||
                            databaseConfig.DeliveryPricePerCategory.TryGetValue(itemDefinition.category, out price))
                            result += price * item.amount;
                    }
                    result += GetContentsPrice(item.contents, currency); 
                }
                return result * multiplier;
            }

            public static float GetNewContainerPrice(Player player)
            {
                return GetContainerPrice(player.TotalActiveContainerCount, player.UI.CurrentCurrency);
            }

            public static float GetContainerPrice(int index, Currency currency)
            {
                float multiplier = 1f;
                if (currency == Currency.Scrap)
                    multiplier = config.ExchangeRate;

                List<DatabaseConfiguration.EachContainerProperties> properties = databaseConfig.ContainerProperties;
                return (properties.ElementAtOrDefault(index) ?? properties.LastOrDefault() ?? new DatabaseConfiguration.EachContainerProperties()).Price * multiplier;
            }
            
            public static float GetContainerSlotPrice(int index, Currency currency)
            {
                float multiplier = 1f;
                if (currency == Currency.Scrap)
                    multiplier = config.ExchangeRate;

                List<DatabaseConfiguration.EachContainerProperties> properties = databaseConfig.ContainerProperties;
                return (properties.ElementAtOrDefault(index) ?? properties.LastOrDefault() ?? new DatabaseConfiguration.EachContainerProperties()).SlotPrice * multiplier;
            }

            public static float GetTotalShippingPrice(Player player, ContainerInfo containerInfo)
            {
                return GetDeliveryVariantPrice(player.UI.DeliveryVariant, player.UI.CurrentCurrency) + GetContentsPrice(containerInfo.Proto, player.UI.CurrentCurrency) + GetStorageCost(containerInfo, player.UI.CurrentCurrency);
            }
        }

        // Модель контейнера базы данных
        public class ContainerInfo
        {
            public int? DatabaseId { get; set; }
            public ulong OwnerId { get; set; }
            public int SourceServerId { get; set; }
            public int? DestinationServerId { get; set; }
            public int SaveProtocol { get; set; }
            public DeliveryVariant DeliveryVariant { get; set; }
            public ContainerState State { get; set; }
            public ItemContainerId ContainerId { get; set; }
            public ProtoBuf.ItemContainer Proto { get; set; }
            public int Slots { get; set; }
            public int Capacity { get; set; }
            public int Timestamp { get; set; }

            public ContainerInfo()
            {
                SourceServerId = PluginInstance.currentServer.DatabaseId;
                Proto = new ProtoBuf.ItemContainer();
                SaveProtocol = Rust.Protocol.save;
                Timestamp = Epoch.Current;
            }

            public ContainerInfo(ulong ownerId) : this()
            {
                OwnerId = ownerId;
            }

            ~ContainerInfo()
            {
                Proto.Dispose();
            }

            public ContainerInfo(Dictionary<string, object> databaseQueryResult)
            {
                DatabaseId = (int)databaseQueryResult["id"];
                OwnerId = (ulong)(long)databaseQueryResult["ownerId"];
                Proto = ProtoBuf.ItemContainer.Deserialize((byte[])databaseQueryResult["data"]);
                Slots = (int)databaseQueryResult["slots"];
                Capacity = (int)databaseQueryResult["capacity"];
                SourceServerId = (int)databaseQueryResult["from-server-id"];
                DestinationServerId = (Convert.IsDBNull(databaseQueryResult["to-server-id"]) ? null : (int)databaseQueryResult["to-server-id"]);
                SaveProtocol = (int)databaseQueryResult["save-protocol"];
                DeliveryVariant = (DeliveryVariant)Enum.Parse(typeof(DeliveryVariant), (string)databaseQueryResult["variant"]);
                State = (ContainerState)Enum.Parse(typeof(ContainerState), (string)databaseQueryResult["state"]);
                Timestamp = (int)databaseQueryResult["timestamp"];
            }

            public ItemContainerId GenerateContainerId()
            {
                return (this.ContainerId = new ItemContainerId((ulong)UnityEngine.Random.Range(int.MinValue, int.MaxValue)));
            }

            public void SetProtoContainerFlags()
            {
                Proto.flags = 0;
                switch (State)
                {
                    case ContainerState.Idle:
                        break;
                    case ContainerState.InTransit:
                        Proto.flags |= (int)ItemContainer.Flag.NoItemInput;
                        Proto.flags |= (int)ItemContainer.Flag.IsLocked;
                        break;
                    case ContainerState.Delivered:
                        Proto.flags |= (int)ItemContainer.Flag.NoItemInput;
                        break;
                }
            }

            public void ToTransit(int destinationServerId, DeliveryVariant deliveryVariant)
            {
                SourceServerId = PluginInstance.currentServer.DatabaseId;
                DestinationServerId = destinationServerId;
                DeliveryVariant = deliveryVariant;
                SetProtoContainerFlags();
                Proto.InspectUids(new UidInspector<ulong>(new RemapperUID(RemappingType.Reset).Map));
                State = ContainerState.InTransit;
                Timestamp = Epoch.Current;
            }

            public void ToDelivered()
            {
                SetProtoContainerFlags();
                Proto.InspectUids(new UidInspector<ulong>(new RemapperUID(RemappingType.New).Map));
                State = ContainerState.Delivered;
                Timestamp = Epoch.Current;
            }

            public void ToCompleted()
            {
                State = ContainerState.Completed;
                Timestamp = Epoch.Current;
            }

            private void Insert()
            {
                if (DatabaseId.HasValue)
                    return;

                var rows = Query(__Sql(
                   @"INSERT INTO `containers` 
                            (`ownerId`, `data`, `slots`, `capacity`, `from-server-id`, `to-server-id`, `save-protocol`, `variant`, `state`, `timestamp`)
                            VALUES (@0, @1, @2, @3, @4, @5, @6, @7, @8, @9);
                            SELECT LAST_INSERT_ID() as inserted_id;",
                   OwnerId,
                   Proto.ToProtoBytes(),
                   Slots,
                   Capacity,
                   SourceServerId,
                   DestinationServerId,
                   SaveProtocol,
                   DeliveryVariant.ToString(),
                   State.ToString(),
                   Timestamp));

                if (rows == null || rows.Count == 0)
                    return;

                DatabaseId = (int)(UInt64)rows[0]["inserted_id"];
            }

            public void Save()
            {
                SetProtoContainerFlags();

                if (!DatabaseId.HasValue)
                {
                    Insert();
                    return;
                }

                NonQuery(__Sql(
                    @"UPDATE `containers` 
                            SET `ownerId` = @1, `data` = @2, `slots` = @3, `capacity` = @4, `from-server-id` = @5, `to-server-id` = @6, `save-protocol` = @7, `variant` = @8, `state` = @9, `timestamp` = @10 WHERE `id` = @0
                    ",
                    DatabaseId,
                    OwnerId,
                    Proto.ToProtoBytes(),
                    Slots,
                    Capacity,
                    SourceServerId,
                    DestinationServerId,
                    SaveProtocol,
                    DeliveryVariant.ToString(),
                    State.ToString(),
                    Timestamp));
            }
        }

        // Переводчик названий предметов
        public class Translator15 : PlayerAssociated
        {
            public static void Init(string language)
            {
                Dictionary<string, string> lang = new Dictionary<string, string>();

                TextAsset textAsset = FileSystem.Load<TextAsset>($"assets/localization/{language}/engine.json", true);
                if (textAsset == null)
                    return;

                Dictionary<string, string> @object = JsonConvert.DeserializeObject<Dictionary<string, string>>(textAsset.text);
                if (@object == null)
                    return;

                foreach (ItemDefinition itemDefinition in ItemManager.itemList)
                {
                    string key = itemDefinition.displayName.token;
                    if (@object.ContainsKey(key))
                        lang[key] = @object[key];
                }

                translations[language] = lang;
            }

            public Translator15(ulong userID) : base(userID) { }

            private static Dictionary<string, Dictionary<string, string>> translations = new Dictionary<string, Dictionary<string, string>>();

            public class Phrase : Translate.Phrase
            {
                private Translator15 translator;

                public Phrase(Translator15 translator)
                {
                    this.translator = translator;
                }

                public override string translated
                {
                    get
                    {
                        if (string.IsNullOrEmpty(this.token))
                        {
                            return this.english;
                        }
                        return translator.Get(this.token, this.english);
                    }
                }
            }

            public Translator15.Phrase Convert(Translate.Phrase phrase)
            {
                if (phraseCache.TryGetValue(phrase, out Translator15.Phrase phrase1))
                    return phrase1;
                else
                    return phraseCache[phrase] = new Translator15.Phrase(this) { english = phrase.english, token = phrase.token };
            }

            private string GetLanguage()
            {
                return PluginInstance.lang.GetLanguage(UserId.ToString());
            }

            public string Get(string key, string def = null)
            {
                if (def == null)
                    def = "#" + key;
                if (string.IsNullOrEmpty(key))
                    return def;
                string language = GetLanguage();
                if (!Translator15.translations.ContainsKey(language))
                    Translator15.Init(language);
                if (Translator15.translations[language].TryGetValue(key, out string result))
                    return result;
                return def;
            }

            private Dictionary<Translate.Phrase, Translator15.Phrase> phraseCache = new Dictionary<Translate.Phrase, Phrase>();
        }

        // Класс игрока для плагина
        public class Player : PlayerAssociated, IDisposable
        {
            public Dictionary<string, object> data = new Dictionary<string, object>
            {

            };

            public PlayerUI UI { get; }
            public CargoNPC NPC { get; private set; }

            public List<ContainerInfo> CachedGlobalContainers { get; private set; } = new List<ContainerInfo>();
            public List<ContainerInfo> CachedServerContainers { get; private set; } = new List<ContainerInfo>();
            public ContainerInfo OrderContainer { get; private set; }

            private Dictionary<int, Dictionary<ulong, int>> containerSnapshot = new Dictionary<int, Dictionary<ulong, int>>();

            public Player(BasePlayer player) : base(player)
            {
                this.UI = new PlayerUI(this.Player, this);
                this.Load();
            }

            public IPaymentProvider paymentProvider => this.UI.CurrentCurrency switch
            {
                Currency.Scrap => new ScrapPaymentProvider(),
                Currency.Coins => new EconomicsPaymentProvider(),
                _ => null
            };

            public void OnPlayerDisconnected()
            {
                this.UI.MarkInActive();
            }

            // Обновление кэша контейнеров из бд, вызов каллбека
            public void FetchContainers(Action<List<ContainerInfo>> callback)
            {
                Sql sql = __Sql(@"
                    SELECT 
                        c.*
                    FROM 
                        `containers` c");

                sql.Where("c.`ownerId` = @0", UserId);
                if (databaseConfig.VisibileOnlyCurrentProtocol)
                    sql.Where("c.`save-protocol` = @0", Rust.Protocol.save);

                Query(sql, list =>
                {
                    if (list == null)
                        return;

                    CachedGlobalContainers.Clear();
                    CachedServerContainers.Clear();
                    OrderContainer = null;

                    foreach (Dictionary<string, object> values in list)
                    {
                        ContainerInfo container = new ContainerInfo(values);
                        CachedGlobalContainers.Add(container);

                        if (container.SourceServerId != PluginInstance.currentServer.DatabaseId)
                            continue;

                        if (container.State == ContainerState.Idle)
                            OrderContainer = container;

                        CachedServerContainers.Add(container);

                    }

                    if (callback != null)
                        callback(CachedGlobalContainers);
                });
            }

            public ContainerInfo FindContainer(ItemContainerId itemContainerId)
            {
                foreach (ContainerInfo info in CachedGlobalContainers)
                {
                    if (info.ContainerId == itemContainerId)
                        return info;
                }
                return null;
            }

            public int TotalActiveContainerCount
            {
                get
                {
                    int result = 0;
                    foreach (ContainerInfo containerInfo in CachedGlobalContainers)
                    {
                        if (containerInfo.State == ContainerState.Idle || containerInfo.State == ContainerState.InTransit)
                        {
                            result++;
                            continue;
                        }

                        if (containerInfo.State == ContainerState.Delivered && containerInfo.Slots > 0)
                        {
                            result++;
                            continue;
                        }
                    }
                    return result;
                }
            }

            public int MaxActiveContainerCount
            {
                get
                {
                    return databaseConfig.PlayerMaxActiveContainerCount;
                }
            }


            // Подгрузка данных из бд
            public void Load()
            {
                FetchContainers(null);
            }

            // Назначение NPC и открытие и UI (вызывается при взаемодействии с NPC)
            public void StartDialogWithNPC(CargoNPC npc)
            {
                this.NPC = npc;
                this.FetchContainers(list => this.UI.Open());
            }

            // Есть ли контейнер для оформления заказа
            public bool HasOrderContainer()
            {
                return OrderContainer != null;
            }

            // Есть ли лимит по контейнерам у игрока
            public bool IsLimitReached()
            {
                return TotalActiveContainerCount >= MaxActiveContainerCount;
            }

            // Отправка контейнера в путь
            public void SendOrderContainer(int destinationServerId, DeliveryVariant deliveryVariant)
            {
                if (OrderContainer == null) return;

                Server sourceServer = PluginInstance.currentServer;
                Server destinationServer = Server.Get(destinationServerId);

                // Собираем информацию о предметах для логирования ТОЛЬКО если НЕТ телепортации
                // (при телепортации логирование уже происходит в OnSendButtonClick)
                if (this.UI.SelectedSpawnPoint == null)
                {
                    var items = new List<DiscordLogger.ItemInfo>();
                    if (OrderContainer.Proto?.contents != null)
                    {
                        foreach (var protoItem in OrderContainer.Proto.contents)
                        {
                            var itemDef = ItemManager.FindItemDefinition(protoItem.itemid);
                            if (itemDef != null)
                            {
                                var translator = PluginInstance.translators.GetOrCreate(UserId);
                                items.Add(new DiscordLogger.ItemInfo
                                {
                                    DisplayName = translator.Convert(itemDef.displayName).translated,
                                    Amount = protoItem.amount
                                });
                            }
                        }
                    }

                    float totalCost = PriceCalculator.GetTotalShippingPrice(this, OrderContainer);
                    Vector3 npcPosition = this.NPC?.transform.position ?? Vector3.zero;

                    // Логирование обычной отправки (без телепортации)
                    DiscordLogger.LogShipment(
                        UserId,
                        Player.displayName,
                        sourceServer,
                        destinationServer,
                        npcPosition,
                        items,
                        totalCost,
                        deliveryVariant
                    );
                }

                OrderContainer.ToTransit(destinationServerId, deliveryVariant);
                OrderContainer.Save();
                OrderContainer = null;
            }

            // Метод для создания снимка контейнера
            private void CreateContainerSnapshot(int containerId, ItemContainer container)
            {
                var snapshot = new Dictionary<ulong, int>(); // key: itemid+skinid hash, value: amount

                foreach (Item item in container.itemList)
                {
                    // Создаем уникальный ключ из itemid и skinid
                    ulong itemKey = GetItemKey(item.info.itemid, item.skin);

                    if (snapshot.ContainsKey(itemKey))
                        snapshot[itemKey] += item.amount;
                    else
                        snapshot[itemKey] = item.amount;
                }

                containerSnapshot[containerId] = snapshot;
            }

            // Метод для создания уникального ключа предмета
            private ulong GetItemKey(int itemId, ulong skinId)
            {
                return ((ulong)itemId << 32) | (skinId & 0xFFFFFFFF);
            }

            // Обновляет данные о предметах и слотах, после вызывает сохранение в базе данных
            public void UpdateContainerData(ContainerInfo containerInfo, ProtoBuf.ItemContainer itemContainer)
            {
                containerInfo.Proto = itemContainer;
                containerInfo.Slots = itemContainer.contents?.Count ?? 0;

                if (containerInfo.State == ContainerState.Delivered && containerInfo.Slots == 0)
                    containerInfo.ToCompleted();

                containerInfo.Save();
            }

            public bool TryBuyContainer()
            {
                if (HasOrderContainer() || IsLimitReached())
                    return false;

                IPaymentProvider paymentProvider = this.paymentProvider;
                if (paymentProvider == null)
                    return false;

                float price = PriceCalculator.GetNewContainerPrice(this);
                if (price > 0 && !paymentProvider.TakeBalance(this.Player, price, null))
                {
                    this.UI.Notification_NotEnoughFunds();
                    return false;
                }

                OrderContainer = new ContainerInfo(this.UserId);
                int index = TotalActiveContainerCount;
                List<DatabaseConfiguration.EachContainerProperties> properties = databaseConfig.ContainerProperties;
                OrderContainer.Capacity = (properties.ElementAtOrDefault(index) ?? properties.LastOrDefault() ?? new DatabaseConfiguration.EachContainerProperties()).StartCapacity;
                OrderContainer.Save();
                CachedServerContainers.Add(OrderContainer);
                CachedGlobalContainers.Add(OrderContainer);
                this.UI.UpdateBody();
                return true;
            }

            public bool TryBuyOrderContainerSlot()
            {
                int containerIndex = TotalActiveContainerCount - 1;

                List<DatabaseConfiguration.EachContainerProperties> properties = databaseConfig.ContainerProperties;
                if (!HasOrderContainer() || OrderContainer.Capacity >= (properties.ElementAtOrDefault(containerIndex) ?? properties.LastOrDefault() ?? new DatabaseConfiguration.EachContainerProperties()).MaxCapacity)
                    return false;

                PlayerLoot playerLoot = this.Player.inventory.loot;
                ItemContainer itemContainer = playerLoot?.FindContainer(OrderContainer.ContainerId);
                if (playerLoot == null || itemContainer == null)
                    return false;

                IPaymentProvider paymentProvider = this.paymentProvider;
                if (paymentProvider == null)
                    return false;

                float price = PriceCalculator.GetContainerSlotPrice(containerIndex, this.UI.CurrentCurrency);
                if (price > 0 && !paymentProvider.TakeBalance(this.Player, price, null))
                {
                    this.UI.Toast_NotEnoughFunds();
                    return false;
                }


                OrderContainer.Capacity++;
                OrderContainer.Save();
                playerLoot.Clear();
                playerLoot.Invoke(() => OpenContainer(OrderContainer), 0.2f);
                return true;
            }

            public void OpenOrderContainer()
            {
                if (OrderContainer == null)
                    return;

                OpenContainer(OrderContainer);
            }

            public ItemContainer OpenContainer(ContainerInfo containerInfo)
            {
                if (containerInfo == null)
                    return null;

                ProtoBuf.ItemContainer protoContainer = containerInfo.Proto;
                if (protoContainer == null)
                    return null;

                protoContainer.ShouldPool = false;
                if (protoContainer.contents == null)
                    protoContainer.contents = Pool.Get<List<ProtoBuf.Item>>();
                protoContainer.slots = containerInfo.Capacity;

                ItemContainer itemContainer = new ItemContainer();
                itemContainer.isServer = true;
                itemContainer.Load(protoContainer);
                for (int i = 0; i < itemContainer.itemList.Count; i++)
                {
                    var weapon = itemContainer.itemList[i].GetHeldEntity() as BaseProjectile;
                    if (weapon.IsValid())
                    {
                        weapon.primaryMagazine.contents = protoContainer.contents[i].ammoCount - 1;
                        weapon.ForceModsChanged();
                    }
                }

                itemContainer.entityOwner = this.NPC;
                itemContainer.uid = containerInfo.GenerateContainerId();
                itemContainer.SetLocked(containerInfo.State == ContainerState.InTransit || containerInfo.State == ContainerState.Completed || (containerInfo.State == ContainerState.Delivered && containerInfo.DestinationServerId != containerInfo.SourceServerId && containerInfo.DestinationServerId != PluginInstance.currentServer.DatabaseId));

                CreateContainerSnapshot(containerInfo.DatabaseId.Value, itemContainer);

                this.NPC.OpenContainer(this.Player, itemContainer);
                this.UI.Destroy();
                this.UI.CargoBuySlotButton();
                return itemContainer;
            }

            private List<DiscordLogger.ItemInfo> CalculateItemDifference(Dictionary<ulong, int> oldSnapshot, Dictionary<ulong, int> newSnapshot)
            {
                var difference = new List<DiscordLogger.ItemInfo>();

                foreach (var kvp in oldSnapshot)
                {
                    ulong itemKey = kvp.Key;
                    int oldAmount = kvp.Value;
                    int newAmount = newSnapshot.ContainsKey(itemKey) ? newSnapshot[itemKey] : 0;

                    int amountDiff = oldAmount - newAmount;
                    if (amountDiff > 0)
                    {
                        // Декодируем itemKey обратно в itemId и skinId
                        int itemId = (int)(itemKey >> 32);
                        ulong skinId = itemKey & 0xFFFFFFFF;

                        var itemDef = ItemManager.FindItemDefinition(itemId);
                        if (itemDef != null)
                        {
                            var translator = PluginInstance.translators.GetOrCreate(UserId);
                            string displayName = translator.Convert(itemDef.displayName).translated;

                            if (skinId > 0)
                            {
                                displayName += $" (Скин: {skinId})";
                            }

                            difference.Add(new DiscordLogger.ItemInfo
                            {
                                DisplayName = displayName,
                                Amount = amountDiff
                            });
                        }
                    }
                }

                return difference;
            }

            // Сохранение контейнера после закрытия
            public void OnPlayerLootEnd()
            {
                PlayerLoot playerLoot = this.Player.inventory.loot;
                ItemContainer container = playerLoot.containers[0];
                ContainerInfo containerInfo = this.CachedGlobalContainers.Find(c => c.ContainerId == container.uid);

                if (containerInfo != null && containerInfo.DatabaseId.HasValue)
                {
                    int containerId = containerInfo.DatabaseId.Value;

                    // Проверяем, есть ли снимок для этого контейнера
                    if (containerSnapshot.ContainsKey(containerId))
                    {
                        // Создаем текущий снимок контейнера
                        var currentSnapshot = new Dictionary<ulong, int>();
                        foreach (Item item in container.itemList)
                        {
                            ulong itemKey = GetItemKey(item.info.itemid, item.skin);
                            if (currentSnapshot.ContainsKey(itemKey))
                                currentSnapshot[itemKey] += item.amount;
                            else
                                currentSnapshot[itemKey] = item.amount;
                        }

                        // Сравниваем снимки и определяем разницу
                        var removedItems = CalculateItemDifference(containerSnapshot[containerId], currentSnapshot);
                        var addedItems = CalculateItemDifference(currentSnapshot, containerSnapshot[containerId]);

                        // Логируем только если были изменения
                        if (removedItems.Count > 0 || addedItems.Count > 0)
                        {
                            Vector3 npcPosition = this.NPC?.transform.position ?? Vector3.zero;
                            Server currentServer = PluginInstance.currentServer;

                            DiscordLogger.LogContainerInteraction(
                                UserId,
                                Player.displayName,
                                currentServer,
                                npcPosition,
                                removedItems,
                                addedItems,
                                containerInfo
                            );
                        }

                        // Удаляем снимок после использования
                        containerSnapshot.Remove(containerId);
                    }

                    UpdateContainerData(containerInfo, container.Save(true));
                }

                CuiHelper.DestroyUi(this.Player, "CargoBuySlotButton");
                this.UI.Open();
                return;
            }

            public void Dispose()
            {
                this.UI.Dispose();
            }
        }


        public class PlayerUI : PlayerAssociated, IDisposable
        {
            public enum Category
            {
                Main,
                CreateOrder,
                Travel,
                Admin
            }

            private Player player;
            private bool active;

            public bool IsActive
            {
                get
                {
                    return active;
                }
            }

            public Currency CurrentCurrency { get; set; } = Currency.Coins;
            public Category CurrentCategory { get; private set; } = Category.Main;
            public Server DestinationServer { get; set; } = null;
            public DeliveryVariant DeliveryVariant { get; set; } = DeliveryVariant.Standard;
            public SpawnPoint SelectedSpawnPoint { get; set; } = null;
            public ItemCategory selectorCategory { get; set; } = ItemCategory.All;
            public Action<int> SelectorAction { get; set; } = null;
            public bool HideCompleted { get; set; } = true;
            public int NpcIndex { get; set; } = 0;
            public int SpawnPointIndex { get; set; } = 0;

            private readonly LocalizationText КАРГО = "КАРГО";
            private readonly LocalizationText ВЫБЕРЕТЕ_СЕРВЕР = "ВЫБЕРЕТЕ СЕРВЕР";
            private readonly LocalizationText СТОИМОСТЬ_ОТПРАВКИ = "СТОИМОСТЬ ОТПРАВКИ";
            private readonly LocalizationText ВАРИАНТЫ_ДОСТАВКИ = "ВАРИАНТЫ ДОСТАВКИ";
            private readonly LocalizationText ВЫБРАТЬ_ПРЕДМЕТЫ = "ВЫБРАТЬ ПРЕДМЕТЫ";
            private readonly LocalizationText УДАЛИТЬ_КОНТЕЙНЕР = "УДАЛИТЬ КОНТЕЙНЕР";
            private readonly LocalizationText ОТПРАВИТЬ = "ОТПРАВИТЬ";
            private readonly LocalizationText ОТПРАВИТЬСЯ_ВМЕСТЕ_С_ГРУЗОМ = "ОТПРАВИТЬСЯ ВМЕСТЕ С ГРУЗОМ";
            private readonly LocalizationText ОТПРАВИТЬСЯ_ВМЕСТЕ_С_ГРУЗОМ_ОПИСАНИЕ = "При отправке вместе с грузом вы будете перемещены на другой сервер на выбраную вами точку, при вас останется ваша одежда.\n<b><color=#b33927>ОСТАЛЬНЫЕ ПРЕДМЕТЫ БУДУТ УДАЛЕНЫ!</color></b>";
            private readonly LocalizationText СТАНДАРТНАЯ = "СТАНДАРТНАЯ";
            private readonly LocalizationText ЭКСПРЕСС = "ЭКСПРЕСС";
            private readonly LocalizationText ОФОРМЛЕНИЕ_ЗАКАЗА = "Оформление заказа";
            private readonly LocalizationText В_ПУТИ = "В пути (%TIME_LEFT%)";
            private readonly LocalizationText ФОРМАТ_ОТСЧЕТА_ВРЕМЕНИ = "%h'ч 'mm'м'";
            private readonly LocalizationText ДОСТАВЛЕНО = "Доставлено";
            private readonly LocalizationText ЗАВЕРШЕН = "Завершен";
            private readonly LocalizationText ВСЕ = "ВСЕ";
            private readonly LocalizationText ЗАКАЗАТЬ = "ЗАКАЗАТЬ";
            private readonly LocalizationText АДМИНКА = "АДМИНКА";
            private readonly LocalizationText TRAVEL = "ОТПРАВИТЬСЯ НА ДРУГОЙ КОНТИНЕНТ";
            private readonly LocalizationText КУПИТЬ = "КУПИТЬ";
            private readonly LocalizationText НЕИЗВЕСТНО = "Неизвестно";
            private readonly LocalizationText ОТСУТСТВУЕТ = "Отсутствует";
            private readonly LocalizationText ПУСТО = "Пусто";
            private readonly LocalizationText ОСМОТРЕТЬ = "ОСМОТРЕТЬ";
            private readonly LocalizationText ОТКУДА = "Откуда";
            private readonly LocalizationText КУДА = "Куда";
            private readonly LocalizationText CОСТОЯНИЕ = "Cостояние";
            private readonly LocalizationText ПРЕДМЕТЫ = "Предметы";
            private readonly LocalizationText СТОИМОСТЬ_ХРАНЕНИЯ = "Стоимость хранения";
            private readonly LocalizationText НЕ_ВЫБРАН_СЕРВЕР = "Не выбран сервер";
            private readonly LocalizationText НЕ_ВЫБРАНА_ТОЧКА_СПАВНА = "Не выбрана точка спавна";
            private readonly LocalizationText НЕ_ВЫБРАНЫ_ПРЕДМЕТЫ = "Не выбраны предметы";
            private readonly LocalizationText НЕДОСТАТОЧНО_СРЕДСТВ = "Недостаточно средств";
            private readonly LocalizationText ОПЛАТА_ПРИ_ОТПРАВКЕ = "Оплата при отправке";
            private readonly LocalizationText МОНЕТЫ = "МОНЕТЫ";
            private readonly LocalizationText СКРАП = "СКРАП";
            private readonly LocalizationText МОНЕТ = "монет";
            private readonly LocalizationText СКРАПА = "скрапа";
            private readonly LocalizationText ФОРМАТ_НЕОБХОДИМО_КУПИТЬ_КОНТЕЙНЕР = "Для начала оформления заказа вам необходимо купить контейнер.\nЕго стоимость: {0}";
            private readonly LocalizationText ФОРМАТ_N_ВАЛЮТЫ = "{0:F2} {1}";
            private readonly LocalizationText ФОРМАТ_ПРЕДМЕТЫ_N_ВАЛЮТЫ = "• Предметы: {0}";
            private readonly LocalizationText ФОРМАТ_ДОСТАВКА_N_ВАЛЮТЫ = "• Доставка: {0}";
            private readonly LocalizationText ФОРМАТ_ХРАНЕНИЕ_N_ВАЛЮТЫ = "• Хранение: {0}";
            private readonly LocalizationText ФОРМАТ_ОБЩАЯ_СТОИМОСТЬ_N_ВАЛЮТЫ = "ОБЩАЯ СТОИМОСТЬ: {0}";
            private readonly LocalizationText ДОСТИГНУТ_ЛИМИТ = "Вы достигли лимита на количество единовременных контейнеров.\nВам стоит разобрать уже имеющиеся контейнеры.";
            private readonly LocalizationText ЧЕРНЫЙ_СПИСОК_ПРЕДМЕТОВ = "ЧЕРНЫЙ СПИСОК ПРЕДМЕТОВ";
            private readonly LocalizationText ПРЕДМЕТЫ_НПС = "ПРЕДМЕТЫ НПС";
            private readonly LocalizationText ЦЕНА_ЗА_ОТПРАВКУ_ПРЕДМЕТА_ИЗ_КАТЕГОРИИ = "ЦЕНА ЗА ОТПРАВКУ ПРЕДМЕТА ИЗ КАТЕГОРИИ";
            private readonly LocalizationText ЦЕНА_ЗА_ОТПРАВКУ_ПРЕДМЕТА = "ЦЕНА ЗА ОТПРАВКУ ПРЕДМЕТА";
            private readonly LocalizationText ЦЕНА_ЗА_ОТПРАВКУ_ПРЕДМЕТА_LOWER = "Цена за отправку предмета";
            private readonly LocalizationText ЦЕНА_ЗА_ОТПРАВКУ_ПРЕДМЕТА_ИЗ_КАТЕГОРИИ_LOWER = "Цена за отправку предмета из категории";
            private readonly LocalizationText НАСТРОЙКИ_НА_КАЖДЫЙ_КОНТЕЙНЕР = "НАСТРОЙКИ НА КАЖДЫЙ КОНТЕЙНЕР";
            private readonly LocalizationText ОТКРЫТЬ = "ОТКРЫТЬ";
            private readonly LocalizationText УСТАНОВИТЬ = "УСТАНОВИТЬ";
            private readonly LocalizationText ИМЯ = "Имя";
            private readonly LocalizationText НПС = "НПС";
            private readonly LocalizationText ЦЕНЫ_ЗА_ОТПРАВКУ = "ЦЕНЫ ЗА ОТПРАВКУ";
            private readonly LocalizationText СТОИМОСТЬ_ХРАНЕНИЯ_ЗА_ЧАС = "Стоимость хранения за час";
            private readonly LocalizationText СКОЛЬКО_ЧАСОВ_БЕСПЛАТНОГО_ХРАНЕНИЯ = "Сколько часов бесплатного хранения";
            private readonly LocalizationText ХРАНЕНИЕ = "ХРАНЕНИЕ";
            private readonly LocalizationText ВРЕМЯ_В_СЕКУНДАХ_ДЛЯ_ВАРИАНТОВ_ДОСТАВКИ = "Время в секундах для вариантов доставки";
            private readonly LocalizationText ЦЕНА_ВАРИАНТОВ_ДОСТАВКИ = "Цена вариантов доставки";
            private readonly LocalizationText ЧЕРНЫЙ_СПИСОК = "Черный список";
            private readonly LocalizationText ДОСТУПНОСТЬ_ПРЕДМЕТОВ = "ДОСТУПНОСТЬ ПРЕДМЕТОВ";
            private readonly LocalizationText НАСТРОЙКИ_НА_КАЖДЫЙ_КОНТЕЙНЕР_Н = "Настройки на каждый контейнер";
            private readonly LocalizationText МАКСИМАЛЬНОЕ_КОЛИЧЕСТВО_КОНТЕЙНЕРОВ_ДЛЯ_ИГРОКА = "Максимальное количество контейнеров для игрока";
            private readonly LocalizationText ДЛЯ_ИГРОКА = "ДЛЯ ИГРОКА";
            private readonly LocalizationText СКРЫТЬ_ЗАВЕРШЕННЫЕ = "СКРЫТЬ ЗАВЕРШЕННЫЕ";

            private static readonly FieldInfo[] LocalizationText_Fields = typeof(PlayerUI).GetFields(BindingFlags.Instance | BindingFlags.NonPublic).Where(field => field.FieldType == typeof(LocalizationText)).ToArray();


            public string GetCurrencyName()
            {
                switch (CurrentCurrency)
                {
                    case Currency.Scrap:
                        return СКРАП;
                    case Currency.Coins:
                        return МОНЕТЫ;
                    default:
                        return НЕИЗВЕСТНО;
                }
            }
            
            public PlayerUI(BasePlayer basePlayer, Player player) : base(basePlayer)
            {
                this.player = player;


                // Присвоение игрока для текстов, для дальйнейшего легкого перевода через ToString
                foreach (FieldInfo field in LocalizationText_Fields)
                {
                    LocalizationText localizationText = (LocalizationText)field.GetValue(this);
                    localizationText.Assign(this.Player);
                }
            }

            public Connection connection => Connection;

            public void Dispose()
            {
                Destroy();
            }

            public void MarkInActive()
            {
                active = false;
            }

            public void Destroy(string elementName)
            {
                CommunityEntity.ServerInstance.ClientRPC<string>(RpcTarget.Player("DestroyUI", Connection), elementName);
            }

            public void Destroy()
            {
                Destroy("Cargo UI");

                if (this.CurrentCategory == Category.Admin)
                {
                    PluginInstance.SaveConfig();
                    databaseConfig.Save();
                }


                active = false;
            }

            public void Open()
            {
                CargoUI();

                active = true;
            }

            public string EnumToString<T>(T @enum) where T : Enum
            {
                if (@enum is DeliveryVariant deliveryVariant)
                {
                    switch (deliveryVariant)
                    {
                        case DeliveryVariant.Standard:
                            return СТАНДАРТНАЯ;
                        case DeliveryVariant.Express:
                            return ЭКСПРЕСС;
                    }
                }
                else if (@enum is ContainerState containerState)
                {
                    switch (containerState)
                    {
                        case ContainerState.Idle:
                            return ОФОРМЛЕНИЕ_ЗАКАЗА;
                        case ContainerState.InTransit:
                            return В_ПУТИ;
                        case ContainerState.Delivered:
                            return ДОСТАВЛЕНО;
                        case ContainerState.Completed:
                            return ЗАВЕРШЕН;
                    }
                }
                else if (@enum is Category category)
                {
                    switch (category)
                    {
                        case Category.Main:
                            return ВСЕ;
                        case Category.CreateOrder:
                            return ЗАКАЗАТЬ;
                        case Category.Travel:
                            return TRAVEL;
                        case Category.Admin:
                            return АДМИНКА;
                    }
                }
                return @enum.ToString();
            }

            private string GetPriceString(float amount)
            {
                string currencyName;
                switch (CurrentCurrency)
                {
                    case Currency.Scrap:
                        currencyName = СКРАПА;
                        break;
                    case Currency.Coins:
                        currencyName = МОНЕТ;
                        break;
                    default:
                        currencyName = "валюты";
                        break;

                }
                return string.Format(ФОРМАТ_N_ВАЛЮТЫ, amount, currencyName);
            }

            public void UpdateAdminContent()
            {
                CUI.Root root = new CUI.Root("Admin Content");
                AdminContentScrollContent(root);
                root.Render(connection);
            }

            public void Notification_NotEnoughFunds()
            {
                CargoUI_Notification(НЕДОСТАТОЧНО_СРЕДСТВ, destroyTime: 3f);
            }

            public void Toast_NotEnoughFunds()
            {
                this.Player.ShowToast(GameTip.Styles.Red_Normal, НЕДОСТАТОЧНО_СРЕДСТВ.ToString());
            }

            public void GoToCategory(Category category)
            {
                if (category != Category.Admin && this.CurrentCategory == Category.Admin)
                    databaseConfig.Save();

                this.CurrentCategory = category;
                switch (category)
                {
                    case Category.Main:
                        UpdateBodyWithFetch();
                        break;
                    default:
                        UpdateBody();
                        break;
                }
            }


            public void UpdateBodyWithFetch()
            {
                this.player.FetchContainers(list => UpdateBody());
            }

            public void UpdateBody()
            {
                CUI.Root root = new CUI.Root("CargoUI_Body");
                Categories(root);
                AddContent(root);
                root.Render(Connection);
            }

            public void OnTravelSendButtonClick()
            {
                Server destinationServer = this.DestinationServer;
                if (destinationServer == null)
                {
                    CargoUI_Notification(НЕ_ВЫБРАН_СЕРВЕР);
                    return;
                }
                SpawnPoint spawnPoint = this.SelectedSpawnPoint;
                if (spawnPoint == null)
                {
                    CargoUI_Notification(НЕ_ВЫБРАНА_ТОЧКА_СПАВНА);
                    return;
                }

                Server currentServer = PluginInstance.currentServer;
                Vector3 npcPosition = this.player.NPC?.transform.position ?? Vector3.zero;

                DiscordLogger.LogPlayerTeleport(
                         this.UserId,
                         this.Player.displayName,
                         currentServer,
                         destinationServer,
                         this.SelectedSpawnPoint,
                         npcPosition
                     );

                ProtoBuf.ItemContainer container = this.Player.inventory.containerWear.Save(false);
                container.InspectUids(new RemapperUID(RemappingType.Reset).Map);
                NonQuery(__Sql("INSERT INTO player_transfer (ownerId, serverId, wearable_data, metabolism_data, spawnpointId) VALUES (@0, @1, @2, @3, @4)", this.UserId, destinationServer.DatabaseId, container.ToProtoBytes(), this.Player.metabolism.Save().ToProtoBytes(), this.SelectedSpawnPoint?.Id), rowsAffected =>
                {
                    string[] split = destinationServer.IP.Split(':');
                    global::ConsoleNetwork.SendClientCommandImmediate(connection, "nexus.redirect", new object[]
                    {
                            split[0],
                            split[1],
                            string.Empty
                    });

                    this.Player.Teleport(Vector3.zero);
                    this.Player.Die();
                });
            }

            public void OnSendButtonClick()
            {
                Server destinationServer = this.DestinationServer;
                if (destinationServer == null)
                {
                    CargoUI_Notification(НЕ_ВЫБРАН_СЕРВЕР);
                    return;
                }

                if (this.player.OrderContainer.Slots < 1)
                {
                    CargoUI_Notification(НЕ_ВЫБРАНЫ_ПРЕДМЕТЫ);
                    return;
                }

                DeliveryVariant deliveryVariant = this.DeliveryVariant;

                float price = PriceCalculator.GetTotalShippingPrice(this.player, this.player.OrderContainer);
                bool takenFunds = false;

                IPaymentProvider paymentProvider = player.paymentProvider;
                if (paymentProvider == null)
                    return;

                try
                {
                    if (price > 0 && !paymentProvider.TakeBalance(this.Player, price, null))
                    {
                        Notification_NotEnoughFunds();
                        return;
                    }
                    takenFunds = true;

                    // Собираем информацию о предметах для логирования ПЕРЕД отправкой
                    var items = new List<DiscordLogger.ItemInfo>();
                    if (this.player.OrderContainer.Proto?.contents != null)
                    {
                        foreach (var protoItem in this.player.OrderContainer.Proto.contents)
                        {
                            var itemDef = ItemManager.FindItemDefinition(protoItem.itemid);
                            if (itemDef != null)
                            {
                                var translator = PluginInstance.translators.GetOrCreate(this.UserId);
                                items.Add(new DiscordLogger.ItemInfo
                                {
                                    DisplayName = translator.Convert(itemDef.displayName).translated,
                                    Amount = protoItem.amount
                                });
                            }
                        }
                    }

                    Server currentServer = PluginInstance.currentServer;
                    Vector3 npcPosition = this.player.NPC?.transform.position ?? Vector3.zero;

                    this.player.SendOrderContainer(destinationServer.DatabaseId, deliveryVariant);

                    if (SelectedSpawnPoint != null)
                    {
                        // Логируем телепортацию с грузом
                        DiscordLogger.LogPlayerTeleport(
                            this.UserId,
                            this.Player.displayName,
                            currentServer,
                            destinationServer,
                            this.SelectedSpawnPoint,
                            npcPosition,
                            items,
                            price,
                            deliveryVariant
                        );

                        ProtoBuf.ItemContainer container = this.Player.inventory.containerWear.Save(false);
                        container.InspectUids(new RemapperUID(RemappingType.Reset).Map);
                        NonQuery(__Sql("INSERT INTO player_transfer (ownerId, serverId, wearable_data, metabolism_data, spawnpointId) VALUES (@0, @1, @2, @3, @4)", this.UserId, destinationServer.DatabaseId, container.ToProtoBytes(), this.Player.metabolism.Save().ToProtoBytes(), this.SelectedSpawnPoint?.Id), rowsAffected =>
                        {
                            string[] split = destinationServer.IP.Split(':');
                            global::ConsoleNetwork.SendClientCommandImmediate(connection, "nexus.redirect", new object[]
                            {
                            split[0],
                            split[1],
                            string.Empty
                            });

                            this.Player.Teleport(Vector3.zero);
                            this.Player.Die();
                        });
                    }
                    else
                    {
                        // Обычная отправка груза без телепортации (уже логируется в SendOrderContainer)
                        this.GoToCategory(Category.Main);
                    }
                }
                catch (Exception ex)
                {
                    UnityEngine.Debug.LogException(ex);
                    if (price > 0 && takenFunds)
                        paymentProvider.AddBalance(this.Player, price);
                }
            }

            public void CargoUI_Notification(string text, int fontSize = 18, TextAnchor align = TextAnchor.MiddleCenter, float destroyTime = 2f)
            {
                CUI.Root root = new CUI.Root("Cargo UI");
                {
                    CUI.Element nlPszg = root.AddContainer(
                        anchorMin: "0.5 1",
                        anchorMax: "0.5 1",
                        offsetMin: "-175 -70",
                        offsetMax: "175 -20",
                        name: "CargoUI_Notification").AddDestroySelfAttribute();
                    nlPszg = nlPszg.AddPanel(
                        color: "0.2641509 0.2641509 0.2641509 0.3058824");
                    nlPszg.AddPanel(
                        color: "0.5843138 0.1960784 0.1372549 1",
                        imageType: UnityEngine.UI.Image.Type.Simple,
                        anchorMin: "0 0",
                        anchorMax: "0 1",
                        offsetMin: "-0.5 0",
                        offsetMax: "0.5 0"
                        /* name: "Panel" */);
                    nlPszg.AddPanel(
                        color: "0.5843138 0.1960784 0.1372549 1",
                        imageType: UnityEngine.UI.Image.Type.Simple,
                        anchorMin: "1 0",
                        anchorMax: "1 1",
                        offsetMin: "-0.5 0",
                        offsetMax: "0.5 0"
                        /* name: "Panel (1)" */);
                    nlPszg.AddText(
                        text: text,
                        color: "0.8862746 0.8588236 0.8274511 1",
                        font: CUI.Font.RobotoCondensedRegular,
                        fontSize: fontSize,
                        align: align,
                        overflow: VerticalWrapMode.Overflow,
                        anchorMin: "0 0",
                        anchorMax: "1 1",
                        offsetMin: "10 5",
                        offsetMax: "-10 -5"
                        /* name: "Text" */);
                }

                foreach (CUI.Element element in root.Container.Skip(1))
                    element.WithFade(0.5f, 0.5f).Components.AddCountdown(endTime: destroyTime);

                root.Render(connection);
            }


            private void ContainerLimitReached(CUI.Element root)
            {
                {
                    CUI.Element cWHUEF = root.AddContainer(
                        anchorMin: "0 0",
                        anchorMax: "1 1",
                        offsetMin: "0 0",
                        offsetMax: "0 0",
                        name: "Container Limit Reached").AddDestroySelfAttribute();
                    cWHUEF.AddText(
                        text: ДОСТИГНУТ_ЛИМИТ,
                        color: "0.8352942 0.8078432 0.7764707 1",
                        font: CUI.Font.RobotoCondensedRegular,
                        fontSize: 22,
                        align: TextAnchor.MiddleCenter,
                        overflow: VerticalWrapMode.Overflow,
                        anchorMin: "0 0.5",
                        anchorMax: "1 0.5",
                        offsetMin: "0 0",
                        offsetMax: "0 100"
                        /* name: "Text" */);
                }
            }

            public void CargoBuySlotButton()
            {
                CUI.Root root = new CUI.Root("Inventory");
                {
                    CUI.Element NaXKpz = root.AddButton(
                        command: "cargonpc buy-slot",
                        color: "0.9686275 0.9215686 0.8823529 0.03137255",
                        imageType: UnityEngine.UI.Image.Type.Simple,
                        anchorMin: "0.5 0",
                        anchorMax: "0.5 0",
                        offsetMin: "200 18.5",
                        offsetMax: "320 43.5",
                        name: "CargoBuySlotButton").AddDestroySelfAttribute();
                    NaXKpz.AddText(
                        text: "ДОКУПИТЬ СЛОТ",
                        color: "0.9686275 0.9215686 0.8823529 1",
                        font: CUI.Font.RobotoCondensedRegular,
                        fontSize: 12,
                        align: TextAnchor.MiddleCenter,
                        overflow: VerticalWrapMode.Overflow,
                        anchorMin: "0 0",
                        anchorMax: "1 1",
                        offsetMin: "0 0",
                        offsetMax: "0 0"
                        /* name: "Text" */);
                }
                root.Render(connection);
            }

            private void BuyContainerContent(CUI.Element root)
            {
                {
                    CUI.Element BoPZwB = root.AddContainer(
                        anchorMin: "0 0",
                        anchorMax: "1 1",
                        offsetMin: "0 0",
                        offsetMax: "0 0",
                        name: "Buy Container Content").AddDestroySelfAttribute();
                    BoPZwB.AddText(
                        text: string.Format(ФОРМАТ_НЕОБХОДИМО_КУПИТЬ_КОНТЕЙНЕР, PriceCalculator.GetNewContainerPrice(this.player)),
                        color: "0.8352942 0.8078432 0.7764707 1",
                        font: CUI.Font.RobotoCondensedRegular,
                        fontSize: 22,
                        align: TextAnchor.MiddleCenter,
                        overflow: VerticalWrapMode.Overflow,
                        anchorMin: "0.5 0.5",
                        anchorMax: "0.5 0.5",
                        offsetMin: "-210 41",
                        offsetMax: "210 141"
                        /* name: "Text" */);
                    {
                        CUI.Element cKVJEH = BoPZwB.AddButton(
                            command: "cargonpc buy-container",
                            color: "0.2714044 0.5377358 0.3081536 1",
                            material: "assets/icons/iconmaterial.mat",
                            imageType: UnityEngine.UI.Image.Type.Simple,
                            anchorMin: "0.5 0.5",
                            anchorMax: "0.5 0.5",
                            offsetMin: "-210 -27.384",
                            offsetMax: "210 27.384"
                            /* name: "Button" */);
                        cKVJEH.AddText(
                            text: КУПИТЬ,
                            color: "0.8679245 0.8679245 0.8679245 1",
                            font: CUI.Font.RobotoCondensedBold,
                            fontSize: 20,
                            align: TextAnchor.MiddleCenter,
                            overflow: VerticalWrapMode.Overflow,
                            anchorMin: "0 0",
                            anchorMax: "1 1",
                            offsetMin: "0 0",
                            offsetMax: "0 0"
                            /* name: "Text" */);
                    }
                }
            }


            private bool HasAccessToCategory(Category category)
            {
                switch (category)
                {
                    case Category.Main:
                    case Category.CreateOrder:
                    case Category.Travel:
                        return this.player.NPC != null && this.player.NPC.Distance(this.Player) <= 3;

                    case Category.Admin:
                        return PluginInstance.permission.UserHasPermission(this.UserId.ToString(), PERMISSSION_ADMIN);
                    default:
                        return false;
                }
            }

            public void AnotherCurrency()
            {
                CUI.Root root = new CUI.Root("Cargo_Currency");
                {
                    CUI.Element igVZIi = root.AddButton(
                        command: $"cargonpc select-currency {(CurrentCurrency == Currency.Scrap ? Currency.Coins : Currency.Scrap)}",
                        close: "Another Currency",
                        color: "0.1098039 0.1098039 0.1098039 1",
                        material: "assets/icons/iconmaterial.mat",
                        imageType: UnityEngine.UI.Image.Type.Simple,
                        anchorMin: "0 0",
                        anchorMax: "1 0",
                        offsetMin: "0 -33.6",
                        offsetMax: "0 -3.6",
                        name: "Another Currency");
                    var wADqMV_text = CurrentCurrency == Currency.Scrap ? МОНЕТЫ : СКРАП;
                    igVZIi.AddText(
                        text: wADqMV_text,
                        color: "0.8862745 0.8588235 0.827451 1",
                        font: CUI.Font.RobotoCondensedRegular,
                        fontSize: 16,
                        align: TextAnchor.MiddleCenter,
                        overflow: VerticalWrapMode.Overflow,
                        anchorMin: "0 0",
                        anchorMax: "1 1",
                        offsetMin: "0 0",
                        offsetMax: "0 0"
                        /* name: "Text" */);
                }
                root.Render(connection);
            }

            private void Categories(CUI.Element root)
            {
                {
                    CUI.Element byjapX = root.AddPanel(
                        color: "0 0 0 0",
                        anchorMin: "0 0",
                        anchorMax: "0 1",
                        offsetMin: "0 560",
                        offsetMax: "1200 0",
                        name: "CargoUI_Categories").AddDestroySelfAttribute();

                    byjapX.Components.AddScrollView(
                            horizontal: true,
                            anchorMin: "0 0",
                            anchorMax: "0 1",
                            offsetMax: "1200 0"
                        );


                    {
                        int i = 0;
                        Category selectedCategory = this.CurrentCategory;
                        foreach (Category category in Enum.GetValues(typeof(Category)).Cast<Category>())
                        {
                            if (!HasAccessToCategory(category))
                                continue;

                            CUI.Element kfxedP = byjapX.AddButton(
                                       command: $"cargonpc change-category {(int)category}",
                                       color: "0 0 0 0",
                                       imageType: UnityEngine.UI.Image.Type.Simple,
                                       anchorMin: "0 1",
                                       anchorMax: "0 1",
                                       offsetMin: $"{i * 150} -30",
                                       offsetMax: $"{(i + 1) * 150} 0"
                                       /* name: "Button" */);

                            kfxedP.AddText(
                                text: EnumToString(category),
                                color: "0.8862745 0.8588235 0.827451 0.9019608",
                                font: CUI.Font.RobotoCondensedBold,
                                align: TextAnchor.MiddleCenter,
                                overflow: VerticalWrapMode.Overflow,
                                anchorMin: "0 0",
                                anchorMax: "1 1",
                                offsetMin: "0 0",
                                offsetMax: "0 0"
                                /* name: "Text" */);

                            if (category == selectedCategory)
                            {
                                kfxedP.AddPanel(
                                   color: "0.8431373 0.2862745 0.2 1",
                                   imageType: UnityEngine.UI.Image.Type.Simple,
                                   anchorMin: "0 0",
                                   anchorMax: "1 0",
                                   offsetMin: "0 -6.5",
                                   offsetMax: "0 -3.5"
                                   /* name: "Line" */);
                            }
                            i++;
                        }
                    }


                }
            }

            private void MainContent(CUI.Element root)
            {
                bool hideCompleted = this.HideCompleted;
                {
                    CUI.Element dHYsiF = root.AddContainer(
                        anchorMin: "0 0",
                        anchorMax: "1 1",
                        offsetMin: "0 0",
                        offsetMax: "0 0",
                        name: "Main Content").AddDestroySelfAttribute();
                    {
                        CUI.Element FkDEOm = dHYsiF.AddContainer(
                            anchorMin: "0.5 0.5",
                            anchorMax: "0.5 0.5",
                            offsetMin: "420 290.9",
                            offsetMax: "620 320.9"
                            /* name: "HideCompleted" */);
                        {
                            {
                                CUI.Element AKgMAr = FkDEOm.AddButton(
                                    command: "cargonpc update-data hide-completed",
                                    sprite: "assets/content/ui/ui.box.sharp.tga",
                                    color: "0.8313726 0.8078431 0.7764706 1",
                                    imageType: UnityEngine.UI.Image.Type.Simple,
                                    anchorMin: "0 0.5",
                                    anchorMax: "0 0.5",
                                    offsetMin: "5 -10",
                                    offsetMax: "25 10"
                                    /* name: "Checkbox" */);

                                if (hideCompleted)
                                {
                                    AKgMAr.AddPanel(
                                        sprite: "assets/icons/close.png",
                                        color: "0.8313726 0.8078431 0.7764706 1",
                                        material: "assets/icons/iconmaterial.mat",
                                        imageType: UnityEngine.UI.Image.Type.Simple,
                                        anchorMin: "0 0",
                                        anchorMax: "1 1",
                                        offsetMin: "4 4",
                                        offsetMax: "-4 -4"
                                        /* name: "X" */);
                                }
                            }
                            FkDEOm.AddText(
                                text: СКРЫТЬ_ЗАВЕРШЕННЫЕ,
                                color: "0.8313726 0.8078431 0.7764706 1",
                                font: CUI.Font.RobotoCondensedRegular,
                                fontSize: 16,
                                align: TextAnchor.MiddleLeft,
                                overflow: VerticalWrapMode.Overflow,
                                anchorMin: "0 0",
                                anchorMax: "1 1",
                                offsetMin: "30 0",
                                offsetMax: "0 0"
                                /* name: "Text" */);
                        }

                        CUI.Element UrKzIP = dHYsiF.AddPanel(
                            material: "assets/icons/iconmaterial.mat",
                            color: "0.1226415 0.1226415 0.1226415 1",
                            imageType: UnityEngine.UI.Image.Type.Simple,
                            anchorMin: "0 1",
                            anchorMax: "0 1",
                            offsetMin: "0 -48",
                            offsetMax: "1240 0"
                            /* name: "Table Columns" */);
                        UrKzIP.AddPanel(
                            color: "0.8313726 0.282353 0.1921569 0.4627451",
                            imageType: UnityEngine.UI.Image.Type.Simple,
                            anchorMin: "0 0",
                            anchorMax: "0 1",
                            offsetMin: "43.6 0",
                            offsetMax: "45.6 0"
                            /* name: "Panel" */);
                        UrKzIP.AddText(
                            text: "#",
                            color: "0.7058824 0.682353 0.6588235 1",
                            font: CUI.Font.RobotoCondensedRegular,
                            fontSize: 20,
                            align: TextAnchor.MiddleCenter,
                            overflow: VerticalWrapMode.Overflow,
                            anchorMin: "0 0",
                            anchorMax: "0 1",
                            offsetMin: "0 0",
                            offsetMax: "45 0"
                            /* name: "Number" */);
                        UrKzIP.AddText(
                            text: ОТКУДА,
                            color: "0.7058824 0.682353 0.6588235 1",
                            font: CUI.Font.RobotoCondensedRegular,
                            fontSize: 20,
                            align: TextAnchor.MiddleLeft,
                            overflow: VerticalWrapMode.Overflow,
                            anchorMin: "0 0",
                            anchorMax: "0 1",
                            offsetMin: "80 0",
                            offsetMax: "320 0"
                            /* name: "From" */);
                        UrKzIP.AddText(
                            text: КУДА,
                            color: "0.7058824 0.682353 0.6588235 1",
                            font: CUI.Font.RobotoCondensedRegular,
                            fontSize: 20,
                            align: TextAnchor.MiddleLeft,
                            overflow: VerticalWrapMode.Overflow,
                            anchorMin: "0 0",
                            anchorMax: "0 1",
                            offsetMin: "330 0",
                            offsetMax: "570 0"
                            /* name: "To" */);
                        UrKzIP.AddText(
                            text: CОСТОЯНИЕ,
                            color: "0.7058824 0.682353 0.6588235 1",
                            font: CUI.Font.RobotoCondensedRegular,
                            fontSize: 20,
                            align: TextAnchor.MiddleLeft,
                            overflow: VerticalWrapMode.Overflow,
                            anchorMin: "0 0",
                            anchorMax: "0 1",
                            offsetMin: "580 0",
                            offsetMax: "820 0"
                            /* name: "State" */);
                        UrKzIP.AddText(
                            text: СТОИМОСТЬ_ХРАНЕНИЯ,
                            color: "0.7058824 0.682353 0.6588235 1",
                            font: CUI.Font.RobotoCondensedRegular,
                            fontSize: 20,
                            align: TextAnchor.MiddleLeft,
                            overflow: VerticalWrapMode.Overflow,
                            anchorMin: "0 0",
                            anchorMax: "0 1",
                            offsetMin: "820 0",
                            offsetMax: "1060 0"
                            /* name: "Storage Cost" */);
                        UrKzIP.AddText(
                            text: ПРЕДМЕТЫ,
                            color: "0.7058824 0.682353 0.6588235 1",
                            font: CUI.Font.RobotoCondensedRegular,
                            fontSize: 20,
                            align: TextAnchor.MiddleLeft,
                            overflow: VerticalWrapMode.Overflow,
                            anchorMin: "0 0",
                            anchorMax: "0 1",
                            offsetMin: "1075 0",
                            offsetMax: "1240 0"
                            /* name: "Items" */);
                    }

                    CUI.Element HaHeGJ = dHYsiF.AddPanel(
                        color: "1 1 1 0",
                        imageType: UnityEngine.UI.Image.Type.Simple,
                        anchorMin: "0 1",
                        anchorMax: "1 1",
                        offsetMin: "0 -576",
                        offsetMax: "0 -48"
                        /* name: "Scroll" */);


                    {
                        int i = 0;
                        foreach (ContainerInfo containerInfo in this.player.CachedGlobalContainers)
                        {
                            if (containerInfo.State == ContainerState.Completed && hideCompleted)
                                continue;

                            CUI.Element GSVnwv = HaHeGJ.AddPanel(
                                material: "assets/icons/iconmaterial.mat",
                                color: "0.1226415 0.1226415 0.1226415 0.6",
                                imageType: UnityEngine.UI.Image.Type.Simple,
                                anchorMin: "0 1",
                                anchorMax: "0 1",
                                offsetMin: $"0 -{(i + 1) * 48}",
                                offsetMax: $"1240 -{i * 48}"
                                /* name: "Row" */);
                            GSVnwv.AddPanel(
                                color: "0.8313726 0.282353 0.1921569 0.4627451",
                                imageType: UnityEngine.UI.Image.Type.Simple,
                                anchorMin: "0 0",
                                anchorMax: "0 1",
                                offsetMin: "43.6 0",
                                offsetMax: "45.6 0"
                                /* name: "Panel" */);
                            GSVnwv.AddText(
                                text: (i + 1).ToString(),
                                color: "0.7058824 0.682353 0.6588235 1",
                                font: CUI.Font.RobotoCondensedRegular,
                                fontSize: 20,
                                align: TextAnchor.MiddleCenter,
                                overflow: VerticalWrapMode.Overflow,
                                anchorMin: "0 0",
                                anchorMax: "0 1",
                                offsetMin: "0 0",
                                offsetMax: "45 0"
                                /* name: "Number" */);

                            Server originServer = Server.Get(containerInfo.SourceServerId);

                            GSVnwv.AddText(
                                text: originServer?.Name ?? НЕИЗВЕСТНО,
                                color: "0.7058824 0.682353 0.6588235 1",
                                font: CUI.Font.RobotoCondensedRegular,
                                fontSize: 20,
                                align: TextAnchor.MiddleLeft,
                                overflow: VerticalWrapMode.Overflow,
                                anchorMin: "0 0",
                                anchorMax: "0 1",
                                offsetMin: "80 0",
                                offsetMax: "320 0"
                                /* name: "From" */);


                            Server destinationServer = null;
                            if (containerInfo.DestinationServerId.HasValue)
                                destinationServer = Server.Get(containerInfo.DestinationServerId.Value);

                            GSVnwv.AddText(
                                text: destinationServer?.Name ?? НЕИЗВЕСТНО,
                                color: "0.7058824 0.682353 0.6588235 1",
                                font: CUI.Font.RobotoCondensedRegular,
                                fontSize: 20,
                                align: TextAnchor.MiddleLeft,
                                overflow: VerticalWrapMode.Overflow,
                                anchorMin: "0 0",
                                anchorMax: "0 1",
                                offsetMin: "330 0",
                                offsetMax: "570 0"
                                /* name: "To" */);
                            var stateText = GSVnwv.AddText(
                                text: EnumToString(containerInfo.State),
                                color: "0.7058824 0.682353 0.6588235 1",
                                font: CUI.Font.RobotoCondensedRegular,
                                fontSize: 20,
                                align: TextAnchor.MiddleLeft,
                                overflow: VerticalWrapMode.Overflow,
                                anchorMin: "0 0",
                                anchorMax: "0 1",
                                offsetMin: "580 0",
                                offsetMax: "820 0"
                                /* name: "State" */);

                            if (containerInfo.State == ContainerState.InTransit)
                                stateText.Components.AddCountdown(startTime: containerInfo.Timestamp + databaseConfig.DeliveryVariantTimes[containerInfo.DeliveryVariant] - Epoch.Current, endTime: 0, timerFormat: TimerFormat.Custom, numberFormat: ФОРМАТ_ОТСЧЕТА_ВРЕМЕНИ);

                            {
                                float storageCost = PriceCalculator.GetStorageCost(containerInfo, CurrentCurrency);

                                CUI.Element yAadzJ = GSVnwv.AddContainer(
                                        anchorMin: "0 0",
                                        anchorMax: "0 1",
                                        offsetMin: "820 0",
                                        offsetMax: "1060 0");

                                switch (containerInfo.State)
                                {
                                    case ContainerState.Idle:
                                        yAadzJ.AddText(
                                                text: ОПЛАТА_ПРИ_ОТПРАВКЕ,
                                                color: "0.7058824 0.682353 0.6588235 1",
                                                font: CUI.Font.RobotoCondensedRegular,
                                                fontSize: 20,
                                                align: TextAnchor.MiddleLeft,
                                                overflow: VerticalWrapMode.Overflow
                                                /* name: "Storage Cost" */);
                                        break;
                                    case ContainerState.Delivered:
                                        yAadzJ.AddText(
                                               text: GetPriceString(storageCost),
                                               color: "0.7058824 0.682353 0.6588235 1",
                                               font: CUI.Font.RobotoCondensedRegular,
                                               fontSize: 20,
                                               align: TextAnchor.MiddleLeft,
                                               overflow: VerticalWrapMode.Overflow
                                               /* name: "Storage Cost" */);
                                        break;
                                    case ContainerState.InTransit:
                                    case ContainerState.Completed:
                                        yAadzJ.AddText(
                                                text: ОТСУТСТВУЕТ,
                                                color: "0.7058824 0.682353 0.6588235 1",
                                                font: CUI.Font.RobotoCondensedRegular,
                                                fontSize: 20,
                                                align: TextAnchor.MiddleLeft,
                                                overflow: VerticalWrapMode.Overflow
                                                /* name: "Storage Cost" */);
                                        break;
                                }

                            }
                            {
                                CUI.Element dPSoHH = GSVnwv.AddContainer(
                                     anchorMin: "0 0",
                                     anchorMax: "0 1",
                                     offsetMin: "1075 0",
                                     offsetMax: "1240 0"
                                     /* name: "Button" */);

                                if (containerInfo.Slots > 0)
                                {
                                    dPSoHH = GSVnwv.AddButton(
                                        command: $"cargonpc inspect-container {containerInfo.DatabaseId}",
                                        color: "0.1886792 0.1886792 0.1886792 1",
                                        material: "assets/icons/iconmaterial.mat",
                                        imageType: UnityEngine.UI.Image.Type.Simple,
                                        anchorMin: "0 0",
                                        anchorMax: "0 1",
                                        offsetMin: "1075 0",
                                        offsetMax: "1210 0"
                                        /* name: "Button" */);
                                    dPSoHH.AddText(
                                        text: ОСМОТРЕТЬ,
                                        color: "0.6980392 0.6745098 0.6509804 1",
                                        font: CUI.Font.RobotoCondensedBold,
                                        fontSize: 20,
                                        align: TextAnchor.MiddleCenter,
                                        overflow: VerticalWrapMode.Overflow,
                                        anchorMin: "0 0",
                                        anchorMax: "1 1",
                                        offsetMin: "0 0",
                                        offsetMax: "0 0"
                                        /* name: "Text" */);

                                    GSVnwv.AddButton(
                                        command: $"cargonpc force-complete-container {containerInfo.DatabaseId}",
                                        color: "0.6431373 0.1862745 0.1 1",
                                        material: "assets/icons/iconmaterial.mat",
                                        imageType: UnityEngine.UI.Image.Type.Simple,
                                        anchorMin: "0 0",
                                        anchorMax: "0 1",
                                        offsetMin: "1213 0",
                                        offsetMax: "1240 0",
                                        name: $"Cargo_DeleteButton_{containerInfo.DatabaseId}")
                                        .AddPanel(
                                            sprite: "assets/icons/clear.png",
                                            material: "assets/icons/iconmaterial.mat",
                                            color: "0.8862745 0.8588235 0.827451 1",
                                            imageType: UnityEngine.UI.Image.Type.Simple,
                                            anchorMin: "0.5 0.5",
                                            anchorMax: "0.5 0.5",
                                            offsetMin: "-10 -10",
                                            offsetMax: "10 10");
                                }
                                else
                                {
                                    dPSoHH.AddText(
                                           text: ПУСТО,
                                           color: "0.6980392 0.6745098 0.6509804 1",
                                           font: CUI.Font.RobotoCondensedRegular,
                                           fontSize: 20,
                                           align: TextAnchor.MiddleLeft,
                                           overflow: VerticalWrapMode.Overflow
                                           /* name: "Text" */);
                                }
                            }

                            if (i > 0)
                            {
                                GSVnwv.AddPanel(
                                  color: "0.3962264 0.3962264 0.3962264 0.05098039",
                                  imageType: UnityEngine.UI.Image.Type.Simple,
                                  anchorMin: "0 1",
                                  anchorMax: "1 1",
                                  offsetMin: "0 -1",
                                  offsetMax: "0 1"
                                  /* name: "Panel" */);
                            }
                            i++;
                        }
                        HaHeGJ.Components.AddScrollView(
                            vertical: true,
                            scrollSensitivity: 20,
                            anchorMin: "0 1",
                            anchorMax: "1 1",
                            offsetMin: $"0 -{Mathf.Max(528, i * 48)}");
                    }
                }
            }


            private void TravelContent(CUI.Element root)
            {
                {
                    CUI.Element blDEZF = root.AddContainer(
                        anchorMin: "0 0",
                        anchorMax: "1 1",
                        offsetMin: "0 0",
                        offsetMax: "0 0",
                        name: "Travel Content").AddDestroySelfAttribute();
                    {
                        CUI.Element KmcgwU = blDEZF.AddContainer(
                            anchorMin: "0 0",
                            anchorMax: "0 1",
                            offsetMin: "13 10",
                            offsetMax: "433 -10"
                            /* name: "Servers" */);
                        KmcgwU.AddText(
                            text: "ВЫБЕРЕТЕ СЕРВЕР",
                            color: "0.7921569 0.7686275 0.7411765 1",
                            font: CUI.Font.RobotoCondensedBold,
                            fontSize: 20,
                            align: TextAnchor.LowerLeft,
                            overflow: VerticalWrapMode.Overflow,
                            anchorMin: "0 1",
                            anchorMax: "1 1",
                            offsetMin: "-0.3 -29.098",
                            offsetMax: "-0.3 0.902"
                            /* name: "Label" */);
                        CargoUI_Order_ServersLayout(KmcgwU, "cargonpc travel server-map {0}");
                    }
                }
            }

            private void CargoUI()
            {
                CUI.Root root = new CUI.Root("OverlayNonScaled");
                {
                    CUI.Element CNXAvk = root.AddPanel(
                        color: "0 0 0 1",
                        imageType: UnityEngine.UI.Image.Type.Simple,
                        cursorEnabled: true,
                        keyboardEnabled: true,
                        anchorMin: "0 0",
                        anchorMax: "1 1",
                        offsetMin: "0 0",
                        offsetMax: "0 0",
                        name: "Cargo UI").AddDestroySelfAttribute();
                    {
                        CUI.Element QHKJVC = CNXAvk.AddContainer(
                            anchorMin: "0 1",
                            anchorMax: "1 1",
                            offsetMin: "20 -70",
                            offsetMax: "-20 -20"
                            /* name: "Header" */);
                        QHKJVC.AddText(
                            text: КАРГО,
                            color: "0.8862745 0.8588235 0.827451 1",
                            font: CUI.Font.RobotoCondensedBold,
                            fontSize: 30,
                            align: TextAnchor.MiddleLeft,
                            overflow: VerticalWrapMode.Overflow,
                            anchorMin: "0 0",
                            anchorMax: "1 1",
                            offsetMin: "5 0",
                            offsetMax: "-65 0"
                            /* name: "Title" */);
                        {
                            CUI.Element CgoRmu = QHKJVC.AddButton(
                                command: "cargonpc destroy",
                                close: "Cargo UI",
                                color: "0.8117647 0.2627451 0.1764706 1",
                                imageType: UnityEngine.UI.Image.Type.Simple,
                                anchorMin: "1 1",
                                anchorMax: "1 1",
                                offsetMin: "-40 -40",
                                offsetMax: "0 0"
                                /* name: "Close Button" */);
                            CgoRmu.AddPanel(
                                sprite: "assets/icons/close.png",
                                material: "assets/icons/iconmaterial.mat",
                                imageType: UnityEngine.UI.Image.Type.Simple,
                                anchorMin: "0 0",
                                anchorMax: "1 1",
                                offsetMin: "9 9",
                                offsetMax: "-9 -9"
                                /* name: "Panel" */);
                        }
                        {
                            CUI.Element dbXQmo = QHKJVC.AddButton(
                                command: "cargonpc dropdown-currency",
                                color: "0.1037736 0.1037736 0.1037736 1",
                                material: "assets/icons/iconmaterial.mat",
                                imageType: UnityEngine.UI.Image.Type.Simple,
                                anchorMin: "0.5 0.5",
                                anchorMax: "0.5 0.5",
                                offsetMin: "395.461 -17.5",
                                offsetMax: "515.461 17.5",
                                name: "Cargo_Currency");
                            {
                                CUI.Element IBdbRw = dbXQmo.AddPanel(
                                    sprite: "assets/icons/chevron_down.png",
                                    color: "0.8862745 0.8588235 0.827451 1",
                                    imageType: UnityEngine.UI.Image.Type.Simple,
                                    anchorMin: "0 0.5",
                                    anchorMax: "0 0.5",
                                    offsetMin: "0 -17.5",
                                    offsetMax: "35 17.5"
                                    /* name: "Panel" */);
                                IBdbRw.AddPanel(
                                    color: "0.2075472 0.2075472 0.2075472 1",
                                    imageType: UnityEngine.UI.Image.Type.Simple,
                                    anchorMin: "1 0",
                                    anchorMax: "1 1",
                                    offsetMin: "-1 0",
                                    offsetMax: "1 0"
                                    /* name: "Panel" */);
                            }
                            var tpucyM_text = GetCurrencyName();
                            dbXQmo.AddText(
                                text: tpucyM_text,
                                color: "0.8862745 0.8588235 0.827451 1",
                                font: CUI.Font.RobotoCondensedRegular,
                                fontSize: 16,
                                align: TextAnchor.MiddleCenter,
                                overflow: VerticalWrapMode.Overflow,
                                anchorMin: "0 0",
                                anchorMax: "1 1",
                                offsetMin: "40 0",
                                offsetMax: "-10 0"
                                /* name: "Text" */);
                        }
                    }
                    {
                        CUI.Element JnOLkQ = CNXAvk.AddContainer(
                            anchorMin: "0 0",
                            anchorMax: "1 1",
                            offsetMin: "20 20",
                            offsetMax: "-20 -85",
                            name: "CargoUI_Body");

                        Categories(JnOLkQ);
                        AddContent(JnOLkQ);
                    }
                }
                root.Render(connection);
            }

            private void AddContent(CUI.Element root)
            {
                CUI.Element content = root.AddContainer(
                                anchorMin: "0 0",
                                anchorMax: "1 1",
                                offsetMin: "0 0",
                                offsetMax: "0 -55",
                                name: "CargoUI_Content").AddDestroySelfAttribute();

                Category category = this.CurrentCategory;

                if (!HasAccessToCategory(category))
                    return;

                switch (category)
                {
                    case Category.Main:
                        MainContent(content);
                        break;
                    case Category.CreateOrder:
                        if (this.player.HasOrderContainer())
                            CreateOrderContent(content);
                        else if (this.player.IsLimitReached())
                            ContainerLimitReached(content);
                        else
                            BuyContainerContent(content);
                        break;
                    case Category.Travel:
                        TravelContent(content);
                        break;
                    case Category.Admin:
                        AdminContent(content);
                        break;
                    default:
                        break;
                }
            }

            private void AdminContent(CUI.Element root)
            {
                {
                    CUI.Element ngWkuC = root.AddPanel(
                        color: "1 1 1 0",
                        imageType: UnityEngine.UI.Image.Type.Simple,
                        anchorMin: "0 0",
                        anchorMax: "1 1",
                        offsetMin: "0 0",
                        offsetMax: "0 0",
                        name: "Admin Content").AddDestroySelfAttribute();
                    AdminContentScrollContent(ngWkuC);
                    ngWkuC.Components.AddScrollView(
                                            vertical: true,
                                            scrollSensitivity: 30f,
                                            anchorMin: "0 1",
                                            anchorMax: "1 1",
                                            offsetMin: "0 -2000");
                }
            }

            private void AdminContentScrollContent(CUI.Element root)
            {
                {
                    CUI.Element vbAnay = root.AddContainer(
                        anchorMin: "0 0",
                        anchorMax: "1 1",
                        offsetMin: "0 0",
                        offsetMax: "0 0",
                        name: "Admin Content // Scroll Content").AddDestroySelfAttribute();
                    {
                        CUI.Element ddYVoH = vbAnay.AddContainer(
                            anchorMin: "0 1",
                            anchorMax: "1 1",
                            offsetMin: "15 -97.792",
                            offsetMax: "-15 -14"
                            /* name: "For Player" */);
                        ddYVoH.AddText(
                            text: ДЛЯ_ИГРОКА,
                            color: "0.8862746 0.8588236 0.8274511 1",
                            font: CUI.Font.RobotoCondensedBold,
                            fontSize: 24,
                            align: TextAnchor.MiddleLeft,
                            overflow: VerticalWrapMode.Overflow,
                            anchorMin: "0 1",
                            anchorMax: "1 1",
                            offsetMin: "0 -30",
                            offsetMax: "0 0"
                            /* name: "Title" */);
                        {
                            CUI.Element kISbKP = ddYVoH.AddContainer(
                                anchorMin: "0 1",
                                anchorMax: "0 1",
                                offsetMin: "0 -77",
                                offsetMax: "550 -37"
                                /* name: "PlayerMaxActiveContainerCount" */);
                            kISbKP.AddText(
                                text: МАКСИМАЛЬНОЕ_КОЛИЧЕСТВО_КОНТЕЙНЕРОВ_ДЛЯ_ИГРОКА,
                                color: "0.8862746 0.8588236 0.8274511 1",
                                font: CUI.Font.RobotoCondensedRegular,
                                fontSize: 16,
                                align: TextAnchor.MiddleLeft,
                                overflow: VerticalWrapMode.Overflow,
                                anchorMin: "0 0",
                                anchorMax: "0.6 1",
                                offsetMin: "10 4",
                                offsetMax: "0 -4"
                                /* name: "Text" */);
                            {
                                CUI.Element oiHogv = kISbKP.AddPanel(
                                    material: "assets/icons/iconmaterial.mat",
                                    color: "0.1415094 0.1395069 0.1395069 1",
                                    imageType: UnityEngine.UI.Image.Type.Simple,
                                    anchorMin: "0.6 0",
                                    anchorMax: "1 1",
                                    offsetMin: "10 0",
                                    offsetMax: "0 0"
                                    /* name: "Panel" */);
                                var apXXCD_value = databaseConfig.PlayerMaxActiveContainerCount.ToString();
                                oiHogv.AddInputfield(
                                    command: "cargonpc admin-settings PlayerMaxActiveContainerCount",
                                    text: apXXCD_value,
                                    color: "0.7264151 0.7264151 0.7264151 1",
                                    font: CUI.Font.RobotoCondensedRegular,
                                    fontSize: 18,
                                    align: TextAnchor.MiddleCenter,
                                    lineType: InputField.LineType.SingleLine,
                                    charsLimit: 0,
                                    anchorMin: "0 0",
                                    anchorMax: "1 1",
                                    offsetMin: "10 4",
                                    offsetMax: "-10 -4"
                                    /* name: "Inputfield" */);
                            }
                        }
                        {
                            CUI.Element WjiXHz = ddYVoH.AddContainer(
                                anchorMin: "0 1",
                                anchorMax: "0 1",
                                offsetMin: "568.3 -77",
                                offsetMax: "1118.3 -37"
                                /* name: "ContainerPrices" */);
                            WjiXHz.AddText(
                                text: НАСТРОЙКИ_НА_КАЖДЫЙ_КОНТЕЙНЕР_Н,
                                color: "0.8862746 0.8588236 0.8274511 1",
                                font: CUI.Font.RobotoCondensedRegular,
                                fontSize: 16,
                                align: TextAnchor.MiddleLeft,
                                overflow: VerticalWrapMode.Overflow,
                                anchorMin: "0 0",
                                anchorMax: "0.6 1",
                                offsetMin: "10 4",
                                offsetMax: "0 -4"
                                /* name: "Text" */);
                            {
                                CUI.Element RZdEMn = WjiXHz.AddButton(
                                    command: "cargonpc popup ContainerPrices",
                                    color: "0.1411765 0.1411765 0.1411765 1",
                                    imageType: UnityEngine.UI.Image.Type.Simple,
                                    anchorMin: "0.6 0",
                                    anchorMax: "1 1",
                                    offsetMin: "10 0",
                                    offsetMax: "0 0"
                                    /* name: "Button" */);
                                RZdEMn.AddText(
                                    text: ОТКРЫТЬ,
                                    color: "0.8039216 0.7803922 0.7490196 1",
                                    font: CUI.Font.RobotoCondensedBold,
                                    fontSize: 19,
                                    align: TextAnchor.MiddleCenter,
                                    overflow: VerticalWrapMode.Overflow,
                                    anchorMin: "0 0",
                                    anchorMax: "1 1",
                                    offsetMin: "0 0",
                                    offsetMax: "0 0"
                                    /* name: "Text" */);
                            }
                        }
                    }
                    {
                        CUI.Element RMPFDK = vbAnay.AddContainer(
                            anchorMin: "0 1",
                            anchorMax: "1 1",
                            offsetMin: "15 -198",
                            offsetMax: "-15 -113"
                            /* name: "Availability of Items" */);
                        RMPFDK.AddText(
                            text: ДОСТУПНОСТЬ_ПРЕДМЕТОВ,
                            color: "0.8862746 0.8588236 0.8274511 1",
                            font: CUI.Font.RobotoCondensedBold,
                            fontSize: 24,
                            align: TextAnchor.MiddleLeft,
                            overflow: VerticalWrapMode.Overflow,
                            anchorMin: "0 1",
                            anchorMax: "1 1",
                            offsetMin: "0 -30",
                            offsetMax: "0 0"
                            /* name: "Title" */);
                        {
                            CUI.Element tYWsGl = RMPFDK.AddContainer(
                                anchorMin: "0 1",
                                anchorMax: "0 1",
                                offsetMin: "0 -70",
                                offsetMax: "550 -30"
                                /* name: "ContainerPrices" */);
                            tYWsGl.AddText(
                                text: ЧЕРНЫЙ_СПИСОК,
                                color: "0.8862746 0.8588236 0.8274511 1",
                                font: CUI.Font.RobotoCondensedRegular,
                                fontSize: 16,
                                align: TextAnchor.MiddleLeft,
                                overflow: VerticalWrapMode.Overflow,
                                anchorMin: "0 0",
                                anchorMax: "0.6 1",
                                offsetMin: "10 4",
                                offsetMax: "0 -4"
                                /* name: "Text" */);
                            {
                                CUI.Element OHryLv = tYWsGl.AddButton(
                                    command: "cargonpc popup ItemBlacklist",
                                    color: "0.1411765 0.1411765 0.1411765 1",
                                    imageType: UnityEngine.UI.Image.Type.Simple,
                                    anchorMin: "0.6 0",
                                    anchorMax: "1 1",
                                    offsetMin: "10 0",
                                    offsetMax: "0 0"
                                    /* name: "Button" */);
                                OHryLv.AddText(
                                    text: ОТКРЫТЬ,
                                    color: "0.8039216 0.7803922 0.7490196 1",
                                    font: CUI.Font.RobotoCondensedBold,
                                    fontSize: 19,
                                    align: TextAnchor.MiddleCenter,
                                    overflow: VerticalWrapMode.Overflow,
                                    anchorMin: "0 0",
                                    anchorMax: "1 1",
                                    offsetMin: "0 0",
                                    offsetMax: "0 0"
                                    /* name: "Text" */);
                            }
                        }
                        {
                            CUI.Element ANkhPz = RMPFDK.AddContainer(
                                anchorMin: "0 1",
                                anchorMax: "0 1",
                                offsetMin: "571.9 -70",
                                offsetMax: "1121.9 -30"
                                /* name: "Stacks" */);
                            ANkhPz.AddText(
                                text: "Стаки",
                                color: "0.8862746 0.8588236 0.8274511 1",
                                font: CUI.Font.RobotoCondensedRegular,
                                fontSize: 16,
                                align: TextAnchor.MiddleLeft,
                                overflow: VerticalWrapMode.Overflow,
                                anchorMin: "0 0",
                                anchorMax: "0.6 1",
                                offsetMin: "10 4",
                                offsetMax: "0 -4"
                                /* name: "Text" */);
                            {
                                CUI.Element XcftFQ = ANkhPz.AddButton(
                                    command: "cargonpc popup ItemStacks",
                                    color: "0.1411765 0.1411765 0.1411765 1",
                                    imageType: UnityEngine.UI.Image.Type.Simple,
                                    anchorMin: "0.6 0",
                                    anchorMax: "1 1",
                                    offsetMin: "10 0",
                                    offsetMax: "0 0"
                                    /* name: "Button" */);
                                XcftFQ.AddText(
                                    text: ОТКРЫТЬ,
                                    color: "0.8039216 0.7803922 0.7490196 1",
                                    font: CUI.Font.RobotoCondensedBold,
                                    fontSize: 19,
                                    align: TextAnchor.MiddleCenter,
                                    overflow: VerticalWrapMode.Overflow,
                                    anchorMin: "0 0",
                                    anchorMax: "1 1",
                                    offsetMin: "0 0",
                                    offsetMax: "0 0"
                                    /* name: "Text" */);
                            }
                        }
                    }
                    {
                        CUI.Element gJzUZQ = vbAnay.AddContainer(
                            anchorMin: "0 1",
                            anchorMax: "1 1",
                            offsetMin: "15 -362.217",
                            offsetMax: "-15 -198.003"
                            /* name: "Delivery Variants" */);
                        gJzUZQ.AddText(
                            text: ВАРИАНТЫ_ДОСТАВКИ,
                            color: "0.8862746 0.8588236 0.8274511 1",
                            font: CUI.Font.RobotoCondensedBold,
                            fontSize: 24,
                            align: TextAnchor.MiddleLeft,
                            overflow: VerticalWrapMode.Overflow,
                            anchorMin: "0 1",
                            anchorMax: "1 1",
                            offsetMin: "0 -30",
                            offsetMax: "0 0"
                            /* name: "Title" */);
                        {
                            CUI.Element qFSETS = gJzUZQ.AddContainer(
                                anchorMin: "0 1",
                                anchorMax: "0 1",
                                offsetMin: "0 -70",
                                offsetMax: "550 -30"
                                /* name: "Prices" */);
                            qFSETS.AddText(
                                text: ЦЕНА_ВАРИАНТОВ_ДОСТАВКИ,
                                color: "0.8862746 0.8588236 0.8274511 1",
                                font: CUI.Font.RobotoCondensedRegular,
                                fontSize: 16,
                                align: TextAnchor.MiddleLeft,
                                overflow: VerticalWrapMode.Overflow,
                                anchorMin: "0 0",
                                anchorMax: "0.6 1",
                                offsetMin: "10 4",
                                offsetMax: "0 -4"
                                /* name: "Text" */);
                            {
                                CUI.Element krmKdm = qFSETS.AddContainer(
                                    anchorMin: "0.5 0.5",
                                    anchorMax: "0.5 0.5",
                                    offsetMin: "-255.177 -60",
                                    offsetMax: "275 -20"
                                    /* name: "Standard" */);
                                krmKdm.AddText(
                                    text: СТАНДАРТНАЯ,
                                    color: "0.8862746 0.8588236 0.8274511 1",
                                    font: CUI.Font.RobotoCondensedRegular,
                                    fontSize: 16,
                                    align: TextAnchor.MiddleLeft,
                                    overflow: VerticalWrapMode.Overflow,
                                    anchorMin: "0 0",
                                    anchorMax: "0.6 1",
                                    offsetMin: "10 4",
                                    offsetMax: "0 -4"
                                    /* name: "Text" */);
                                {
                                    CUI.Element eHZHxX = krmKdm.AddPanel(
                                        material: "assets/icons/iconmaterial.mat",
                                        color: "0.1415094 0.1395069 0.1395069 1",
                                        imageType: UnityEngine.UI.Image.Type.Simple,
                                        anchorMin: "0.6 0",
                                        anchorMax: "1 1",
                                        offsetMin: "10 0",
                                        offsetMax: "0 0"
                                        /* name: "Panel" */);
                                    var tKRLHq_value = databaseConfig.DeliveryVariantPrices[DeliveryVariant.Standard].ToString();
                                    eHZHxX.AddInputfield(
                                        command: "cargonpc admin-settings DeliveryVariantPrices Standard",
                                        text: tKRLHq_value,
                                        color: "0.7264151 0.7264151 0.7264151 1",
                                        font: CUI.Font.RobotoCondensedRegular,
                                        fontSize: 18,
                                        align: TextAnchor.MiddleCenter,
                                        lineType: InputField.LineType.SingleLine,
                                        charsLimit: 0,
                                        anchorMin: "0 0",
                                        anchorMax: "1 1",
                                        offsetMin: "10 4",
                                        offsetMax: "-10 -4"
                                        /* name: "Inputfield" */);
                                }
                            }
                            {
                                CUI.Element qrlXDQ = qFSETS.AddContainer(
                                    anchorMin: "0.5 0.5",
                                    anchorMax: "0.5 0.5",
                                    offsetMin: "-255.177 -100",
                                    offsetMax: "275 -60"
                                    /* name: "Express" */);
                                qrlXDQ.AddText(
                                    text: ЭКСПРЕСС,
                                    color: "0.8862746 0.8588236 0.8274511 1",
                                    font: CUI.Font.RobotoCondensedRegular,
                                    fontSize: 16,
                                    align: TextAnchor.MiddleLeft,
                                    overflow: VerticalWrapMode.Overflow,
                                    anchorMin: "0 0",
                                    anchorMax: "0.6 1",
                                    offsetMin: "10 4",
                                    offsetMax: "0 -4"
                                    /* name: "Text" */);
                                {
                                    CUI.Element cOBZQX = qrlXDQ.AddPanel(
                                        material: "assets/icons/iconmaterial.mat",
                                        color: "0.1415094 0.1395069 0.1395069 1",
                                        imageType: UnityEngine.UI.Image.Type.Simple,
                                        anchorMin: "0.6 0",
                                        anchorMax: "1 1",
                                        offsetMin: "10 0",
                                        offsetMax: "0 0"
                                        /* name: "Panel" */);
                                    var hylrRM_value = databaseConfig.DeliveryVariantPrices[DeliveryVariant.Express].ToString();
                                    cOBZQX.AddInputfield(
                                        command: "cargonpc admin-settings DeliveryVariantPrices Express",
                                        text: hylrRM_value,
                                        color: "0.7264151 0.7264151 0.7264151 1",
                                        font: CUI.Font.RobotoCondensedRegular,
                                        fontSize: 18,
                                        align: TextAnchor.MiddleCenter,
                                        lineType: InputField.LineType.SingleLine,
                                        charsLimit: 0,
                                        anchorMin: "0 0",
                                        anchorMax: "1 1",
                                        offsetMin: "10 4",
                                        offsetMax: "-10 -4"
                                        /* name: "Inputfield" */);
                                }
                            }
                        }
                        {
                            CUI.Element gcbOMU = gJzUZQ.AddContainer(
                                anchorMin: "0 1",
                                anchorMax: "0 1",
                                offsetMin: "571 -70",
                                offsetMax: "1121 -30"
                                /* name: "Times" */);
                            gcbOMU.AddText(
                                text: ВРЕМЯ_В_СЕКУНДАХ_ДЛЯ_ВАРИАНТОВ_ДОСТАВКИ,
                                color: "0.8862746 0.8588236 0.8274511 1",
                                font: CUI.Font.RobotoCondensedRegular,
                                fontSize: 16,
                                align: TextAnchor.MiddleLeft,
                                overflow: VerticalWrapMode.Overflow,
                                anchorMin: "0 0",
                                anchorMax: "0.6 1",
                                offsetMin: "10 4",
                                offsetMax: "0 -4"
                                /* name: "Text" */);
                            {
                                CUI.Element luxLvm = gcbOMU.AddContainer(
                                    anchorMin: "0.5 0.5",
                                    anchorMax: "0.5 0.5",
                                    offsetMin: "-255.177 -60",
                                    offsetMax: "275 -20"
                                    /* name: "Standard" */);
                                luxLvm.AddText(
                                    text: СТАНДАРТНАЯ,
                                    color: "0.8862746 0.8588236 0.8274511 1",
                                    font: CUI.Font.RobotoCondensedRegular,
                                    fontSize: 16,
                                    align: TextAnchor.MiddleLeft,
                                    overflow: VerticalWrapMode.Overflow,
                                    anchorMin: "0 0",
                                    anchorMax: "0.6 1",
                                    offsetMin: "10 4",
                                    offsetMax: "0 -4"
                                    /* name: "Text" */);
                                {
                                    CUI.Element mLMGoG = luxLvm.AddPanel(
                                        material: "assets/icons/iconmaterial.mat",
                                        color: "0.1415094 0.1395069 0.1395069 1",
                                        imageType: UnityEngine.UI.Image.Type.Simple,
                                        anchorMin: "0.6 0",
                                        anchorMax: "1 1",
                                        offsetMin: "10 0",
                                        offsetMax: "0 0"
                                        /* name: "Panel" */);
                                    var TnSDsX_value = databaseConfig.DeliveryVariantTimes[DeliveryVariant.Standard].ToString();
                                    mLMGoG.AddInputfield(
                                        command: "cargonpc admin-settings DeliveryVariantTimes Standard",
                                        text: TnSDsX_value,
                                        color: "0.7264151 0.7264151 0.7264151 1",
                                        font: CUI.Font.RobotoCondensedRegular,
                                        fontSize: 18,
                                        align: TextAnchor.MiddleCenter,
                                        lineType: InputField.LineType.SingleLine,
                                        charsLimit: 0,
                                        anchorMin: "0 0",
                                        anchorMax: "1 1",
                                        offsetMin: "10 4",
                                        offsetMax: "-10 -4"
                                        /* name: "Inputfield" */);
                                }
                            }
                            {
                                CUI.Element hSsGrx = gcbOMU.AddContainer(
                                    anchorMin: "0.5 0.5",
                                    anchorMax: "0.5 0.5",
                                    offsetMin: "-255.177 -100",
                                    offsetMax: "275 -60"
                                    /* name: "Express" */);
                                hSsGrx.AddText(
                                    text: ЭКСПРЕСС,
                                    color: "0.8862746 0.8588236 0.8274511 1",
                                    font: CUI.Font.RobotoCondensedRegular,
                                    fontSize: 16,
                                    align: TextAnchor.MiddleLeft,
                                    overflow: VerticalWrapMode.Overflow,
                                    anchorMin: "0 0",
                                    anchorMax: "0.6 1",
                                    offsetMin: "10 4",
                                    offsetMax: "0 -4"
                                    /* name: "Text" */);
                                {
                                    CUI.Element WHWQYC = hSsGrx.AddPanel(
                                        material: "assets/icons/iconmaterial.mat",
                                        color: "0.1415094 0.1395069 0.1395069 1",
                                        imageType: UnityEngine.UI.Image.Type.Simple,
                                        anchorMin: "0.6 0",
                                        anchorMax: "1 1",
                                        offsetMin: "10 0",
                                        offsetMax: "0 0"
                                        /* name: "Panel" */);
                                    var XGbUXy_value = databaseConfig.DeliveryVariantTimes[DeliveryVariant.Express].ToString();
                                    WHWQYC.AddInputfield(
                                        command: "cargonpc admin-settings DeliveryVariantTimes Express",
                                        text: XGbUXy_value,
                                        color: "0.7264151 0.7264151 0.7264151 1",
                                        font: CUI.Font.RobotoCondensedRegular,
                                        fontSize: 18,
                                        align: TextAnchor.MiddleCenter,
                                        lineType: InputField.LineType.SingleLine,
                                        charsLimit: 0,
                                        anchorMin: "0 0",
                                        anchorMax: "1 1",
                                        offsetMin: "10 4",
                                        offsetMax: "-10 -4"
                                        /* name: "Inputfield" */);
                                }
                            }
                        }
                    }
                    {
                        CUI.Element WFiovM = vbAnay.AddContainer(
                            anchorMin: "0 1",
                            anchorMax: "1 1",
                            offsetMin: "15 -462.5",
                            offsetMax: "-15 -377.5"
                            /* name: "Storage" */);
                        WFiovM.AddText(
                            text: ХРАНЕНИЕ,
                            color: "0.8862746 0.8588236 0.8274511 1",
                            font: CUI.Font.RobotoCondensedBold,
                            fontSize: 24,
                            align: TextAnchor.MiddleLeft,
                            overflow: VerticalWrapMode.Overflow,
                            anchorMin: "0 1",
                            anchorMax: "1 1",
                            offsetMin: "0 -30",
                            offsetMax: "0 0"
                            /* name: "Title" */);
                        {
                            CUI.Element OTeVgz = WFiovM.AddContainer(
                                anchorMin: "0 1",
                                anchorMax: "0 1",
                                offsetMin: "0 -70",
                                offsetMax: "550 -30"
                                /* name: "FreeStorageHours" */);
                            OTeVgz.AddText(
                                text: СКОЛЬКО_ЧАСОВ_БЕСПЛАТНОГО_ХРАНЕНИЯ,
                                color: "0.8862746 0.8588236 0.8274511 1",
                                font: CUI.Font.RobotoCondensedRegular,
                                fontSize: 16,
                                align: TextAnchor.MiddleLeft,
                                overflow: VerticalWrapMode.Overflow,
                                anchorMin: "0 0",
                                anchorMax: "0.6 1",
                                offsetMin: "10 4",
                                offsetMax: "0 -4"
                                /* name: "Text" */);
                            {
                                CUI.Element kbaKbt = OTeVgz.AddPanel(
                                    material: "assets/icons/iconmaterial.mat",
                                    color: "0.1415094 0.1395069 0.1395069 1",
                                    imageType: UnityEngine.UI.Image.Type.Simple,
                                    anchorMin: "0.6 0",
                                    anchorMax: "1 1",
                                    offsetMin: "10 0",
                                    offsetMax: "0 0"
                                    /* name: "Panel" */);
                                var zPGVZZ_value = databaseConfig.FreeStorageHours.ToString();
                                kbaKbt.AddInputfield(
                                    command: "cargonpc admin-settings FreeStorageHours",
                                    text: zPGVZZ_value,
                                    color: "0.7264151 0.7264151 0.7264151 1",
                                    font: CUI.Font.RobotoCondensedRegular,
                                    fontSize: 18,
                                    align: TextAnchor.MiddleCenter,
                                    lineType: InputField.LineType.SingleLine,
                                    charsLimit: 0,
                                    anchorMin: "0 0",
                                    anchorMax: "1 1",
                                    offsetMin: "10 4",
                                    offsetMax: "-10 -4"
                                    /* name: "Inputfield" */);
                            }
                        }
                        {
                            CUI.Element TEqiHX = WFiovM.AddContainer(
                                anchorMin: "0 1",
                                anchorMax: "0 1",
                                offsetMin: "580 -70",
                                offsetMax: "1130 -30"
                                /* name: "StorageCostPerHour" */);
                            TEqiHX.AddText(
                                text: СТОИМОСТЬ_ХРАНЕНИЯ_ЗА_ЧАС,
                                color: "0.8862746 0.8588236 0.8274511 1",
                                font: CUI.Font.RobotoCondensedRegular,
                                fontSize: 16,
                                align: TextAnchor.MiddleLeft,
                                overflow: VerticalWrapMode.Overflow,
                                anchorMin: "0 0",
                                anchorMax: "0.6 1",
                                offsetMin: "10 4",
                                offsetMax: "0 -4"
                                /* name: "Text" */);
                            {
                                CUI.Element pGIXZc = TEqiHX.AddPanel(
                                    material: "assets/icons/iconmaterial.mat",
                                    color: "0.1415094 0.1395069 0.1395069 1",
                                    imageType: UnityEngine.UI.Image.Type.Simple,
                                    anchorMin: "0.6 0",
                                    anchorMax: "1 1",
                                    offsetMin: "10 0",
                                    offsetMax: "0 0"
                                    /* name: "Panel" */);
                                var IFihLY_value = databaseConfig.StorageCostPerHour.ToString();
                                pGIXZc.AddInputfield(
                                    command: "cargonpc admin-settings StorageCostPerHour",
                                    text: IFihLY_value,
                                    color: "0.7264151 0.7264151 0.7264151 1",
                                    font: CUI.Font.RobotoCondensedRegular,
                                    fontSize: 18,
                                    align: TextAnchor.MiddleCenter,
                                    lineType: InputField.LineType.SingleLine,
                                    charsLimit: 0,
                                    anchorMin: "0 0",
                                    anchorMax: "1 1",
                                    offsetMin: "10 4",
                                    offsetMax: "-10 -4"
                                    /* name: "Inputfield" */);
                            }
                        }
                    }
                    {
                        CUI.Element UfYAbZ = vbAnay.AddContainer(
                            anchorMin: "0 1",
                            anchorMax: "1 1",
                            offsetMin: "15 -560",
                            offsetMax: "-15 -475"
                            /* name: "DeliveryPrices" */);
                        UfYAbZ.AddText(
                            text: ЦЕНЫ_ЗА_ОТПРАВКУ,
                            color: "0.8862746 0.8588236 0.8274511 1",
                            font: CUI.Font.RobotoCondensedBold,
                            fontSize: 24,
                            align: TextAnchor.MiddleLeft,
                            overflow: VerticalWrapMode.Overflow,
                            anchorMin: "0 1",
                            anchorMax: "1 1",
                            offsetMin: "0 -30",
                            offsetMax: "0 0"
                            /* name: "Title" */);
                        {
                            CUI.Element KDpLLM = UfYAbZ.AddContainer(
                                anchorMin: "0 1",
                                anchorMax: "0 1",
                                offsetMin: "0 -70",
                                offsetMax: "550 -30"
                                /* name: "Category" */);
                            KDpLLM.AddText(
                                text: ЦЕНА_ЗА_ОТПРАВКУ_ПРЕДМЕТА_ИЗ_КАТЕГОРИИ_LOWER,
                                color: "0.8862746 0.8588236 0.8274511 1",
                                font: CUI.Font.RobotoCondensedRegular,
                                fontSize: 16,
                                align: TextAnchor.MiddleLeft,
                                overflow: VerticalWrapMode.Overflow,
                                anchorMin: "0 0",
                                anchorMax: "0.6 1",
                                offsetMin: "10 4",
                                offsetMax: "0 -4"
                                /* name: "Text" */);
                            {
                                CUI.Element ZuQDRc = KDpLLM.AddButton(
                                    command: "cargonpc popup DeliveryPricePerCategory",
                                    color: "0.1411765 0.1411765 0.1411765 1",
                                    imageType: UnityEngine.UI.Image.Type.Simple,
                                    anchorMin: "0.6 0",
                                    anchorMax: "1 1",
                                    offsetMin: "10 0",
                                    offsetMax: "0 0"
                                    /* name: "Button" */);
                                ZuQDRc.AddText(
                                    text: ОТКРЫТЬ,
                                    color: "0.8039216 0.7803922 0.7490196 1",
                                    font: CUI.Font.RobotoCondensedBold,
                                    fontSize: 19,
                                    align: TextAnchor.MiddleCenter,
                                    overflow: VerticalWrapMode.Overflow,
                                    anchorMin: "0 0",
                                    anchorMax: "1 1",
                                    offsetMin: "0 0",
                                    offsetMax: "0 0"
                                    /* name: "Text" */);
                            }
                        }
                        {
                            CUI.Element bpxGoZ = UfYAbZ.AddContainer(
                                anchorMin: "0 1",
                                anchorMax: "0 1",
                                offsetMin: "578 -70",
                                offsetMax: "1128 -30"
                                /* name: "Item" */);
                            bpxGoZ.AddText(
                                text: ЦЕНА_ЗА_ОТПРАВКУ_ПРЕДМЕТА_LOWER,
                                color: "0.8862746 0.8588236 0.8274511 1",
                                font: CUI.Font.RobotoCondensedRegular,
                                fontSize: 16,
                                align: TextAnchor.MiddleLeft,
                                overflow: VerticalWrapMode.Overflow,
                                anchorMin: "0 0",
                                anchorMax: "0.6 1",
                                offsetMin: "10 4",
                                offsetMax: "0 -4"
                                /* name: "Text" */);
                            {
                                CUI.Element ckxxUc = bpxGoZ.AddButton(
                                    command: "cargonpc popup DeliveryPricePerItem",
                                    color: "0.1411765 0.1411765 0.1411765 1",
                                    imageType: UnityEngine.UI.Image.Type.Simple,
                                    anchorMin: "0.6 0",
                                    anchorMax: "1 1",
                                    offsetMin: "10 0",
                                    offsetMax: "0 0"
                                    /* name: "Button" */);
                                ckxxUc.AddText(
                                    text: ОТКРЫТЬ,
                                    color: "0.8039216 0.7803922 0.7490196 1",
                                    font: CUI.Font.RobotoCondensedBold,
                                    fontSize: 19,
                                    align: TextAnchor.MiddleCenter,
                                    overflow: VerticalWrapMode.Overflow,
                                    anchorMin: "0 0",
                                    anchorMax: "1 1",
                                    offsetMin: "0 0",
                                    offsetMax: "0 0"
                                    /* name: "Text" */);
                            }
                        }
                    }
                    {
                        CUI.Element GCYAel = vbAnay.AddContainer(
                            anchorMin: "0 1",
                            anchorMax: "1 1",
                            offsetMin: "15 -720.939",
                            offsetMax: "-15 -577.104"
                            /* name: "NPC" */);
                        CUI.Element GeezBC = GCYAel.AddText(
                            text: "НПС",
                            color: "0.8862746 0.8588236 0.8274511 1",
                            font: CUI.Font.RobotoCondensedBold,
                            fontSize: 24,
                            align: TextAnchor.MiddleLeft,
                            overflow: VerticalWrapMode.Overflow,
                            anchorMin: "0 1",
                            anchorMax: "1 1",
                            offsetMin: "0 -30",
                            offsetMax: "0 0"
                            /* name: "Title" */);
                        {
                            CUI.Element tYrffY = GeezBC.AddButton(
                                command: "cargonpc admin-settings NpcIndex prev",
                                color: "1 1 1 0",
                                imageType: UnityEngine.UI.Image.Type.Simple,
                                anchorMin: "0.5 0.5",
                                anchorMax: "0.5 0.5",
                                offsetMin: "-538.378 -15",
                                offsetMax: "-516.623 15"
                                /* name: "Button" */);
                            tYrffY.AddText(
                                text: "<",
                                color: "0.8862745 0.8588235 0.827451 1",
                                font: CUI.Font.RobotoCondensedRegular,
                                fontSize: 20,
                                align: TextAnchor.MiddleCenter,
                                overflow: VerticalWrapMode.Overflow,
                                anchorMin: "0 0",
                                anchorMax: "1 1",
                                offsetMin: "0 0",
                                offsetMax: "0 0"
                                /* name: "Text" */);
                        }
                        GeezBC.AddText(
                            text: this.NpcIndex.ToString(),
                            color: "0.8862745 0.8588235 0.827451 1",
                            font: CUI.Font.RobotoCondensedBold,
                            fontSize: 20,
                            align: TextAnchor.MiddleCenter,
                            overflow: VerticalWrapMode.Overflow,
                            anchorMin: "0 0",
                            anchorMax: "1 1",
                            offsetMin: "88.4 0",
                            offsetMax: "-1087.1 0"
                            /* name: "Text (1)" */);
                        {
                            CUI.Element KFJdJr = GeezBC.AddButton(
                                command: "cargonpc admin-settings NpcIndex next",
                                color: "1 1 1 0",
                                imageType: UnityEngine.UI.Image.Type.Simple,
                                anchorMin: "0.5 0.5",
                                anchorMax: "0.5 0.5",
                                offsetMin: "-482.078 -15",
                                offsetMax: "-460.322 15"
                                /* name: "Button (1)" */);
                            KFJdJr.AddText(
                                text: ">",
                                color: "0.8862745 0.8588235 0.827451 1",
                                font: CUI.Font.RobotoCondensedRegular,
                                fontSize: 20,
                                align: TextAnchor.MiddleCenter,
                                overflow: VerticalWrapMode.Overflow,
                                anchorMin: "0 0",
                                anchorMax: "1 1",
                                offsetMin: "0 0",
                                offsetMax: "0 0"
                                /* name: "Text" */);
                        }
                        {
                            CUI.Element wXWHCY = GeezBC.AddButton(
                                command: "cargonpc admin-settings NpcList add",
                                color: "1 1 1 0",
                                imageType: UnityEngine.UI.Image.Type.Simple,
                                anchorMin: "0.5 0.5",
                                anchorMax: "0.5 0.5",
                                offsetMin: "-438.338 -15",
                                offsetMax: "-416.582 15"
                                /* name: "Button (2)" */);
                            wXWHCY.AddPanel(
                                sprite: "assets/icons/add.png",
                                material: "assets/icons/iconmaterial.mat",
                                color: "0.8862745 0.8588235 0.827451 1",
                                imageType: UnityEngine.UI.Image.Type.Simple,
                                anchorMin: "0.5 0.5",
                                anchorMax: "0.5 0.5",
                                offsetMin: "-7.5 -7.5",
                                offsetMax: "7.5 7.5"
                                /* name: "Panel" */);
                        }
                        {
                            CUI.Element CPCsbe = GeezBC.AddButton(
                                command: $"cargonpc admin-settings NpcList remove {NpcIndex}",
                                color: "1 1 1 0",
                                imageType: UnityEngine.UI.Image.Type.Simple,
                                anchorMin: "0.5 0.5",
                                anchorMax: "0.5 0.5",
                                offsetMin: "-416.578 -15",
                                offsetMax: "-394.822 15"
                                /* name: "Button (3)" */);
                            CPCsbe.AddPanel(
                                sprite: "assets/icons/subtract.png",
                                material: "assets/icons/iconmaterial.mat",
                                color: "0.8862745 0.8588235 0.827451 1",
                                imageType: UnityEngine.UI.Image.Type.Simple,
                                anchorMin: "0.5 0.5",
                                anchorMax: "0.5 0.5",
                                offsetMin: "-7.5 -7.5",
                                offsetMax: "7.5 7.5"
                                /* name: "Panel" */);
                        }

                        Configuration.NPC npcConfig = config.Npc.ElementAtOrDefault(NpcIndex);

                        if (npcConfig != null)
                        {
                            {
                                CUI.Element KOwIug = GCYAel.AddContainer(
                                    anchorMin: "0 1",
                                    anchorMax: "0 1",
                                    offsetMin: "0 -77",
                                    offsetMax: "550 -37"
                                    /* name: "Name" */);
                                KOwIug.AddText(
                                    text: ИМЯ,
                                    color: "0.8862746 0.8588236 0.8274511 1",
                                    font: CUI.Font.RobotoCondensedRegular,
                                    fontSize: 16,
                                    align: TextAnchor.MiddleLeft,
                                    overflow: VerticalWrapMode.Overflow,
                                    anchorMin: "0 0",
                                    anchorMax: "0.6 1",
                                    offsetMin: "10 4",
                                    offsetMax: "0 -4"
                                    /* name: "Text" */);
                                {
                                    CUI.Element NBoGjb = KOwIug.AddPanel(
                                        material: "assets/icons/iconmaterial.mat",
                                        color: "0.1415094 0.1395069 0.1395069 1",
                                        imageType: UnityEngine.UI.Image.Type.Simple,
                                        anchorMin: "0.6 0",
                                        anchorMax: "1 1",
                                        offsetMin: "10 0",
                                        offsetMax: "0 0"
                                        /* name: "Panel" */);
                                    var uAYFZM_value = npcConfig.Name;
                                    NBoGjb.AddInputfield(
                                        command: "cargonpc admin-settings NpcName",
                                        text: uAYFZM_value,
                                        color: "0.7264151 0.7264151 0.7264151 1",
                                        font: CUI.Font.RobotoCondensedRegular,
                                        fontSize: 18,
                                        align: TextAnchor.MiddleCenter,
                                        lineType: InputField.LineType.SingleLine,
                                        charsLimit: 0,
                                        anchorMin: "0 0",
                                        anchorMax: "1 1",
                                        offsetMin: "10 4",
                                        offsetMax: "-10 -4"
                                        /* name: "Inputfield" */);
                                }
                            }
                            {
                                CUI.Element UNMtzQ = GCYAel.AddContainer(
                                    anchorMin: "0 1",
                                    anchorMax: "0 1",
                                    offsetMin: "568.3 -77",
                                    offsetMax: "1118.3 -37"
                                    /* name: "Items" */);
                                UNMtzQ.AddText(
                                    text: ПРЕДМЕТЫ,
                                    color: "0.8862746 0.8588236 0.8274511 1",
                                    font: CUI.Font.RobotoCondensedRegular,
                                    fontSize: 16,
                                    align: TextAnchor.MiddleLeft,
                                    overflow: VerticalWrapMode.Overflow,
                                    anchorMin: "0 0",
                                    anchorMax: "0.6 1",
                                    offsetMin: "10 4",
                                    offsetMax: "0 -4"
                                    /* name: "Text" */);
                                {
                                    CUI.Element VQwcDr = UNMtzQ.AddButton(
                                        command: "cargonpc popup NpcItems",
                                        color: "0.1411765 0.1411765 0.1411765 1",
                                        imageType: UnityEngine.UI.Image.Type.Simple,
                                        anchorMin: "0.6 0",
                                        anchorMax: "1 1",
                                        offsetMin: "10 0",
                                        offsetMax: "0 0"
                                        /* name: "Button" */);
                                    VQwcDr.AddText(
                                        text: ОТКРЫТЬ,
                                        color: "0.8039216 0.7803922 0.7490196 1",
                                        font: CUI.Font.RobotoCondensedBold,
                                        fontSize: 19,
                                        align: TextAnchor.MiddleCenter,
                                        overflow: VerticalWrapMode.Overflow,
                                        anchorMin: "0 0",
                                        anchorMax: "1 1",
                                        offsetMin: "0 0",
                                        offsetMax: "0 0"
                                        /* name: "Text" */);
                                }
                            }
                            {
                                CUI.Element vomNSP = GCYAel.AddContainer(
                                    anchorMin: "0 1",
                                    anchorMax: "0 1",
                                    offsetMin: "0 -124.7",
                                    offsetMax: "550 -84.7"
                                    /* name: "Coords" */);
                                vomNSP.AddText(
                                    text: "Точка спавна",
                                    color: "0.8862746 0.8588236 0.8274511 1",
                                    font: CUI.Font.RobotoCondensedRegular,
                                    fontSize: 16,
                                    align: TextAnchor.MiddleLeft,
                                    overflow: VerticalWrapMode.Overflow,
                                    anchorMin: "0 0",
                                    anchorMax: "0.6 1",
                                    offsetMin: "10 4",
                                    offsetMax: "0 -4"
                                    /* name: "Text" */);
                                {
                                    CUI.Element eHuZrz = vomNSP.AddButton(
                                        command: "cargonpc admin-settings NpcCoord set",
                                        color: "0.1411765 0.1411765 0.1411765 1",
                                        imageType: UnityEngine.UI.Image.Type.Simple,
                                        anchorMin: "0.6 0",
                                        anchorMax: "1 1",
                                        offsetMin: "10 0",
                                        offsetMax: "0 0"
                                        /* name: "Button" */);
                                    eHuZrz.AddText(
                                        text: "УСТАНОВИТЬ",
                                        color: "0.8039216 0.7803922 0.7490196 1",
                                        font: CUI.Font.RobotoCondensedBold,
                                        fontSize: 19,
                                        align: TextAnchor.MiddleCenter,
                                        overflow: VerticalWrapMode.Overflow,
                                        anchorMin: "0 0",
                                        anchorMax: "1 1",
                                        offsetMin: "0 0",
                                        offsetMax: "0 0"
                                        /* name: "Text" */);
                                }
                            }
                        }
                    }
                    {
                        CUI.Element ESRmcP = vbAnay.AddContainer(
                           anchorMin: "0 1",
                           anchorMax: "1 1",
                           offsetMin: "15 -864.778",
                           offsetMax: "-15 -720.942"
                           /* name: "Visibility" */);
                        ESRmcP.AddText(
                            text: "ВИДИМОСТЬ",
                            color: "0.8862746 0.8588236 0.8274511 1",
                            font: CUI.Font.RobotoCondensedBold,
                            fontSize: 24,
                            align: TextAnchor.MiddleLeft,
                            overflow: VerticalWrapMode.Overflow,
                            anchorMin: "0 1",
                            anchorMax: "1 1",
                            offsetMin: "0 -30",
                            offsetMax: "0 0"
                            /* name: "Title" */);
                        {
                            CUI.Element DiKIXV = ESRmcP.AddContainer(
                                anchorMin: "0 1",
                                anchorMax: "0 1",
                                offsetMin: "0 -77",
                                offsetMax: "550 -37"
                                /* name: "OnlyCurrentSaveProtocol" */);
                            DiKIXV.AddText(
                                text: "Отображать контейнеры только текущего протокола",
                                color: "0.8862746 0.8588236 0.8274511 1",
                                font: CUI.Font.RobotoCondensedRegular,
                                fontSize: 16,
                                align: TextAnchor.MiddleLeft,
                                overflow: VerticalWrapMode.Overflow,
                                anchorMin: "0 0",
                                anchorMax: "0.6 1",
                                offsetMin: "10 4",
                                offsetMax: "0 -4"
                                /* name: "Text" */);
                            {
                                CUI.Element RlNMDS = DiKIXV.AddButton(
                                    command: "cargonpc admin-settings save-protocol",
                                    color: "0.1411765 0.1411765 0.1411765 1",
                                    imageType: UnityEngine.UI.Image.Type.Simple,
                                    anchorMin: "0.6 0",
                                    anchorMax: "1 1",
                                    offsetMin: "10 0",
                                    offsetMax: "0 0"
                                    /* name: "Button" */);
                                var yglxjn_value = (databaseConfig.VisibileOnlyCurrentProtocol ? "Да" : "Нет");
                                RlNMDS.AddText(
                                    text: yglxjn_value,
                                    color: "0.8039216 0.7803922 0.7490196 1",
                                    font: CUI.Font.RobotoCondensedRegular,
                                    fontSize: 19,
                                    align: TextAnchor.MiddleCenter,
                                    overflow: VerticalWrapMode.Overflow,
                                    anchorMin: "0 0",
                                    anchorMax: "1 1",
                                    offsetMin: "0 0",
                                    offsetMax: "0 0"
                                    /* name: "Text" */);
                            }
                        }
                    }
                    {
                        CUI.Element KZVtVD = vbAnay.AddContainer(
                            anchorMin: "0 1",
                            anchorMax: "1 1",
                            offsetMin: "586.3 -808.961",
                            offsetMax: "-15 -720.939"
                            /* name: "Exchange Rate" */);
                        KZVtVD.AddText(
                            text: "КУРС ВАЛЮТ",
                            color: "0.8862746 0.8588236 0.8274511 1",
                            font: CUI.Font.RobotoCondensedBold,
                            fontSize: 24,
                            align: TextAnchor.MiddleLeft,
                            overflow: VerticalWrapMode.Overflow,
                            anchorMin: "0 1",
                            anchorMax: "1 1",
                            offsetMin: "0 -30",
                            offsetMax: "0 0"
                            /* name: "Title" */);
                        {
                            CUI.Element LHptZg = KZVtVD.AddContainer(
                                anchorMin: "0 1",
                                anchorMax: "0 1",
                                offsetMin: "0 -78.1",
                                offsetMax: "550 -38.1"
                                /* name: "Rate" */);
                            LHptZg.AddText(
                                text: "Курс монеты к скрапу",
                                color: "0.8862746 0.8588236 0.8274511 1",
                                font: CUI.Font.RobotoCondensedRegular,
                                fontSize: 16,
                                align: TextAnchor.MiddleLeft,
                                overflow: VerticalWrapMode.Overflow,
                                anchorMin: "0 0",
                                anchorMax: "0.6 1",
                                offsetMin: "10 4",
                                offsetMax: "0 -4"
                                /* name: "Text" */);
                            {
                                CUI.Element IGLhev = LHptZg.AddPanel(
                                    material: "assets/icons/iconmaterial.mat",
                                    color: "0.1415094 0.1395069 0.1395069 1",
                                    imageType: UnityEngine.UI.Image.Type.Simple,
                                    anchorMin: "0.6 0",
                                    anchorMax: "1 1",
                                    offsetMin: "10 0",
                                    offsetMax: "0 0"
                                    /* name: "Panel" */);
                                var bUxMFG_value = config.ExchangeRate.ToString();
                                IGLhev.AddInputfield(
                                    command: "cargonpc admin-settings set-exchange-rate",
                                    text: bUxMFG_value,
                                    color: "0.7264151 0.7264151 0.7264151 1",
                                    font: CUI.Font.RobotoCondensedRegular,
                                    fontSize: 18,
                                    align: TextAnchor.MiddleCenter,
                                    lineType: InputField.LineType.SingleLine,
                                    charsLimit: 0,
                                    anchorMin: "0 0",
                                    anchorMax: "1 1",
                                    offsetMin: "10 4",
                                    offsetMax: "-10 -4"
                                    /* name: "Inputfield" */);
                            }
                        }
                    }
                    {
                        CUI.Element DQKEDj = vbAnay.AddContainer(
                            anchorMin: "0 1",
                            anchorMax: "1 1",
                            offsetMin: "15 -952.798",
                            offsetMax: "-15 -808.962"
                            /* name: "SpawnPoints" */);
                        {
                            CUI.Element FSJSzV = DQKEDj.AddText(
                                text: "ТОЧКИ СПАВНА",
                                color: "0.8862746 0.8588236 0.8274511 1",
                                font: CUI.Font.RobotoCondensedBold,
                                fontSize: 24,
                                align: TextAnchor.MiddleLeft,
                                overflow: VerticalWrapMode.Overflow,
                                anchorMin: "0 1",
                                anchorMax: "1 1",
                                offsetMin: "0 -30",
                                offsetMax: "0 0"
                                /* name: "Title" */);
                            {
                                CUI.Element rXoWYN = FSJSzV.AddButton(
                                    command: "cargonpc admin-settings SpawnPointIndex prev",
                                    color: "1 1 1 0",
                                    imageType: UnityEngine.UI.Image.Type.Simple,
                                    anchorMin: "0.5 0.5",
                                    anchorMax: "0.5 0.5",
                                    offsetMin: "-434.778 -14.4",
                                    offsetMax: "-413.023 15.6"
                                    /* name: "Button" */);
                                rXoWYN.AddText(
                                    text: "<",
                                    color: "0.8862745 0.8588235 0.827451 1",
                                    font: CUI.Font.RobotoCondensedRegular,
                                    fontSize: 20,
                                    align: TextAnchor.MiddleCenter,
                                    overflow: VerticalWrapMode.Overflow,
                                    anchorMin: "0 0",
                                    anchorMax: "1 1",
                                    offsetMin: "0 0",
                                    offsetMax: "0 0"
                                    /* name: "Text" */);
                            }
                            var RmDkHb_index = this.SpawnPointIndex.ToString();
                            FSJSzV.AddText(
                                text: RmDkHb_index,
                                color: "0.8862745 0.8588235 0.827451 1",
                                font: CUI.Font.RobotoCondensedBold,
                                fontSize: 20,
                                align: TextAnchor.MiddleCenter,
                                overflow: VerticalWrapMode.Overflow,
                                anchorMin: "0 0",
                                anchorMax: "1 1",
                                offsetMin: "192 0.6",
                                offsetMax: "-983.5 0.6"
                                /* name: "Text (1)" */);
                            {
                                CUI.Element mrjgQz = FSJSzV.AddButton(
                                    command: "cargonpc admin-settings SpawnPointIndex next",
                                    color: "1 1 1 0",
                                    imageType: UnityEngine.UI.Image.Type.Simple,
                                    anchorMin: "0.5 0.5",
                                    anchorMax: "0.5 0.5",
                                    offsetMin: "-378.478 -14.4",
                                    offsetMax: "-356.722 15.6"
                                    /* name: "Button (1)" */);
                                mrjgQz.AddText(
                                    text: ">",
                                    color: "0.8862745 0.8588235 0.827451 1",
                                    font: CUI.Font.RobotoCondensedRegular,
                                    fontSize: 20,
                                    align: TextAnchor.MiddleCenter,
                                    overflow: VerticalWrapMode.Overflow,
                                    anchorMin: "0 0",
                                    anchorMax: "1 1",
                                    offsetMin: "0 0",
                                    offsetMax: "0 0"
                                    /* name: "Text" */);
                            }
                            {
                                CUI.Element bEVKDY = FSJSzV.AddButton(
                                    command: "cargonpc admin-settings SpawnPoints add",
                                    color: "1 1 1 0",
                                    imageType: UnityEngine.UI.Image.Type.Simple,
                                    anchorMin: "0.5 0.5",
                                    anchorMax: "0.5 0.5",
                                    offsetMin: "-334.738 -14.4",
                                    offsetMax: "-312.982 15.6"
                                    /* name: "Button (2)" */);
                                bEVKDY.AddPanel(
                                    sprite: "assets/icons/add.png",
                                    material: "assets/icons/iconmaterial.mat",
                                    color: "0.8862745 0.8588235 0.827451 1",
                                    imageType: UnityEngine.UI.Image.Type.Simple,
                                    anchorMin: "0.5 0.5",
                                    anchorMax: "0.5 0.5",
                                    offsetMin: "-7.5 -7.5",
                                    offsetMax: "7.5 7.5"
                                    /* name: "Panel" */);
                            }
                            {
                                CUI.Element ABqWcB = FSJSzV.AddButton(
                                    command: "cargonpc admin-settings SpawnPoints remove",
                                    color: "1 1 1 0",
                                    imageType: UnityEngine.UI.Image.Type.Simple,
                                    anchorMin: "0.5 0.5",
                                    anchorMax: "0.5 0.5",
                                    offsetMin: "-312.978 -14.4",
                                    offsetMax: "-291.222 15.6"
                                    /* name: "Button (3)" */);
                                ABqWcB.AddPanel(
                                    sprite: "assets/icons/subtract.png",
                                    material: "assets/icons/iconmaterial.mat",
                                    color: "0.8862745 0.8588235 0.827451 1",
                                    imageType: UnityEngine.UI.Image.Type.Simple,
                                    anchorMin: "0.5 0.5",
                                    anchorMax: "0.5 0.5",
                                    offsetMin: "-7.5 -7.5",
                                    offsetMax: "7.5 7.5"
                                    /* name: "Panel" */);
                            }
                        }
                        Console.WriteLine(PluginInstance.currentServer.SpawnPoints);
                        if (SpawnPointIndex < PluginInstance.currentServer.SpawnPoints.Count)
                        {
                            SpawnPoint spawnPoint = PluginInstance.currentServer.SpawnPoints[SpawnPointIndex];
                            CUI.Element sZHLtf = DQKEDj.AddContainer(
                                anchorMin: "0 1",
                                anchorMax: "0 1",
                                offsetMin: "0 -70",
                                offsetMax: "550 -30"
                                /* name: "Name" */);
                            sZHLtf.AddText(
                                text: "Имя",
                                color: "0.8862746 0.8588236 0.8274511 1",
                                font: CUI.Font.RobotoCondensedRegular,
                                fontSize: 16,
                                align: TextAnchor.MiddleLeft,
                                overflow: VerticalWrapMode.Overflow,
                                anchorMin: "0 0",
                                anchorMax: "0.6 1",
                                offsetMin: "10 4",
                                offsetMax: "0 -4"
                                /* name: "Text" */);
                            {
                                CUI.Element mtLZph = sZHLtf.AddPanel(
                                    material: "assets/icons/iconmaterial.mat",
                                    color: "0.1415094 0.1395069 0.1395069 1",
                                    imageType: UnityEngine.UI.Image.Type.Simple,
                                    anchorMin: "0.6 0",
                                    anchorMax: "1 1",
                                    offsetMin: "10 0",
                                    offsetMax: "0 0"
                                    /* name: "Panel" */);
                                var APEFDW_value = spawnPoint.Name ?? string.Empty;
                                mtLZph.AddInputfield(
                                    command: "cargonpc admin-settings SpawnPoints set-name",
                                    text: APEFDW_value,
                                    color: "0.7264151 0.7264151 0.7264151 1",
                                    font: CUI.Font.RobotoCondensedRegular,
                                    fontSize: 18,
                                    align: TextAnchor.MiddleCenter,
                                    lineType: InputField.LineType.SingleLine,
                                    charsLimit: 0,
                                    anchorMin: "0 0",
                                    anchorMax: "1 1",
                                    offsetMin: "10 4",
                                    offsetMax: "-10 -4"
                                    /* name: "Inputfield" */);
                            }
                        }
                        DQKEDj.AddText(
                            text: "Для реализации общей локации с несколькими точками спавна - установите одинаковое общее название для нескольких точек которые хотите объединить",
                            color: "0.8862746 0.8588236 0.8274511 1",
                            font: CUI.Font.RobotoCondensedRegular,
                            fontSize: 16,
                            align: TextAnchor.MiddleLeft,
                            overflow: VerticalWrapMode.Overflow,
                            anchorMin: "0 0",
                            anchorMax: "0.6 1",
                            offsetMin: "574.967 77.835",
                            offsetMax: "427.433 -34"
                            /* name: "Text (1)" */);
                        {
                            CUI.Element HyEYMF = DQKEDj.AddButton(
                                command: "cargonpc admin-settings SpawnPoints set-position",
                                color: "0.1411765 0.1411765 0.1411765 1",
                                imageType: UnityEngine.UI.Image.Type.Simple,
                                anchorMin: "0.5 0.5",
                                anchorMax: "0.5 0.5",
                                offsetMin: "-605 -44.965",
                                offsetMax: "-395 -4.965"
                                /* name: "Button" */);
                            HyEYMF.AddText(
                                text: "УСТАНОВИТЬ КООРДИНАТЫ",
                                color: "0.7254902 0.7254902 0.7254902 1",
                                font: CUI.Font.RobotoCondensedBold,
                                fontSize: 15,
                                align: TextAnchor.MiddleCenter,
                                overflow: VerticalWrapMode.Overflow,
                                anchorMin: "0 0",
                                anchorMax: "1 1",
                                offsetMin: "0 0",
                                offsetMax: "0 0"
                                /* name: "Text" */);
                        }
                        {
                            CUI.Element FzXSnw = DQKEDj.AddButton(
                                command: "cargonpc admin-settings SpawnPoints teleport",
                                color: "0.1411765 0.1411765 0.1411765 1",
                                imageType: UnityEngine.UI.Image.Type.Simple,
                                anchorMin: "0.5 0.5",
                                anchorMax: "0.5 0.5",
                                offsetMin: "-385.7 -44.965",
                                offsetMax: "-175.7 -4.965"
                                /* name: "Button (1)" */);
                            FzXSnw.AddText(
                                text: "ТЕЛЕПОРТИРОВАТЬСЯ",
                                color: "0.7254902 0.7254902 0.7254902 1",
                                font: CUI.Font.RobotoCondensedBold,
                                fontSize: 15,
                                align: TextAnchor.MiddleCenter,
                                overflow: VerticalWrapMode.Overflow,
                                anchorMin: "0 0",
                                anchorMax: "1 1",
                                offsetMin: "0 0",
                                offsetMax: "0 0"
                                /* name: "Text" */);
                        }
                    }
                }
            }



            public void ContainerPricesPopup()
            {
                CUI.Root root = new CUI.Root("CargoUI_Content");
                {
                    CUI.Element HQIaYh = root.AddContainer(
                        anchorMin: "0 0",
                        anchorMax: "1 1",
                        offsetMin: "0 0",
                        offsetMax: "0 0",
                        name: "ContainerPricesPopup").AddDestroySelfAttribute();
                    HQIaYh.AddButton(
                        command: null,
                        close: "ContainerPricesPopup",
                        material: "assets/content/ui/uibackgroundblur-ingamemenu.mat",
                        color: "0 0 0 0.95",
                        imageType: UnityEngine.UI.Image.Type.Simple,
                        anchorMin: "0 0",
                        anchorMax: "1 1",
                        offsetMin: "0 0",
                        offsetMax: "0 0"
                        /* name: "CloseArea" */);
                    {
                        CUI.Element aphqoI = HQIaYh.AddPanel(
                            color: "0.0754717 0.0754717 0.0754717 1",
                            imageType: UnityEngine.UI.Image.Type.Simple,
                            anchorMin: "0.5 0.5",
                            anchorMax: "0.5 0.5",
                            offsetMin: "-603.616 -178",
                            offsetMax: "-294.984 222"
                            /* name: "Panel" */);
                        {
                            CUI.Element sbSfZG = aphqoI.AddText(
                                text: "ЦЕНЫ ЗА КОНТЕЙНЕРЫ",
                                color: "0.8862746 0.8588236 0.8274511 1",
                                font: CUI.Font.RobotoCondensedBold,
                                fontSize: 24,
                                align: TextAnchor.LowerLeft,
                                overflow: VerticalWrapMode.Overflow,
                                anchorMin: "0 1",
                                anchorMax: "1 1",
                                offsetMin: "0 5",
                                offsetMax: "0 35"
                                /* name: "Text" */);
                            {
                                CUI.Element mkxzsU = sbSfZG.AddButton(
                                    command: "cargonpc admin-settings ContainerProperties add-item",
                                    color: "0.1903702 0.5849056 0.314993 1",
                                    material: "assets/icons/iconmaterial.mat",
                                    imageType: UnityEngine.UI.Image.Type.Simple,
                                    anchorMin: "1 0.5",
                                    anchorMax: "1 0.5",
                                    offsetMin: "-30 -15",
                                    offsetMax: "0 15"
                                    /* name: "Button" */);
                                mkxzsU.AddPanel(
                                    sprite: "assets/icons/add.png",
                                    material: "assets/icons/iconmaterial.mat",
                                    color: "0.8962264 0.8962264 0.8962264 1",
                                    imageType: UnityEngine.UI.Image.Type.Simple,
                                    anchorMin: "0 0",
                                    anchorMax: "1 1",
                                    offsetMin: "5 5",
                                    offsetMax: "-5 -5"
                                    /* name: "Panel" */);
                            }
                        }
                        {
                            CUI.Element MPSllF = aphqoI.AddPanel(
                                color: "1 1 1 0",
                                imageType: UnityEngine.UI.Image.Type.Simple,
                                anchorMin: "0 0",
                                anchorMax: "1 1",
                                offsetMin: "0 0",
                                offsetMax: "0 0"
                                /* name: "Content" */);

                            int i = 0;
                            foreach (var properties in databaseConfig.ContainerProperties)
                            {
                                float price = properties.Price;
                                int slots = properties.StartCapacity;
                                {
                                    CUI.Element qzOiBx = MPSllF.AddButton(
                                        command: $"cargonpc popup ContainerProperties {i}",
                                        color: "0.1698113 0.1698113 0.1698113 1",
                                        imageType: UnityEngine.UI.Image.Type.Simple,
                                        anchorMin: "0 1",
                                        anchorMax: "1 1",
                                        offsetMin: $"18 -{10 + (i + 1) * 40 + i * 5}",
                                        offsetMax: $"-50 -{10 + i * 40 + i * 5}"
                                        /* name: "Item" */);
                                    var NIjuPC_index = $"#{i + 1}";
                                    qzOiBx.AddText(
                                        text: NIjuPC_index,
                                        color: "0.8862746 0.8588236 0.8274511 1",
                                        font: CUI.Font.RobotoCondensedRegular,
                                        fontSize: 16,
                                        align: TextAnchor.MiddleLeft,
                                        overflow: VerticalWrapMode.Overflow,
                                        anchorMin: "0 0",
                                        anchorMax: "0.6 1",
                                        offsetMin: "10 4",
                                        offsetMax: "0 -4"
                                        /* name: "Text" */);
                                    {
                                        CUI.Element ehLccf = qzOiBx.AddButton(
                                            command: $"cargonpc admin-settings ContainerProperties remove-item {i}",
                                            color: "0.5843138 0.2224132 0.1921569 1",
                                            material: "assets/icons/iconmaterial.mat",
                                            imageType: UnityEngine.UI.Image.Type.Simple,
                                            anchorMin: "1 0.5",
                                            anchorMax: "1 0.5",
                                            offsetMin: "5 -15",
                                            offsetMax: "35 15"
                                            /* name: "Button" */);
                                        ehLccf.AddPanel(
                                            sprite: "assets/icons/subtract.png",
                                            material: "assets/icons/iconmaterial.mat",
                                            color: "0.8980392 0.8980392 0.8980392 1",
                                            imageType: UnityEngine.UI.Image.Type.Simple,
                                            anchorMin: "0 0",
                                            anchorMax: "1 1",
                                            offsetMin: "5 5",
                                            offsetMax: "-5 -5"
                                            /* name: "Panel" */);
                                    }
                                }
                                i++;
                            }

                            MPSllF.Components.AddScrollView(
                                    vertical: true,
                                    scrollSensitivity: 30,
                                    anchorMin: "0 1",
                                    anchorMax: "1 1",
                                    offsetMin: $"0 -{Mathf.Max(400, 20 + i * 45)}");

                        }
                    }
                }
                root.Render(connection);
            }

            public void ContainerProperties(int index)
            {
                CUI.Root root = new CUI.Root("ContainerPricesPopup");
                {
                    CUI.Element wXWHCY = root.AddPanel(
                        color: "0.0754717 0.0754717 0.0754717 1",
                        imageType: UnityEngine.UI.Image.Type.Simple,
                        anchorMin: "0.5 0.5",
                        anchorMax: "0.5 0.5",
                        offsetMin: "-244.607 -178",
                        offsetMax: "370.007 222",
                        name: "ContainerProperties").AddDestroySelfAttribute();
                    var EoasRl_text = string.Format("КОНТЕЙНЕР #{0}", index + 1 ); ;
                    wXWHCY.AddText(
                        text: EoasRl_text,
                        color: "0.8862746 0.8588236 0.8274511 1",
                        font: CUI.Font.RobotoCondensedBold,
                        fontSize: 24,
                        align: TextAnchor.LowerLeft,
                        overflow: VerticalWrapMode.Overflow,
                        anchorMin: "0 1",
                        anchorMax: "1 1",
                        offsetMin: "0 5",
                        offsetMax: "0 35"
                        /* name: "Text" */);
                    {
                        CUI.Element yPtlxi = wXWHCY.AddContainer(
                            anchorMin: "0 1",
                            anchorMax: "1 1",
                            offsetMin: "10 -53.1",
                            offsetMax: "-10 -13.1"
                            /* name: "Property" */);
                        yPtlxi.AddText(
                            text: "Цена покупки контейнера",
                            color: "0.8862746 0.8588236 0.8274511 1",
                            font: CUI.Font.RobotoCondensedRegular,
                            fontSize: 16,
                            align: TextAnchor.MiddleLeft,
                            overflow: VerticalWrapMode.Overflow,
                            anchorMin: "0 0",
                            anchorMax: "0.6 1",
                            offsetMin: "10 4",
                            offsetMax: "0 -4"
                            /* name: "Text" */);
                        {
                            CUI.Element YmzBtW = yPtlxi.AddPanel(
                                material: "assets/icons/iconmaterial.mat",
                                color: "0.1415094 0.1395069 0.1395069 1",
                                imageType: UnityEngine.UI.Image.Type.Simple,
                                anchorMin: "0.6 0",
                                anchorMax: "1 1",
                                offsetMin: "10 0",
                                offsetMax: "0 0"
                                /* name: "Panel" */);
                            var itgJDe_value = databaseConfig.ContainerProperties[index].Price.ToString();
                            YmzBtW.AddInputfield(
                                command: $"cargonpc admin-settings ContainerProperties set-price {index}",
                                text: itgJDe_value,
                                color: "0.7264151 0.7264151 0.7264151 1",
                                font: CUI.Font.RobotoCondensedRegular,
                                fontSize: 18,
                                align: TextAnchor.MiddleCenter,
                                lineType: InputField.LineType.SingleLine,
                                charsLimit: 0,
                                anchorMin: "0 0",
                                anchorMax: "1 1",
                                offsetMin: "10 4",
                                offsetMax: "-10 -4"
                                /* name: "Inputfield" */);
                        }
                    }
                    {
                        CUI.Element jCEFlO = wXWHCY.AddContainer(
                            anchorMin: "0 1",
                            anchorMax: "1 1",
                            offsetMin: "10 -101.5",
                            offsetMax: "-10 -61.5"
                            /* name: "Property (1)" */);
                        jCEFlO.AddText(
                            text: "Изначальное количество слотов",
                            color: "0.8862746 0.8588236 0.8274511 1",
                            font: CUI.Font.RobotoCondensedRegular,
                            fontSize: 16,
                            align: TextAnchor.MiddleLeft,
                            overflow: VerticalWrapMode.Overflow,
                            anchorMin: "0 0",
                            anchorMax: "0.6 1",
                            offsetMin: "10 4",
                            offsetMax: "0 -4"
                            /* name: "Text" */);
                        {
                            CUI.Element oRQVkf = jCEFlO.AddPanel(
                                material: "assets/icons/iconmaterial.mat",
                                color: "0.1415094 0.1395069 0.1395069 1",
                                imageType: UnityEngine.UI.Image.Type.Simple,
                                anchorMin: "0.6 0",
                                anchorMax: "1 1",
                                offsetMin: "10 0",
                                offsetMax: "0 0"
                                /* name: "Panel" */);
                            var bfqEaV_value = databaseConfig.ContainerProperties[index].StartCapacity.ToString();
                            oRQVkf.AddInputfield(
                                command: $"cargonpc admin-settings ContainerProperties set-capacity {index} start",
                                text: bfqEaV_value,
                                color: "0.7264151 0.7264151 0.7264151 1",
                                font: CUI.Font.RobotoCondensedRegular,
                                fontSize: 18,
                                align: TextAnchor.MiddleCenter,
                                lineType: InputField.LineType.SingleLine,
                                charsLimit: 0,
                                anchorMin: "0 0",
                                anchorMax: "1 1",
                                offsetMin: "10 4",
                                offsetMax: "-10 -4"
                                /* name: "Inputfield" */);
                        }
                    }
                    {
                        CUI.Element eheims = wXWHCY.AddContainer(
                            anchorMin: "0 1",
                            anchorMax: "1 1",
                            offsetMin: "10 -149.9",
                            offsetMax: "-10 -109.9"
                            /* name: "Property (2)" */);
                        eheims.AddText(
                            text: "Максимальное количество слотов",
                            color: "0.8862746 0.8588236 0.8274511 1",
                            font: CUI.Font.RobotoCondensedRegular,
                            fontSize: 16,
                            align: TextAnchor.MiddleLeft,
                            overflow: VerticalWrapMode.Overflow,
                            anchorMin: "0 0",
                            anchorMax: "0.6 1",
                            offsetMin: "10 4",
                            offsetMax: "0 -4"
                            /* name: "Text" */);
                        {
                            CUI.Element nkFRlb = eheims.AddPanel(
                                material: "assets/icons/iconmaterial.mat",
                                color: "0.1415094 0.1395069 0.1395069 1",
                                imageType: UnityEngine.UI.Image.Type.Simple,
                                anchorMin: "0.6 0",
                                anchorMax: "1 1",
                                offsetMin: "10 0",
                                offsetMax: "0 0"
                                /* name: "Panel" */);
                            var yaLxFB_value = databaseConfig.ContainerProperties[index].MaxCapacity.ToString();
                            nkFRlb.AddInputfield(
                                command: $"cargonpc admin-settings ContainerProperties set-capacity {index} max",
                                text: yaLxFB_value,
                                color: "0.7264151 0.7264151 0.7264151 1",
                                font: CUI.Font.RobotoCondensedRegular,
                                fontSize: 18,
                                align: TextAnchor.MiddleCenter,
                                lineType: InputField.LineType.SingleLine,
                                charsLimit: 0,
                                anchorMin: "0 0",
                                anchorMax: "1 1",
                                offsetMin: "10 4",
                                offsetMax: "-10 -4"
                                /* name: "Inputfield" */);
                        }
                    }
                    {
                        CUI.Element rRYPEx = wXWHCY.AddContainer(
                            anchorMin: "0 1",
                            anchorMax: "1 1",
                            offsetMin: "10 -199.2",
                            offsetMax: "-10 -159.2"
                            /* name: "Property (3)" */);
                        rRYPEx.AddText(
                            text: "Цена за дополнительный слот",
                            color: "0.8862746 0.8588236 0.8274511 1",
                            font: CUI.Font.RobotoCondensedRegular,
                            fontSize: 16,
                            align: TextAnchor.MiddleLeft,
                            overflow: VerticalWrapMode.Overflow,
                            anchorMin: "0 0",
                            anchorMax: "0.6 1",
                            offsetMin: "10 4",
                            offsetMax: "0 -4"
                            /* name: "Text" */);
                        {
                            CUI.Element EcRDxM = rRYPEx.AddPanel(
                                material: "assets/icons/iconmaterial.mat",
                                color: "0.1415094 0.1395069 0.1395069 1",
                                imageType: UnityEngine.UI.Image.Type.Simple,
                                anchorMin: "0.6 0",
                                anchorMax: "1 1",
                                offsetMin: "10 0",
                                offsetMax: "0 0"
                                /* name: "Panel" */);
                            var OWEEqg_value = databaseConfig.ContainerProperties[index].SlotPrice.ToString();
                            EcRDxM.AddInputfield(
                                command: $"cargonpc admin-settings ContainerProperties set-slot-price {index}",
                                text: OWEEqg_value,
                                color: "0.7264151 0.7264151 0.7264151 1",
                                font: CUI.Font.RobotoCondensedRegular,
                                fontSize: 18,
                                align: TextAnchor.MiddleCenter,
                                lineType: InputField.LineType.SingleLine,
                                charsLimit: 0,
                                anchorMin: "0 0",
                                anchorMax: "1 1",
                                offsetMin: "10 4",
                                offsetMax: "-10 -4"
                                /* name: "Inputfield" */);
                        }
                    }
                }
                root.Render(connection);
            }



            public void DeliveryPricePerItemPopup()
            {
                CUI.Root root = new CUI.Root("CargoUI_Body");
                {
                    CUI.Element xNDGPE = root.AddContainer(
                        anchorMin: "0 0",
                        anchorMax: "1 1",
                        offsetMin: "0 0",
                        offsetMax: "0 0",
                        name: "DeliveryPricePerItemPopup").AddDestroySelfAttribute();
                    xNDGPE.AddButton(
                        command: null,
                        close: "DeliveryPricePerItemPopup",
                        color: "0 0 0 0.9647059",
                        imageType: UnityEngine.UI.Image.Type.Simple,
                        anchorMin: "0 0",
                        anchorMax: "1 1",
                        offsetMin: "0 0",
                        offsetMax: "0 0"
                        /* name: "CloseArea" */);
                    {
                        CUI.Element JdOZcD = xNDGPE.AddPanel(
                            color: "0.0754717 0.0754717 0.0754717 1",
                            imageType: UnityEngine.UI.Image.Type.Simple,
                            anchorMin: "0.5 0.5",
                            anchorMax: "0.5 0.5",
                            offsetMin: "-300 -200",
                            offsetMax: "300 200"
                            /* name: "Panel" */);
                        {
                            CUI.Element VClArn = JdOZcD.AddText(
                                text: ЦЕНА_ЗА_ОТПРАВКУ_ПРЕДМЕТА,
                                color: "0.8862746 0.8588236 0.8274511 1",
                                font: CUI.Font.RobotoCondensedBold,
                                fontSize: 24,
                                align: TextAnchor.LowerLeft,
                                overflow: VerticalWrapMode.Overflow,
                                anchorMin: "0 1",
                                anchorMax: "1 1",
                                offsetMin: "0 5",
                                offsetMax: "0 35"
                                /* name: "Text" */);
                            {
                                CUI.Element XJjMyw = VClArn.AddButton(
                                    command: "cargonpc admin-settings DeliveryPricePerItem add-item",
                                    color: "0.1903702 0.5849056 0.314993 1",
                                    material: "assets/icons/iconmaterial.mat",
                                    imageType: UnityEngine.UI.Image.Type.Simple,
                                    anchorMin: "1 0.5",
                                    anchorMax: "1 0.5",
                                    offsetMin: "-30 -15",
                                    offsetMax: "0 15"
                                    /* name: "Button" */);
                                XJjMyw.AddPanel(
                                    sprite: "assets/icons/add.png",
                                    material: "assets/icons/iconmaterial.mat",
                                    color: "0.8962264 0.8962264 0.8962264 1",
                                    imageType: UnityEngine.UI.Image.Type.Simple,
                                    anchorMin: "0 0",
                                    anchorMax: "1 1",
                                    offsetMin: "5 5",
                                    offsetMax: "-5 -5"
                                    /* name: "Panel" */);
                            }
                        }
                        {
                            CUI.Element XfgDLd = JdOZcD.AddPanel(
                                color: "1 1 1 0",
                                imageType: UnityEngine.UI.Image.Type.Simple,
                                anchorMin: "0 0",
                                anchorMax: "1 1",
                                offsetMin: "0 0",
                                offsetMax: "0 0"
                                /* name: "Content" */);
                            {

                                Translator15 translator = PluginInstance.translators.GetOrCreate(this.UserId);

                                int i = 0;
                                foreach (var pair in databaseConfig.DeliveryPricePerItem)
                                {
                                    ItemDefinition itemDefinition = ItemManager.FindItemDefinition(pair.Key);
                                    if (itemDefinition == null)
                                        return;

                                    CUI.Element MkLiUW = XfgDLd.AddContainer(
                                        anchorMin: "0 1",
                                        anchorMax: "1 1",
                                        offsetMin: $"18 -{10 + (i + 1) * 40}",
                                        offsetMax: $"-50 -{10 + i * 40}"
                                        /* name: "Item" */);
                                    MkLiUW.AddText(
                                        text: translator.Convert(itemDefinition.displayName).translated,
                                        color: "0.8862746 0.8588236 0.8274511 1",
                                        font: CUI.Font.RobotoCondensedRegular,
                                        fontSize: 16,
                                        align: TextAnchor.MiddleLeft,
                                        overflow: VerticalWrapMode.Overflow,
                                        anchorMin: "0 0",
                                        anchorMax: "0.6 1",
                                        offsetMin: "10 4",
                                        offsetMax: "0 -4"
                                        /* name: "Text" */);
                                    {
                                        CUI.Element IGiQjn = MkLiUW.AddPanel(
                                            material: "assets/icons/iconmaterial.mat",
                                            color: "0.1415094 0.1395069 0.1395069 1",
                                            imageType: UnityEngine.UI.Image.Type.Simple,
                                            anchorMin: "0.6 0",
                                            anchorMax: "1 1",
                                            offsetMin: "10 0",
                                            offsetMax: "0 0"
                                            /* name: "Panel" */);

                                        IGiQjn.AddInputfield(
                                            command: $"cargonpc admin-settings DeliveryPricePerItem set {pair.Key}",
                                            text: pair.Value.ToString(),
                                            color: "0.7264151 0.7264151 0.7264151 1",
                                            font: CUI.Font.RobotoCondensedRegular,
                                            fontSize: 18,
                                            align: TextAnchor.MiddleCenter,
                                            lineType: InputField.LineType.SingleLine,
                                            charsLimit: 0,
                                            anchorMin: "0 0",
                                            anchorMax: "1 1",
                                            offsetMin: "10 4",
                                            offsetMax: "-10 -4"
                                            /* name: "Inputfield" */);
                                    }
                                    {
                                        CUI.Element SgMoBM = MkLiUW.AddButton(
                                            command: $"cargonpc admin-settings DeliveryPricePerItem remove-item {pair.Key}",
                                            color: "0.5843138 0.2224132 0.1921569 1",
                                            material: "assets/icons/iconmaterial.mat",
                                            imageType: UnityEngine.UI.Image.Type.Simple,
                                            anchorMin: "1 0.5",
                                            anchorMax: "1 0.5",
                                            offsetMin: "5 -15",
                                            offsetMax: "35 15"
                                            /* name: "Button" */);
                                        SgMoBM.AddPanel(
                                            sprite: "assets/icons/subtract.png",
                                            material: "assets/icons/iconmaterial.mat",
                                            color: "0.8980392 0.8980392 0.8980392 1",
                                            imageType: UnityEngine.UI.Image.Type.Simple,
                                            anchorMin: "0 0",
                                            anchorMax: "1 1",
                                            offsetMin: "5 5",
                                            offsetMax: "-5 -5"
                                            /* name: "Panel" */);
                                    }
                                    i++;
                                }

                                XfgDLd.Components.AddScrollView(
                                    vertical: true,
                                    scrollSensitivity: 30,
                                    anchorMin: "0 1",
                                    anchorMax: "1 1",
                                    offsetMin: $"0 -{Mathf.Max(400, 20 + i * 40)}");
                            }
                        }
                    }
                }
                root.Render(connection);
            }

            public void DeliveryPricePerCategoryPopup()
            {
                CUI.Root root = new CUI.Root("CargoUI_Body");
                {
                    CUI.Element WTZTei = root.AddContainer(
                        anchorMin: "0 0",
                        anchorMax: "1 1",
                        offsetMin: "0 0",
                        offsetMax: "0 0",
                        name: "DeliveryPricePerCategory").AddDestroySelfAttribute();
                    WTZTei.AddButton(
                        command: null,
                        close: "DeliveryPricePerCategory",
                        color: "0 0 0 0.9647059",
                        imageType: UnityEngine.UI.Image.Type.Simple,
                        anchorMin: "0 0",
                        anchorMax: "1 1",
                        offsetMin: "0 0",
                        offsetMax: "0 0"
                        /* name: "CloseArea" */);
                    {
                        CUI.Element HXJWMZ = WTZTei.AddPanel(
                            color: "0.0754717 0.0754717 0.0754717 1",
                            imageType: UnityEngine.UI.Image.Type.Simple,
                            anchorMin: "0.5 0.5",
                            anchorMax: "0.5 0.5",
                            offsetMin: "-300 -200",
                            offsetMax: "300 200"
                            /* name: "Panel" */);
                        HXJWMZ.AddText(
                            text: ЦЕНА_ЗА_ОТПРАВКУ_ПРЕДМЕТА_ИЗ_КАТЕГОРИИ,
                            color: "0.8862746 0.8588236 0.8274511 1",
                            font: CUI.Font.RobotoCondensedBold,
                            fontSize: 24,
                            align: TextAnchor.LowerLeft,
                            overflow: VerticalWrapMode.Overflow,
                            anchorMin: "0 1",
                            anchorMax: "1 1",
                            offsetMin: "0 5",
                            offsetMax: "0 35"
                            /* name: "Text" */);
                        {
                            CUI.Element JlkkNr = HXJWMZ.AddPanel(
                                color: "1 1 1 0",
                                imageType: UnityEngine.UI.Image.Type.Simple,
                                anchorMin: "0 0",
                                anchorMax: "1 1",
                                offsetMin: "0 0",
                                offsetMax: "0 0"
                                /* name: "Content" */);
                            {

                                int i = 0;

                                foreach (var pair in databaseConfig.DeliveryPricePerCategory)
                                {
                                    CUI.Element QvRjxA = JlkkNr.AddContainer(
                                   anchorMin: "0 1",
                                   anchorMax: "1 1",
                                   offsetMin: $"28 -{10 + (i + 1) * 40}",
                                   offsetMax: $"-40 -{10 + i * 40}"
                                   /* name: "Item" */);
                                    QvRjxA.AddText(
                                        text: pair.Key.ToString(),
                                        color: "0.8862746 0.8588236 0.8274511 1",
                                        font: CUI.Font.RobotoCondensedRegular,
                                        fontSize: 16,
                                        align: TextAnchor.MiddleLeft,
                                        overflow: VerticalWrapMode.Overflow,
                                        anchorMin: "0 0",
                                        anchorMax: "0.6 1",
                                        offsetMin: "10 4",
                                        offsetMax: "0 -4"
                                        /* name: "Text" */);
                                    {
                                        CUI.Element sQMhyU = QvRjxA.AddPanel(
                                            material: "assets/icons/iconmaterial.mat",
                                            color: "0.1415094 0.1395069 0.1395069 1",
                                            imageType: UnityEngine.UI.Image.Type.Simple,
                                            anchorMin: "0.6 0",
                                            anchorMax: "1 1",
                                            offsetMin: "10 0",
                                            offsetMax: "0 0"
                                            /* name: "Panel" */);
                                        sQMhyU.AddInputfield(
                                            command: $"cargonpc admin-settings DeliveryPricePerCategory {(int)pair.Key}",
                                            text: pair.Value.ToString(),
                                            color: "0.7264151 0.7264151 0.7264151 1",
                                            font: CUI.Font.RobotoCondensedRegular,
                                            fontSize: 18,
                                            align: TextAnchor.MiddleCenter,
                                            lineType: InputField.LineType.SingleLine,
                                            charsLimit: 0,
                                            anchorMin: "0 0",
                                            anchorMax: "1 1",
                                            offsetMin: "10 4",
                                            offsetMax: "-10 -4"
                                            /* name: "Inputfield" */);
                                    }
                                    i++;
                                }

                                JlkkNr.Components.AddScrollView(
                                    vertical: true,
                                    scrollSensitivity: 30,
                                    anchorMin: "0 1",
                                    anchorMax: "1 1",
                                    offsetMin: $"0 -{Mathf.Max(400, 20 + i * 40)}");
                            }
                        }
                    }
                }
                root.Render(connection);
            }

            // UI редактирования предметов НПС
            public void NpcItemsPopup()
            {
                Configuration.NPC npcConfig = config.Npc.ElementAtOrDefault(NpcIndex);
                if (npcConfig == null)
                    return;

                CUI.Root root = new CUI.Root("CargoUI_Body");
                {
                    CUI.Element steKZa = root.AddContainer(
                        anchorMin: "0 0",
                        anchorMax: "1 1",
                        offsetMin: "0 0",
                        offsetMax: "0 0",
                        name: "NpcItemsPopup").AddDestroySelfAttribute();
                    steKZa.AddButton(
                        command: null,
                        close: "NpcItemsPopup",
                        color: "0 0 0 0.9647059",
                        imageType: UnityEngine.UI.Image.Type.Simple,
                        anchorMin: "0 0",
                        anchorMax: "1 1",
                        offsetMin: "0 0",
                        offsetMax: "0 0"
                        /* name: "CloseArea" */);
                    {
                        CUI.Element KWfJNq = steKZa.AddPanel(
                            color: "0.0754717 0.0754717 0.0754717 1",
                            imageType: UnityEngine.UI.Image.Type.Simple,
                            anchorMin: "0.5 0.5",
                            anchorMax: "0.5 0.5",
                            offsetMin: "-300 -200",
                            offsetMax: "300 200"
                            /* name: "Panel" */);
                        {
                            CUI.Element EKRNWy = KWfJNq.AddText(
                                text: ПРЕДМЕТЫ_НПС,
                                color: "0.8862746 0.8588236 0.8274511 1",
                                font: CUI.Font.RobotoCondensedBold,
                                fontSize: 24,
                                align: TextAnchor.LowerLeft,
                                overflow: VerticalWrapMode.Overflow,
                                anchorMin: "0 1",
                                anchorMax: "1 1",
                                offsetMin: "0 5",
                                offsetMax: "0 35"
                                /* name: "Text" */);
                            {
                                CUI.Element OoWUsn = EKRNWy.AddButton(
                                    command: "cargonpc admin-settings NpcItems add-item",
                                    color: "0.1903702 0.5849056 0.314993 1",
                                    material: "assets/icons/iconmaterial.mat",
                                    imageType: UnityEngine.UI.Image.Type.Simple,
                                    anchorMin: "1 0.5",
                                    anchorMax: "1 0.5",
                                    offsetMin: "-30 -15",
                                    offsetMax: "0 15"
                                    /* name: "Button" */);
                                OoWUsn.AddPanel(
                                    sprite: "assets/icons/add.png",
                                    material: "assets/icons/iconmaterial.mat",
                                    color: "0.8962264 0.8962264 0.8962264 1",
                                    imageType: UnityEngine.UI.Image.Type.Simple,
                                    anchorMin: "0 0",
                                    anchorMax: "1 1",
                                    offsetMin: "5 5",
                                    offsetMax: "-5 -5"
                                    /* name: "Panel" */);
                            }
                        }
                        {
                            CUI.Element yokWNF = KWfJNq.AddPanel(
                                color: "1 1 1 0",
                                imageType: UnityEngine.UI.Image.Type.Simple,
                                anchorMin: "0 0",
                                anchorMax: "1 1",
                                offsetMin: "0 0",
                                offsetMax: "0 0"
                                /* name: "Content" */);
                            {

                                int i = 0;

                                Translator15 translator = PluginInstance.translators.GetOrCreate(this.UserId);

                                foreach (var pair in npcConfig.Items)
                                {
                                    int itemId = pair.Key;
                                    ulong skinId = pair.Value;
                                    ItemDefinition itemDefinition = ItemManager.FindItemDefinition(itemId);
                                    if (itemDefinition == null)
                                        return;

                                    CUI.Element MkLiUW = yokWNF.AddContainer(
                                        anchorMin: "0 1",
                                        anchorMax: "1 1",
                                        offsetMin: $"18 -{10 + (i + 1) * 40}",
                                        offsetMax: $"-50 -{10 + i * 40}"
                                        /* name: "Item" */);
                                    MkLiUW.AddText(
                                        text: translator.Convert(itemDefinition.displayName).translated,
                                        color: "0.8862746 0.8588236 0.8274511 1",
                                        font: CUI.Font.RobotoCondensedRegular,
                                        fontSize: 16,
                                        align: TextAnchor.MiddleLeft,
                                        overflow: VerticalWrapMode.Overflow,
                                        anchorMin: "0 0",
                                        anchorMax: "0.6 1",
                                        offsetMin: "10 4",
                                        offsetMax: "0 -4"
                                        /* name: "Text" */);
                                    {
                                        CUI.Element IGiQjn = MkLiUW.AddPanel(
                                            material: "assets/icons/iconmaterial.mat",
                                            color: "0.1415094 0.1395069 0.1395069 1",
                                            imageType: UnityEngine.UI.Image.Type.Simple,
                                            anchorMin: "0.6 0",
                                            anchorMax: "1 1",
                                            offsetMin: "10 0",
                                            offsetMax: "0 0"
                                            /* name: "Panel" */);

                                        IGiQjn.AddInputfield(
                                            command: $"cargonpc admin-settings NpcItems set {itemId}",
                                            text: skinId.ToString(),
                                            color: "0.7264151 0.7264151 0.7264151 1",
                                            font: CUI.Font.RobotoCondensedRegular,
                                            fontSize: 18,
                                            align: TextAnchor.MiddleCenter,
                                            lineType: InputField.LineType.SingleLine,
                                            charsLimit: 0,
                                            anchorMin: "0 0",
                                            anchorMax: "1 1",
                                            offsetMin: "10 4",
                                            offsetMax: "-10 -4"
                                            /* name: "Inputfield" */);
                                    }
                                    {
                                        CUI.Element SgMoBM = MkLiUW.AddButton(
                                            command: $"cargonpc admin-settings NpcItems remove-item {itemId}",
                                            color: "0.5843138 0.2224132 0.1921569 1",
                                            material: "assets/icons/iconmaterial.mat",
                                            imageType: UnityEngine.UI.Image.Type.Simple,
                                            anchorMin: "1 0.5",
                                            anchorMax: "1 0.5",
                                            offsetMin: "5 -15",
                                            offsetMax: "35 15"
                                            /* name: "Button" */);
                                        SgMoBM.AddPanel(
                                            sprite: "assets/icons/subtract.png",
                                            material: "assets/icons/iconmaterial.mat",
                                            color: "0.8980392 0.8980392 0.8980392 1",
                                            imageType: UnityEngine.UI.Image.Type.Simple,
                                            anchorMin: "0 0",
                                            anchorMax: "1 1",
                                            offsetMin: "5 5",
                                            offsetMax: "-5 -5"
                                            /* name: "Panel" */);
                                    }
                                    i++;
                                }
                            }
                        }
                    }
                }
                root.Render(connection);
            }

            // UI редактирования стаков предметов

            public void ItemStacksPopup()
            {
                Configuration.NPC npcConfig = config.Npc.ElementAtOrDefault(NpcIndex);
                if (npcConfig == null)
                    return;

                CUI.Root root = new CUI.Root("CargoUI_Body");
                {
                    CUI.Element steKZa = root.AddContainer(
                        anchorMin: "0 0",
                        anchorMax: "1 1",
                        offsetMin: "0 0",
                        offsetMax: "0 0",
                        name: "ItemStacksPopup").AddDestroySelfAttribute();
                    steKZa.AddButton(
                        command: null,
                        close: "ItemStacksPopup",
                        color: "0 0 0 0.9647059",
                        imageType: UnityEngine.UI.Image.Type.Simple,
                        anchorMin: "0 0",
                        anchorMax: "1 1",
                        offsetMin: "0 0",
                        offsetMax: "0 0"
                        /* name: "CloseArea" */);
                    {
                        CUI.Element KWfJNq = steKZa.AddPanel(
                            color: "0.0754717 0.0754717 0.0754717 1",
                            imageType: UnityEngine.UI.Image.Type.Simple,
                            anchorMin: "0.5 0.5",
                            anchorMax: "0.5 0.5",
                            offsetMin: "-300 -200",
                            offsetMax: "300 200"
                            /* name: "Panel" */);
                        {
                            CUI.Element EKRNWy = KWfJNq.AddText(
                                text: "СТАКИ ПРЕДМЕТОВ",
                                color: "0.8862746 0.8588236 0.8274511 1",
                                font: CUI.Font.RobotoCondensedBold,
                                fontSize: 24,
                                align: TextAnchor.LowerLeft,
                                overflow: VerticalWrapMode.Overflow,
                                anchorMin: "0 1",
                                anchorMax: "1 1",
                                offsetMin: "0 5",
                                offsetMax: "0 35"
                                /* name: "Text" */);
                            {
                                CUI.Element OoWUsn = EKRNWy.AddButton(
                                    command: "cargonpc admin-settings ItemStackable add-item",
                                    color: "0.1903702 0.5849056 0.314993 1",
                                    material: "assets/icons/iconmaterial.mat",
                                    imageType: UnityEngine.UI.Image.Type.Simple,
                                    anchorMin: "1 0.5",
                                    anchorMax: "1 0.5",
                                    offsetMin: "-30 -15",
                                    offsetMax: "0 15"
                                    /* name: "Button" */);
                                OoWUsn.AddPanel(
                                    sprite: "assets/icons/add.png",
                                    material: "assets/icons/iconmaterial.mat",
                                    color: "0.8962264 0.8962264 0.8962264 1",
                                    imageType: UnityEngine.UI.Image.Type.Simple,
                                    anchorMin: "0 0",
                                    anchorMax: "1 1",
                                    offsetMin: "5 5",
                                    offsetMax: "-5 -5"
                                    /* name: "Panel" */);
                            }
                        }
                        {
                            CUI.Element yokWNF = KWfJNq.AddPanel(
                                color: "1 1 1 0",
                                imageType: UnityEngine.UI.Image.Type.Simple,
                                anchorMin: "0 0",
                                anchorMax: "1 1",
                                offsetMin: "0 0",
                                offsetMax: "0 0"
                                /* name: "Content" */);
                            {

                                int i = 0;

                                Translator15 translator = PluginInstance.translators.GetOrCreate(this.UserId);

                                foreach (var pair in databaseConfig.ItemStackable)
                                {
                                    int itemId = pair.Key;
                                    int stackable = pair.Value;
                                    ItemDefinition itemDefinition = ItemManager.FindItemDefinition(itemId);
                                    if (itemDefinition == null)
                                        return;

                                    CUI.Element MkLiUW = yokWNF.AddContainer(
                                        anchorMin: "0 1",
                                        anchorMax: "1 1",
                                        offsetMin: $"18 -{10 + (i + 1) * 40}",
                                        offsetMax: $"-50 -{10 + i * 40}"
                                        /* name: "Item" */);
                                    MkLiUW.AddText(
                                        text: translator.Convert(itemDefinition.displayName).translated,
                                        color: "0.8862746 0.8588236 0.8274511 1",
                                        font: CUI.Font.RobotoCondensedRegular,
                                        fontSize: 16,
                                        align: TextAnchor.MiddleLeft,
                                        overflow: VerticalWrapMode.Overflow,
                                        anchorMin: "0 0",
                                        anchorMax: "0.6 1",
                                        offsetMin: "10 4",
                                        offsetMax: "0 -4"
                                        /* name: "Text" */);
                                    {
                                        CUI.Element IGiQjn = MkLiUW.AddPanel(
                                            material: "assets/icons/iconmaterial.mat",
                                            color: "0.1415094 0.1395069 0.1395069 1",
                                            imageType: UnityEngine.UI.Image.Type.Simple,
                                            anchorMin: "0.6 0",
                                            anchorMax: "1 1",
                                            offsetMin: "10 0",
                                            offsetMax: "0 0"
                                            /* name: "Panel" */);

                                        IGiQjn.AddInputfield(
                                            command: $"cargonpc admin-settings ItemStackable set {itemId}",
                                            text: stackable.ToString(),
                                            color: "0.7264151 0.7264151 0.7264151 1",
                                            font: CUI.Font.RobotoCondensedRegular,
                                            fontSize: 18,
                                            align: TextAnchor.MiddleCenter,
                                            lineType: InputField.LineType.SingleLine,
                                            charsLimit: 0,
                                            anchorMin: "0 0",
                                            anchorMax: "1 1",
                                            offsetMin: "10 4",
                                            offsetMax: "-10 -4"
                                            /* name: "Inputfield" */);
                                    }
                                    {
                                        CUI.Element SgMoBM = MkLiUW.AddButton(
                                            command: $"cargonpc admin-settings ItemStackable remove-item {itemId}",
                                            color: "0.5843138 0.2224132 0.1921569 1",
                                            material: "assets/icons/iconmaterial.mat",
                                            imageType: UnityEngine.UI.Image.Type.Simple,
                                            anchorMin: "1 0.5",
                                            anchorMax: "1 0.5",
                                            offsetMin: "5 -15",
                                            offsetMax: "35 15"
                                            /* name: "Button" */);
                                        SgMoBM.AddPanel(
                                            sprite: "assets/icons/subtract.png",
                                            material: "assets/icons/iconmaterial.mat",
                                            color: "0.8980392 0.8980392 0.8980392 1",
                                            imageType: UnityEngine.UI.Image.Type.Simple,
                                            anchorMin: "0 0",
                                            anchorMax: "1 1",
                                            offsetMin: "5 5",
                                            offsetMax: "-5 -5"
                                            /* name: "Panel" */);
                                    }
                                    i++;
                                }
                            }
                        }
                    }
                }
                root.Render(connection);
            }

            // UI редактирования запрещенных предметов
            public void ItemBlacklistPopup()
            {
                CUI.Root root = new CUI.Root("CargoUI_Body");
                {
                    CUI.Element awBTuq = root.AddContainer(
                        anchorMin: "0 0",
                        anchorMax: "1 1",
                        offsetMin: "0 0",
                        offsetMax: "0 0",
                        name: "ItemBlacklistPopup").AddDestroySelfAttribute();
                    awBTuq.AddButton(
                        command: null,
                        close: "ItemBlacklistPopup",
                        color: "0 0 0 0.9647059",
                        imageType: UnityEngine.UI.Image.Type.Simple,
                        anchorMin: "0 0",
                        anchorMax: "1 1",
                        offsetMin: "0 0",
                        offsetMax: "0 0"
                        /* name: "CloseArea" */);
                    {
                        CUI.Element ujKLlG = awBTuq.AddPanel(
                            color: "0.0754717 0.0754717 0.0754717 1",
                            imageType: UnityEngine.UI.Image.Type.Simple,
                            anchorMin: "0.5 0.5",
                            anchorMax: "0.5 0.5",
                            offsetMin: "-300 -200",
                            offsetMax: "300 200"
                            /* name: "Panel" */);
                        {
                            CUI.Element WOjxgQ = ujKLlG.AddText(
                                text: ЧЕРНЫЙ_СПИСОК_ПРЕДМЕТОВ,
                                color: "0.8862746 0.8588236 0.8274511 1",
                                font: CUI.Font.RobotoCondensedBold,
                                fontSize: 24,
                                align: TextAnchor.LowerLeft,
                                overflow: VerticalWrapMode.Overflow,
                                anchorMin: "0 1",
                                anchorMax: "1 1",
                                offsetMin: "0 5",
                                offsetMax: "0 35"
                                /* name: "Text" */);
                            {
                                CUI.Element nicmya = WOjxgQ.AddButton(
                                    command: "cargonpc admin-settings ItemBlacklist add-item",
                                    color: "0.1903702 0.5849056 0.314993 1",
                                    material: "assets/icons/iconmaterial.mat",
                                    imageType: UnityEngine.UI.Image.Type.Simple,
                                    anchorMin: "1 0.5",
                                    anchorMax: "1 0.5",
                                    offsetMin: "-30 -15",
                                    offsetMax: "0 15"
                                    /* name: "Button" */);
                                nicmya.AddPanel(
                                    sprite: "assets/icons/add.png",
                                    material: "assets/icons/iconmaterial.mat",
                                    color: "0.8962264 0.8962264 0.8962264 1",
                                    imageType: UnityEngine.UI.Image.Type.Simple,
                                    anchorMin: "0 0",
                                    anchorMax: "1 1",
                                    offsetMin: "5 5",
                                    offsetMax: "-5 -5"
                                    /* name: "Panel" */);
                            }
                        }
                        {
                            CUI.Element vipkFq = ujKLlG.AddPanel(
                                color: "1 1 1 0",
                                imageType: UnityEngine.UI.Image.Type.Simple,
                                anchorMin: "0 0",
                                anchorMax: "1 1",
                                offsetMin: "0 0",
                                offsetMax: "0 0"
                                /* name: "Content" */);
                            {

                                int i = 0;
                                Translator15 translator = PluginInstance.translators.GetOrCreate(this.UserId);

                                foreach (string shortname in config.ItemBlacklist)
                                {
                                    ItemDefinition itemDefinition = ItemManager.FindItemDefinition(shortname);
                                    if (itemDefinition == null)
                                        return;

                                    CUI.Element VdAGlh = vipkFq.AddContainer(
                                      anchorMin: "0 1",
                                      anchorMax: "1 1",
                                      offsetMin: $"18 -{10 + (i + 1) * 40}",
                                      offsetMax: $"-50 -{10 + i * 40}"
                                     /* name: "Item" */);

                                    VdAGlh.AddText(
                                        text: translator.Convert(itemDefinition.displayName).translated,
                                        color: "0.8862746 0.8588236 0.8274511 1",
                                        font: CUI.Font.RobotoCondensedRegular,
                                        fontSize: 16,
                                        align: TextAnchor.MiddleLeft,
                                        overflow: VerticalWrapMode.Overflow,
                                        anchorMin: "0 0",
                                        anchorMax: "0.6 1",
                                        offsetMin: "10 4",
                                        offsetMax: "205 -4"
                                        /* name: "Text" */);
                                    {
                                        CUI.Element OFxufg = VdAGlh.AddButton(
                                            command: $"cargonpc admin-settings ItemBlacklist remove-item {shortname}",
                                            color: "0.5843138 0.2224132 0.1921569 1",
                                            material: "assets/icons/iconmaterial.mat",
                                            imageType: UnityEngine.UI.Image.Type.Simple,
                                            anchorMin: "1 0.5",
                                            anchorMax: "1 0.5",
                                            offsetMin: "5 -15",
                                            offsetMax: "35 15"
                                            /* name: "Button" */);
                                        OFxufg.AddPanel(
                                            sprite: "assets/icons/subtract.png",
                                            material: "assets/icons/iconmaterial.mat",
                                            color: "0.8980392 0.8980392 0.8980392 1",
                                            imageType: UnityEngine.UI.Image.Type.Simple,
                                            anchorMin: "0 0",
                                            anchorMax: "1 1",
                                            offsetMin: "5 5",
                                            offsetMax: "-5 -5"
                                            /* name: "Panel" */);
                                    }
                                    i++;
                                }

                                vipkFq.Components.AddScrollView(
                                    vertical: true,
                                    scrollSensitivity: 30,
                                    anchorMin: "0 1",
                                    anchorMax: "1 1",
                                    offsetMin: $"0 -{Mathf.Max(400, 20 + i * 40)}");

                            }
                        }
                    }
                }
                root.Render(connection);
            }

            // Обновление селектора предметов
            public void UpdateItemSelector()
            {
                CUI.Root root = new CUI.Root();
                root.Name = "ItemSelectorCategories";
                ItemSelectorCategories(root, out _);
                root.Name = "Item Selector Panel";
                ItemSelectorBody(root);
                root.Render(connection);
            }

            // Вызвать селектор предметов
            public void ItemSelector()
            {
                CUI.Root root = new CUI.Root("Cargo UI");
                {
                    CUI.Element nxrMiq = root.AddPanel(
                        material: "assets/content/ui/uibackgroundblur-ingamemenu.mat",
                        color: "0 0 0 0.6745098",
                        imageType: UnityEngine.UI.Image.Type.Simple,
                        anchorMin: "0 0",
                        anchorMax: "1 1",
                        offsetMin: "0 0",
                        offsetMax: "0 0",
                        name: "Item Selector").AddDestroySelfAttribute();

                    CUI.Element yfKtue = nxrMiq.AddPanel(
                            color: "0.09433961 0.09433961 0.09433961 1",
                            imageType: UnityEngine.UI.Image.Type.Simple,
                            anchorMin: "0.5 0.5",
                            anchorMax: "0.5 0.5",
                            offsetMin: "-450 -263",
                            offsetMax: "450 237",
                            name: "Item Selector Panel");
                    {


                        CUI.Element VFIPDq = yfKtue.AddPanel(
                            color: "1 1 1 0",
                            imageType: UnityEngine.UI.Image.Type.Simple,
                            anchorMin: "0 1",
                            anchorMax: "0 1",
                            offsetMin: "0 -40",
                            offsetMax: "900 0",
                            name: "ItemSelectorCategories");

                        ItemSelectorCategories(VFIPDq, out int count);

                        VFIPDq.Components.AddScrollView(
                            horizontal: true,
                            scrollSensitivity: -50f,
                            anchorMin: "0 0",
                            anchorMax: "0 1",
                            offsetMax: $"{Mathf.Max(900, count * 130)} 0");
                    }

                    ItemSelectorBody(yfKtue);
                }
                root.Render(connection);
            }

            // Часть с категориями
            private void ItemSelectorCategories(CUI.Element root, out int count)
            {
                var container = root.AddContainer(name: "ItemSelectorCategoriesContainer").AddDestroySelfAttribute();

                ItemCategory selectedCategory = this.selectorCategory;
                const string selectedCategoryColor = "0 0 0 1";

                int i = 0;
                foreach (ItemCategory category in ValidCategories)
                {
                    string color;
                    if (category == selectedCategory)
                        color = selectedCategoryColor;
                    else
                        color = $"0.05 0.05 0.05 {(i % 2 == 0 ? "0.5" : "0.75")}";

                    container.AddButton(
                       command: $"cargonpc item-selector select-category {(int)category}",
                       color: color,
                       imageType: UnityEngine.UI.Image.Type.Simple,
                       anchorMin: "0 0",
                       anchorMax: "0 1",
                       offsetMin: $"{i * 130} 0",
                       offsetMax: $"{(i + 1) * 130} 0"
                       /* name: "Button" */)
                    .AddText(
                        text: category.ToString(),
                        font: CUI.Font.RobotoMonoRegular,
                        align: TextAnchor.MiddleCenter,
                        offsetMin: "10 5",
                        offsetMax: "-10 -5");
                    i++;
                }
                count = i;
            }

            // Тело селектора
            private void ItemSelectorBody(CUI.Element root)
            {
                ItemCategory selectedCategory = this.selectorCategory;
                {
                    CUI.Element hHJGJX = root.AddPanel(
                            color: "1 1 1 0",
                            imageType: UnityEngine.UI.Image.Type.Simple,
                            anchorMin: "0.5 0.5",
                            anchorMax: "0.5 0.5",
                            offsetMin: "-450 -250",
                            offsetMax: "450 200",
                            name: "ItemSelectorBody").AddDestroySelfAttribute();



                    int row = 0;
                    int column = 0;
                    foreach (ItemDefinition itemDefinition in ItemManager.itemList)
                    {
                        if (itemDefinition.category != selectedCategory || itemDefinition.hidden)
                            continue;

                        hHJGJX.AddButton(
                            command: $"cargonpc item-selector select-item {itemDefinition.itemid}",
                            close: "Item Selector",
                            sprite: "assets/content/ui/ui.box.dotted.tga",
                            color: "0.254717 0.254717 0.254717 1",
                            imageType: UnityEngine.UI.Image.Type.Simple,
                            anchorMin: "0 1",
                            anchorMax: "0 1",
                            offsetMin: $"{column * 90} -{(row + 1) * 90}",
                            offsetMax: $"{(column + 1) * 90} -{row * 90}"
                            /* name: "Panel" */)
                            .AddIcon(
                                itemId: itemDefinition.itemid,
                                offsetMin: "10 10",
                                offsetMax: "-10 -10");

                        column++;

                        if (column == 10)
                        {
                            row++;
                            column = 0;
                        }
                    }

                    hHJGJX.Components.AddScrollView(
                        vertical: true,
                        scrollSensitivity: 20,
                        anchorMin: "0 1",
                        anchorMax: "1 1",
                        offsetMin: $"0 -{Mathf.Max(450, row * 90)}");
                }
            }

            public void OrderMapSelector(string parent, string continueButtonText, string continueButtonCommand)
            {
                Server server = this.DestinationServer;
                if (server == null)
                    return;
                MapImage mapImage = server.Map;
                if (mapImage == null || mapImage.crc == 0)
                    return;
                if (server.SpawnPoints == null)
                    return;

                CUI.Root root = new CUI.Root(parent);
                {
                    CUI.Element IweOdH = root.AddPanel(
                        material: "assets/content/ui/uibackgroundblur.mat",
                        color: "0 0 0 0.09803922",
                        imageType: UnityEngine.UI.Image.Type.Simple,
                        anchorMin: "0 0",
                        anchorMax: "1 1",
                        offsetMin: "0 0",
                        offsetMax: "0 0",
                        name: "Order Map Selector").AddDestroySelfAttribute();
                    {
                        CUI.Element lrEOGK = IweOdH.AddPanel(
                            material: "assets/icons/iconmaterial.mat",
                            color: "0 0.3882353 0.4823529 1",
                            imageType: UnityEngine.UI.Image.Type.Simple,
                            anchorMin: "0 0",
                            anchorMax: "1 1",
                            offsetMin: "0 0",
                            offsetMax: "0 0"
                            /* name: "Viewport" */);

                        if (ColorUtility.TryParseHtmlString(mapImage.backgroundHex, out Color backgroundColor))
                        {
                            lrEOGK.AddPanel(
                                color: $"{backgroundColor.r} {backgroundColor.g} {backgroundColor.b} {backgroundColor.a}",
                                material: CUI.Defaults.IconMaterial
                                );
                        }

                        var map = lrEOGK.AddImage(
                           content: server.Map.crc.ToString(),
                           anchorMin: "0.5 0.5",
                           anchorMax: "0.5 0.5",
                           offsetMin: $"-275 -275",
                           offsetMax: $"275 275"
                           /* name: "Panel" */);

                        lrEOGK.AddText(
                            text: "ВЫБЕРИТЕ ТОЧКУ СПАВНА",
                            color: "0.8862746 0.8588236 0.8274511 1",
                            font: CUI.Font.RobotoCondensedBold,
                            fontSize: 20,
                            align: TextAnchor.MiddleCenter,
                            overflow: VerticalWrapMode.Overflow,
                            anchorMin: "0 1",
                            anchorMax: "0 1",
                            offsetMin: "20 -50",
                            offsetMax: "320 -10"
                            /* name: "Tip" */);
                        lrEOGK.AddText(
                            text: "СПИСОК НАЗВАННЫХ ТОЧЕК:",
                            color: "0.8862746 0.8588236 0.8274511 1",
                            font: CUI.Font.RobotoCondensedBold,
                            fontSize: 18,
                            align: TextAnchor.LowerLeft,
                            overflow: VerticalWrapMode.Overflow,
                            anchorMin: "0 0",
                            anchorMax: "0 0",
                            offsetMin: "20 464.6",
                            offsetMax: "320 492.6"
                            /* name: "ListTitle" */);

                        CUI.Element hIkBVQ = lrEOGK.AddPanel(
                            material: "assets/content/ui/menuui/mainmenu.panel.mat",
                            imageType: UnityEngine.UI.Image.Type.Simple,
                            anchorMin: "0 0.5",
                            anchorMax: "0 0.5",
                            offsetMin: "20 -269.7",
                            offsetMax: "320 180.3"
                            /* name: "PointList" */);

                        int gridElementCounter = 0;
                        foreach (var group in server.SpawnPoints.GroupBy(sp => sp.Name))
                        {
                            bool hasName = !string.IsNullOrEmpty(group.Key);
                            int count = group.Count();
                            int id;
                            bool isSelected = group.Contains(SelectedSpawnPoint);
                            if (hasName && count > 1)
                            {
                                SpawnPoint randomOfGroup = group.ElementAt(UnityEngine.Random.Range(0, count));
                                id = randomOfGroup.Id;
                            }
                            else
                            {
                                id = group.ElementAt(0).Id;
                            }

                            string command = $"cargonpc select-spawnpoint {id}";
                            Vector3 position = Vector3Extensions.CenterPoint(group.Select(sp => sp.Position));

                            Vector2 pos = WorldToRel(position, server.WorldSize);
                            map.AddButton(
                             command: command,
                             sprite: "assets/content/ui/map/icon-map_waypoint.png",
                             color: (isSelected ? "1 1 0 1" : "1 1 1 1"),
                             anchorMin: $"{pos.x} {pos.y}",
                             anchorMax: $"{pos.x} {pos.y}",
                             offsetMin: "-10 0",
                             offsetMax: "10 20");


                            if (!hasName)
                                continue;

                            CUI.Element JOAUbc = hIkBVQ.AddButton(
                                command: command,
                                color: (isSelected ? "0.4 0.4 0.4 0.6" : "0 0 0 0.6"),
                                material: "assets/icons/iconmaterial.mat",
                                imageType: UnityEngine.UI.Image.Type.Simple,
                                anchorMin: "0 1",
                                anchorMax: "1 1",
                                offsetMin: $"0 -{(gridElementCounter + 1) * 40 + gridElementCounter * 5}",
                                offsetMax: $"0 -{gridElementCounter * 40 + gridElementCounter * 5}"
                                /* name: "Button" */);
                            JOAUbc.AddText(
                                text: group.Key,
                                color: "0.8862746 0.8588236 0.8274511 1",
                                font: CUI.Font.RobotoCondensedRegular,
                                fontSize: 15,
                                align: TextAnchor.MiddleCenter,
                                overflow: VerticalWrapMode.Overflow,
                                anchorMin: "0 0",
                                anchorMax: "1 1",
                                offsetMin: "0 0",
                                offsetMax: "0 0"
                                /* name: "Text" */);
                            gridElementCounter++;
                        }
                        hIkBVQ.Components.AddScrollView(
                            vertical: true,
                            anchorMin: "0 1",
                            anchorMax: "1 1",
                            offsetMin: $"0 -{Mathf.Max(450, gridElementCounter * 45)}");

                        {
                            CUI.Element crwpTR = lrEOGK.AddButton(
                                command: "cargonpc select-spawnpoint random",
                                color: "0.5188679 0.5188679 0.5188679 1",
                                imageType: UnityEngine.UI.Image.Type.Simple,
                                anchorMin: "1 0",
                                anchorMax: "1 0",
                                offsetMin: "-257.5 70.3",
                                offsetMax: "-57.5 120.3"
                                /* name: "Random Button" */);
                            crwpTR.AddText(
                                text: "СЛУЧАЙНАЯ ТОЧКА",
                                font: CUI.Font.RobotoCondensedBold,
                                fontSize: 20,
                                align: TextAnchor.MiddleCenter,
                                overflow: VerticalWrapMode.Overflow,
                                anchorMin: "0 0",
                                anchorMax: "1 1",
                                offsetMin: "0 0",
                                offsetMax: "0 0"
                                /* name: "Text" */);
                        }
                        {
                            CUI.Element FRjmSA = lrEOGK.AddButton(
                                command: continueButtonCommand,
                                close: "Order Map Selector",
                                color: "0.03444285 0.709902 0.8113208 1",
                                imageType: UnityEngine.UI.Image.Type.Simple,
                                anchorMin: "1 0",
                                anchorMax: "1 0",
                                offsetMin: "-257.5 11.8",
                                offsetMax: "-57.5 61.8"
                                /* name: "Continue Button" */);
                            FRjmSA.AddText(
                                text: continueButtonText,
                                font: CUI.Font.RobotoCondensedBold,
                                fontSize: 20,
                                align: TextAnchor.MiddleCenter,
                                overflow: VerticalWrapMode.Overflow,
                                anchorMin: "0 0",
                                anchorMax: "1 1",
                                offsetMin: "0 0",
                                offsetMax: "0 0"
                                /* name: "Text" */);
                        }
                    }
                }
                root.Render(connection);

                Vector2 WorldToRel(Vector3 worldPosition, int worldSize)
                {
                    var halfWorldSize = worldSize / 2;
                    return new Vector2((worldPosition.x + halfWorldSize) / worldSize, (worldPosition.z + halfWorldSize) / worldSize);
                }
            }


            // Контент заказа
            private void CreateOrderContent(CUI.Element root)
            {
                {
                    CUI.Element XfgDLd = root.AddContainer(
                        anchorMin: "0 0",
                        anchorMax: "1 1",
                        offsetMin: "0 0",
                        offsetMax: "0 0",
                        name: "Create Order Content").AddDestroySelfAttribute();
                    {
                        CUI.Element ACxrMv = XfgDLd.AddContainer(
                            anchorMin: "0 0",
                            anchorMax: "0 1",
                            offsetMin: "13 10",
                            offsetMax: "433 -10"
                            /* name: "Servers" */);
                        ACxrMv.AddText(
                            text: ВЫБЕРЕТЕ_СЕРВЕР,
                            color: "0.7921569 0.7686275 0.7411765 1",
                            font: CUI.Font.RobotoCondensedBold,
                            fontSize: 20,
                            align: TextAnchor.LowerLeft,
                            overflow: VerticalWrapMode.Overflow,
                            anchorMin: "0 1",
                            anchorMax: "1 1",
                            offsetMin: "-0.3 -29.098",
                            offsetMax: "-0.3 0.902"
                            /* name: "Label" */);
                        CargoUI_Order_ServersLayout(ACxrMv, "cargonpc update-data server {0}");
                    }
                    {
                        CUI.Element ZrhVmX = XfgDLd.AddContainer(
                            anchorMin: "0.5 0",
                            anchorMax: "0.5 1",
                            offsetMin: "-180.895 414.184",
                            offsetMax: "203.247 -9.098"
                            /* name: "Price List" */);
                        ZrhVmX.AddText(
                            text: СТОИМОСТЬ_ОТПРАВКИ,
                            color: "0.7921569 0.7686275 0.7411765 1",
                            font: CUI.Font.RobotoCondensedBold,
                            fontSize: 20,
                            align: TextAnchor.MiddleLeft,
                            overflow: VerticalWrapMode.Overflow,
                            anchorMin: "0 1",
                            anchorMax: "1 1",
                            offsetMin: "6 -30",
                            offsetMax: "-6 0"
                            /* name: "Title" */);
                        {
                            CUI.Element UJaEIE = ZrhVmX.AddContainer(
                                anchorMin: "0 1",
                                anchorMax: "1 1",
                                offsetMin: "6 -105",
                                offsetMax: "-4 -35"
                                /* name: "Container" */);

                            {
                                int i = 0;
                                foreach (var pair in new (string format, string price)[]
                                {
                                    (ФОРМАТ_ПРЕДМЕТЫ_N_ВАЛЮТЫ, GetPriceString(PriceCalculator.GetContentsPrice(this.player.OrderContainer.Proto, CurrentCurrency))),
                                    (ФОРМАТ_ДОСТАВКА_N_ВАЛЮТЫ, GetPriceString(PriceCalculator.GetDeliveryVariantPrice(DeliveryVariant, CurrentCurrency))),
                                    (ФОРМАТ_ХРАНЕНИЕ_N_ВАЛЮТЫ, GetPriceString(PriceCalculator.GetStorageCost(this.player.OrderContainer, CurrentCurrency)))
                                }.OrderByDescending(p => p.price))
                                {
                                    UJaEIE.AddText(
                                        text: string.Format(pair.format, pair.price),
                                        color: "0.745283 0.745283 0.745283 1",
                                        font: CUI.Font.RobotoCondensedRegular,
                                        fontSize: 16,
                                        align: TextAnchor.UpperLeft,
                                        overflow: VerticalWrapMode.Overflow,
                                        anchorMin: "0 1",
                                        anchorMax: "1 1",
                                        offsetMin: $"0 -{(i + 1) * 25}",
                                        offsetMax: $"0 -{i * 25}"
                                        /* name: "Text" */);
                                    i++;
                                }
                            }

                        }
                    }
                    {
                        CUI.Element hjKREf = XfgDLd.AddButton(
                            command: "cargonpc open-order-container",
                            color: "0.1058824 0.1058824 0.1058824 1",
                            imageType: UnityEngine.UI.Image.Type.Simple,
                            anchorMin: "0.5 0.5",
                            anchorMax: "0.5 0.5",
                            offsetMin: "-180.894 74.18",
                            offsetMax: "203.246 134.18"
                            /* name: "Select Items Button" */);
                        hjKREf.AddText(
                            text: ВЫБРАТЬ_ПРЕДМЕТЫ,
                            color: "0.8862745 0.8588235 0.827451 1",
                            font: CUI.Font.RobotoCondensedRegular,
                            fontSize: 28,
                            align: TextAnchor.MiddleCenter,
                            overflow: VerticalWrapMode.Overflow,
                            anchorMin: "0 0",
                            anchorMax: "1 1",
                            offsetMin: "15 0",
                            offsetMax: "0 0"
                            /* name: "Text" */);
                        hjKREf.AddPanel(
                            sprite: "assets/icons/add.png",
                            material: "assets/icons/iconmaterial.mat",
                            color: "0.8862745 0.8588235 0.827451 1",
                            imageType: UnityEngine.UI.Image.Type.Simple,
                            anchorMin: "0 0.5",
                            anchorMax: "0 0.5",
                            offsetMin: "15 -15",
                            offsetMax: "45 15"
                            /* name: "Panel" */);
                    }
                    {
                        CUI.Element ZMBqnX = XfgDLd.AddButton(
                            command: $"cargonpc force-complete-container {this.player.OrderContainer.DatabaseId}",
                            color: "0.3679245 0.1623804 0.1336329 1",
                            imageType: UnityEngine.UI.Image.Type.Simple,
                            anchorMin: "0.5 0.5",
                            anchorMax: "0.5 0.5",
                            offsetMin: "-180.895 8.3",
                            offsetMax: "203.245 68.3",
                            name: $"Cargo_DeleteButton_{this.player.OrderContainer.DatabaseId}");
                        ZMBqnX.AddText(
                            text: УДАЛИТЬ_КОНТЕЙНЕР,
                            color: "0.8862745 0.8588235 0.827451 1",
                            font: CUI.Font.RobotoCondensedRegular,
                            fontSize: 28,
                            align: TextAnchor.MiddleCenter,
                            overflow: VerticalWrapMode.Overflow,
                            anchorMin: "0 0",
                            anchorMax: "1 1",
                            offsetMin: "15 0",
                            offsetMax: "0 0"
                            /* name: "Text" */);
                        ZMBqnX.AddPanel(
                            sprite: "assets/icons/clear.png",
                            material: "assets/icons/iconmaterial.mat",
                            color: "0.8862745 0.8588235 0.827451 1",
                            imageType: UnityEngine.UI.Image.Type.Simple,
                            anchorMin: "0 0.5",
                            anchorMax: "0 0.5",
                            offsetMin: "15 -15",
                            offsetMax: "45 15"
                            /* name: "Panel" */);
                    }
                    {

                        CUI.Element NLwXdd = XfgDLd.AddButton(
                            command: $"cargonpc set-spawnpoint",
                            color: "0 0 0 0",
                            imageType: UnityEngine.UI.Image.Type.Simple,
                            anchorMin: "0 1",
                            anchorMax: "0 1",
                            offsetMin: "439.105 -335",
                            offsetMax: "1226.995 -285"
                            /* name: "Go To Travel" */);
                        NLwXdd.AddText(
                            text: ОТПРАВИТЬСЯ_ВМЕСТЕ_С_ГРУЗОМ,
                            color: "0.8862745 0.8588235 0.827451 1",
                            font: CUI.Font.RobotoCondensedRegular,
                            fontSize: 20,
                            align: TextAnchor.MiddleLeft,
                            overflow: VerticalWrapMode.Overflow,
                            anchorMin: "0 0",
                            anchorMax: "1 1",
                            offsetMin: "40 8",
                            offsetMax: "-8 -8"
                            /* name: "Title" */);
                        var circle = NLwXdd.AddPanel(
                            sprite: "assets/icons/circle_open.png",
                            material: "assets/icons/iconmaterial.mat",
                            imageType: UnityEngine.UI.Image.Type.Simple,
                            anchorMin: "0 0.5",
                            anchorMax: "0 0.5",
                            offsetMin: "7.5 -12.5",
                            offsetMax: "32.5 12.5"
                            /* name: "Circle" */);

                        if (SelectedSpawnPoint != null)
                        {
                            circle.AddPanel(
                                sprite: "assets/icons/circle_closed_white.png",
                                material: "assets/icons/iconmaterial.mat",
                                imageType: UnityEngine.UI.Image.Type.Simple,
                                anchorMin: "0 0",
                                anchorMax: "1 1",
                                offsetMin: "5 5",
                                offsetMax: "-5 -5"
                                /* name: "Dot" */);
                        }

                        {
                            CUI.Element sthNXX = NLwXdd.AddPanel(
                                color: "0.1226415 0.1226415 0.1226415 1",
                                imageType: UnityEngine.UI.Image.Type.Simple,
                                anchorMin: "0.5 0.5",
                                anchorMax: "0.5 0.5",
                                offsetMin: "-386.445 -128",
                                offsetMax: "237.917 -28"
                                /* name: "Information" */);
                            sthNXX.AddPanel(
                                sprite: "assets/icons/info.png",
                                material: "assets/icons/iconmaterial.mat",
                                color: "0.5754717 0.5754717 0.5754717 1",
                                imageType: UnityEngine.UI.Image.Type.Simple,
                                anchorMin: "0 0.5",
                                anchorMax: "0 0.5",
                                offsetMin: "19 -25",
                                offsetMax: "69 25"
                                /* name: "Icon" */);
                            sthNXX.AddText(
                                text: ОТПРАВИТЬСЯ_ВМЕСТЕ_С_ГРУЗОМ_ОПИСАНИЕ,
                                color: "0.754717 0.754717 0.754717 0.7176471",
                                font: CUI.Font.RobotoCondensedRegular,
                                fontSize: 18,
                                align: TextAnchor.MiddleCenter,
                                overflow: VerticalWrapMode.Overflow,
                                anchorMin: "0 0",
                                anchorMax: "1 1",
                                offsetMin: "90 10",
                                offsetMax: "-10 -10"
                                /* name: "Text" */);
                        }
                    }
                    {
                        CUI.Element dGDeVq = XfgDLd.AddContainer(
                            anchorMin: "0 0",
                            anchorMax: "0 1",
                            offsetMin: "842.435 304.151",
                            offsetMax: "1227 -9.098"
                            /* name: "Delivery Variants" */);
                        dGDeVq.AddText(
                            text: ВАРИАНТЫ_ДОСТАВКИ,
                            color: "0.7921569 0.7686275 0.7411765 1",
                            font: CUI.Font.RobotoCondensedBold,
                            fontSize: 20,
                            align: TextAnchor.LowerLeft,
                            overflow: VerticalWrapMode.Overflow,
                            anchorMin: "0 1",
                            anchorMax: "1 1",
                            offsetMin: "-0.3 -29.098",
                            offsetMax: "-0.3 0.902"
                            /* name: "Label" */);
                        {

                            CUI.Element KIczHS = dGDeVq.AddContainer(
                                anchorMin: "0 0",
                                anchorMax: "1 1",
                                offsetMin: "-0.3 0",
                                offsetMax: "-0.3 -40"
                                /* name: "Layout" */);
                            {
                                int i = 0;
                                foreach (DeliveryVariant variant in Enum.GetValues(typeof(DeliveryVariant)))
                                {
                                    CUI.Element PdKXmj = KIczHS.AddButton(
                                    command: $"cargonpc update-data delivery-variant {i}",
                                    color: "0 0 0 0",
                                    imageType: UnityEngine.UI.Image.Type.Simple,
                                    anchorMin: "0 1",
                                    anchorMax: "1 1",
                                    offsetMin: $"0 -{(i + 1) * 50}",
                                    offsetMax: $"0 -{i * 50}"
                                    /* name: "Element" */);
                                    PdKXmj.AddText(
                                        text: EnumToString(variant),
                                        color: "0.7921569 0.7686275 0.7411765 1",
                                        font: CUI.Font.RobotoCondensedRegular,
                                        fontSize: 20,
                                        align: TextAnchor.MiddleLeft,
                                        overflow: VerticalWrapMode.Overflow,
                                        anchorMin: "0 0",
                                        anchorMax: "1 1",
                                        offsetMin: "40 8",
                                        offsetMax: "-8 -8"
                                        /* name: "Variant Name" */);
                                    {
                                        CUI.Element BpohkQ = PdKXmj.AddPanel(
                                            sprite: "assets/icons/circle_open.png",
                                            material: "assets/icons/iconmaterial.mat",
                                            imageType: UnityEngine.UI.Image.Type.Simple,
                                            anchorMin: "0 0.5",
                                            anchorMax: "0 0.5",
                                            offsetMin: "7.5 -12.5",
                                            offsetMax: "32.5 12.5"
                                            /* name: "Circle" */);

                                        if (variant == DeliveryVariant)
                                        {
                                            BpohkQ.AddPanel(
                                                sprite: "assets/icons/circle_closed_white.png",
                                                material: "assets/icons/iconmaterial.mat",
                                                imageType: UnityEngine.UI.Image.Type.Simple,
                                                anchorMin: "0 0",
                                                anchorMax: "1 1",
                                                offsetMin: "5 5",
                                                offsetMax: "-5 -5"
                                                /* name: "Dot" */);
                                        }
                                    }
                                    i++;
                                }
                            }
                        }
                    }
                    XfgDLd.AddText(
                        text: string.Format(ФОРМАТ_ОБЩАЯ_СТОИМОСТЬ_N_ВАЛЮТЫ, GetPriceString(PriceCalculator.GetTotalShippingPrice(player, player.OrderContainer)).ToUpper()),
                        color: "0.8862745 0.8588235 0.827451 1",
                        font: CUI.Font.RobotoCondensedBold,
                        fontSize: 28,
                        align: TextAnchor.MiddleLeft,
                        overflow: VerticalWrapMode.Overflow,
                        anchorMin: "0 1",
                        anchorMax: "1 1",
                        offsetMin: "439.106 -544.998",
                        offsetMax: "-292.594 -474.998"
                        /* name: "Total Price" */);
                    {
                        CUI.Element COJpvL = XfgDLd.AddButton(
                            command: "cargonpc send",
                            color: "0.1054646 0.2830189 0.1799649 1",
                            material: "assets/icons/iconmaterial.mat",
                            imageType: UnityEngine.UI.Image.Type.Simple,
                            anchorMin: "1 0",
                            anchorMax: "1 0",
                            offsetMin: "-283 15",
                            offsetMax: "-13 85"
                            /* name: "Send Button" */);
                        COJpvL.AddText(
                            text: ОТПРАВИТЬ,
                            color: "0.8862745 0.8588235 0.827451 1",
                            font: CUI.Font.RobotoCondensedBold,
                            fontSize: 36,
                            align: TextAnchor.MiddleCenter,
                            overflow: VerticalWrapMode.Overflow,
                            anchorMin: "0 0",
                            anchorMax: "1 1",
                            offsetMin: "0 0",
                            offsetMax: "0 0"
                            /* name: "Text" */);
                    }
                }
            }


            // Часть контента заказа, список серверов
            private void CargoUI_Order_ServersLayout(CUI.Element root, string commandFormat)
            {
                Server selectedServer = this.DestinationServer;
                CUI.Element CjLMkN = root.AddContainer(
                        anchorMin: "0 0",
                        anchorMax: "1 1",
                        offsetMin: "0 0",
                        offsetMax: "0 -40",
                        name: "CargoUI_Order_ServersLayout").AddDestroySelfAttribute();
                {
                    int i = 0;
                    IEnumerator<Server> enumerator = PluginInstance.serverList.Values.GetEnumerator();
                    while (enumerator.MoveNext())
                    {
                        Server server = enumerator.Current;
                        if (server == PluginInstance.currentServer && !config.VisibileCurrentServer)
                            continue;

                        string command = string.Format(commandFormat, server.DatabaseId);

                        CUI.Element WkgQQY = CjLMkN.AddButton(
                            command: command,
                            color: "0 0 0 0",
                            imageType: UnityEngine.UI.Image.Type.Simple,
                            anchorMin: "0 1",
                            anchorMax: "1 1",
                            offsetMin: $"0 -{(i + 1) * 50}",
                            offsetMax: $"0 -{i * 50}"
                            /* name: "Element" */);
                        WkgQQY.AddText(
                            text: server.Name,
                            color: "0.7921569 0.7686275 0.7411765 1",
                            font: CUI.Font.RobotoCondensedRegular,
                            fontSize: 20,
                            align: TextAnchor.MiddleLeft,
                            overflow: VerticalWrapMode.Overflow,
                            anchorMin: "0 0",
                            anchorMax: "1 1",
                            offsetMin: "40 8",
                            offsetMax: "-8 -8"
                            /* name: "Server Name" */);
                        WkgQQY.AddButton(
                            command: command,
                            sprite: "assets/icons/circle_open.png",
                            material: "assets/icons/iconmaterial.mat",
                            imageType: UnityEngine.UI.Image.Type.Simple,
                            anchorMin: "0 0.5",
                            anchorMax: "0 0.5",
                            offsetMin: "7.5 -12.5",
                            offsetMax: "32.5 12.5"
                            /* name: "Circle" */);

                        if (server == selectedServer)
                        {
                            WkgQQY.AddPanel(
                                sprite: "assets/icons/circle_closed_white.png",
                                material: "assets/icons/iconmaterial.mat",
                                imageType: UnityEngine.UI.Image.Type.Simple,
                                anchorMin: "0 0",
                                anchorMax: "1 1",
                                offsetMin: "12.5 17.5",
                                offsetMax: "-392.5 -17.5"
                                /* name: "Dot" */);
                        }
                        i++;
                    }

                }
            }

        }

        public class UIManager : PlayerAssociatedCollection<Connection, PlayerUI> { }

        #region LocalizationText
        // Ленгирования текст относительно игрока, с автоматической записью в lang file и удобным использованием
        public class LocalizationText
        {
            private string originalText;
            private string key;

            private BasePlayer player;
            private Connection connection;


            private static Lang lang = Interface.Oxide.GetLibrary<Lang>(null);
            private static Dictionary<string, string> messages = new Dictionary<string, string>();
            private static Plugin plugin;

            private const string DEFAULT_LANGUAGE = "en";

            private Connection Connection
            {
                get
                {
                    return connection ?? player?.Connection;
                }
            }

            public LocalizationText(string text)
            {
                this.originalText = text;
                if (!string.IsNullOrEmpty(text))
                {
                    this.key = text.GetHashCode().ToString();
                    if (!messages.ContainsKey(this.key))
                    {
                        messages.Add(this.key, text);
                        if (plugin != null)
                            UpdateLang();
                    }
                }
            }

            public void Assign(BasePlayer basePlayer)
            {
                this.player = basePlayer;
            }

            public void Assign(Connection connection)
            {
                this.connection = connection;
            }


            public static implicit operator LocalizationText(string str)
            {
                return new LocalizationText(str);
            }

            public static implicit operator string(LocalizationText label)
            {
                return label.ToString();
            }

            public override string ToString()
            {
                return this.Localize(Connection);
            }

            public string Localize(string userId)
            {
                if (string.IsNullOrEmpty(userId))
                    return this.originalText;

                string result = this.originalText;
                if (this.key != null)
                {
                    result = lang.GetMessage(this.key, plugin, userId);
                    if (result == this.key)
                        result = this.originalText;
                }
                return result;
            }

            public string Localize(BasePlayer player)
            {
                return Localize(player?.Connection);
            }

            public string Localize(Connection connection)
            {
                return Localize(connection?.userid.ToString());
            }

            private static bool MergeMessages(Dictionary<string, string> existingMessages, Dictionary<string, string> messages)
            {
                bool result = false;
                foreach (KeyValuePair<string, string> keyValuePair in messages)
                {
                    if (!existingMessages.ContainsKey(keyValuePair.Key))
                    {
                        existingMessages.Add(keyValuePair.Key, keyValuePair.Value);
                        result = true;
                    }
                }
                return result;
            }

            public static void LoadDefaultMessages(Plugin plugin)
            {
                LocalizationText.plugin = plugin;
                Dictionary<string, string> existingMessages = lang.GetMessages(DEFAULT_LANGUAGE, plugin) ?? new Dictionary<string, string>();
                bool IsMerged = MergeMessages(existingMessages, messages);
                messages = existingMessages;

                if (IsMerged)
                    UpdateLang();
            }


            public static void UpdateLang()
            {
                if (plugin == null)
                    return;
                lang.RegisterMessages(messages, plugin, DEFAULT_LANGUAGE);
            }
        }
        #endregion

        #region Entities
        #region NPC

        public class CargoNPC : Npc15.InteractingNPC, IIdealSlotEntity
        {
            private static LocalizationText TOAST_ITEM_CONTAINS_ITEMS = "The item must not contain items within itself.";
            private static LocalizationText TOAST_ITEM_CONTAINS_AMMO = "The item must not contain ammunation within itself.";

            public Configuration.NPC config;

            public override string GetName(BasePlayer reciever = null)
            {
                string name = config?.Name;
                if (string.IsNullOrEmpty(name))
                    return base.GetName(reciever);
                return ((LocalizationText)name).Localize(reciever);
            }

            public override void Equip()
            {
                foreach (var pair in config.Items)
                    this.inventory.GiveItem(ItemManager.CreateByItemID(pair.Key, 1, pair.Value), ItemMoveModifier.Alt);
            }

            public void UpdateInventory()
            {
                this.inventory.Strip();
                Equip();
                EquipTest();
            }



            [RPC_Server]
            public override void RPC_OpenDialog(RPCMessage msg)
            {
                base.RPC_OpenDialog(msg);
                PluginInstance.playerCollection.GetOrCreate(msg.player).StartDialogWithNPC(this);
            }

            public void OpenContainer(BasePlayer player, ItemContainer container)
            {
                if (player == null || container == null)
                    return;


                container.entityOwner = this;
                container.onPreItemRemove += OnPreItemRemove;
                container.canAcceptItem += CanAcceptItem;
                container.SetBlacklist(Cargo.config.ItemBlacklist.Select(shortname => ItemManager.FindItemDefinition(shortname)).ToArray());

                PlayerLoot playerLoot = player.inventory.loot;
                playerLoot.Clear();
                playerLoot.PositionChecks = true;
                playerLoot.entitySource = this;
                playerLoot.AddContainer(container);
                playerLoot.SendImmediate();
                player.ClientRPC<string>(global::RpcTarget.Player("RPC_OpenLootPanel", player), "generic_resizable");
            }

            private bool CanAcceptItem(Item item, int targetPos)
            {
                BasePlayer player = item.GetOwnerPlayer();

                if (Interface.Oxide.CallHook("CanAcceptItemToCargo", item) is bool @overrideResult)
                    return overrideResult;

                if (item.contents != null && !item.contents.IsEmpty())
                {
                    if (player)
                        player.ShowToast(GameTip.Styles.Red_Normal, TOAST_ITEM_CONTAINS_ITEMS.Localize(player), true);
                    return false;
                }

                if (item.GetHeldEntity() is BaseProjectile projectile)
                {
                    if (projectile.primaryMagazine.contents > 0)
                    {
                        if (player)
                            player.ShowToast(GameTip.Styles.Red_Normal, TOAST_ITEM_CONTAINS_AMMO.Localize(player), true);
                        return false;
                    }
                }

                return true;
            }

            public void OnPreItemRemove(Item item)
            {
                ItemContainer container = item.parent;
                if (container == null)
                    return;


                BasePlayer basePlayer = BasePlayer.activePlayerList.FirstOrDefault(player =>
                {
                    PlayerLoot playerLoot = player.inventory.loot;
                    return playerLoot.IsLooting() && playerLoot.FindContainer(container.uid) != null;
                });
                if (basePlayer == null)
                    return;

                Player pluginPlayer = PluginInstance.playerCollection.Get(basePlayer);
                if (pluginPlayer == null)
                    return;

                ContainerInfo containerInfo = pluginPlayer.FindContainer(container.uid);

                float storageCost = PriceCalculator.GetStorageCost(containerInfo, pluginPlayer.UI.CurrentCurrency);
                if (storageCost <= 0)
                    return;

                IPaymentProvider paymentProvider = pluginPlayer.paymentProvider;
                if (paymentProvider == null)
                    return;

                float storageCostForItem = storageCost / containerInfo.Slots;
                paymentProvider.TakeBalance(basePlayer, storageCostForItem, null);
            }

            public void PlayerStoppedLooting(BasePlayer player)
            {
                PluginInstance.playerCollection.Get(player)?.OnPlayerLootEnd();
            }


        }
        #endregion
        #endregion
        #endregion


        #region Config
        static DatabaseConfiguration databaseConfig;
        static Configuration config;


        public class Configuration
        {
            public class DatabaseCredentials
            {
                [JsonProperty("Host Address")]
                public string Address = "localhost";

                [JsonProperty("Host Port")]
                public int Port = 3306;

                [JsonProperty("Database Name")]
                public string Name = "database_name";

                [JsonProperty("Database Username")]
                public string Username = "enterusername";

                [JsonProperty("Database Password")]
                public string Password = "enterpassword";

                public static DatabaseCredentials DefaultConfig()
                {
                    return new DatabaseCredentials();
                }
            }

            public class NPC
            {
                [JsonProperty("Имя")]
                public string Name { get; set; } = "Карго";

                [JsonProperty("Точка спавна")]
                public (Vector3 position, Vector3 rotation) Coord { get; set; }

                [JsonProperty("Список предметов")]
                public Dictionary<int, ulong> Items { get; set; } = new Dictionary<int, ulong>();
            }

            [JsonProperty("Настройки базы данных")]
            public DatabaseCredentials DbCredentials { get; set; } = new DatabaseCredentials();

            [JsonProperty("Список НПС")]
            public List<NPC> Npc { get; set; } = new List<NPC>();
            
            [JsonProperty("Курс монеты к скрапу")]
            public float ExchangeRate { get; set; } = 2f;
            
            [JsonProperty("Отображать текущий сервер")]
            public bool VisibileCurrentServer { get; set; } = false;

            [JsonProperty("Черный список предметов")]
            public HashSet<string> ItemBlacklist { get; set; } = new HashSet<string>();

            [JsonProperty("Discord Webhook URL")]
            public string DiscordWebhookUrl { get; set; } = "https://discord.com/api/webhooks/YOUR_WEBHOOK_ID/YOUR_WEBHOOK_TOKEN";

            [JsonProperty("Включить логирование в Discord")]
            public bool EnableDiscordLogging { get; set; } = true;

            public static Configuration DefaultConfig()
            {
                return new Configuration();
            }
        }

        public class DatabaseConfiguration
        {

            public class EachContainerProperties
            {
                public float Price { get; set; } = 0f;
                public int StartCapacity { get; set; } = 32;
                public int MaxCapacity { get; set; } = 48;
                public int SlotPrice { get; set; } = 5;
            }

            [JsonProperty("Максимальное количество контейнеров для игрока")]
            public int PlayerMaxActiveContainerCount { get; set; } = 10;

            [JsonProperty("Цена вариантов доставки")]
            public Dictionary<DeliveryVariant, float> DeliveryVariantPrices { get; set; } = new Dictionary<DeliveryVariant, float>
            {
                { DeliveryVariant.Standard, 10f },
                { DeliveryVariant.Express, 25f },
            };

            [JsonProperty("Время в секундах для вариантов доставки")]
            public Dictionary<DeliveryVariant, float> DeliveryVariantTimes { get; set; } = new Dictionary<DeliveryVariant, float>
            {
                { DeliveryVariant.Standard, 7200f },
                { DeliveryVariant.Express, 0f },
            };

            [JsonProperty("Настройки на каждый контейнер")]
            public List<EachContainerProperties> ContainerProperties { get; set; } = new List<EachContainerProperties>();

            [JsonProperty("Цена за отправку предмета")]
            public Dictionary<string, float> DeliveryPricePerItem { get; set; } = new Dictionary<string, float>();

            [JsonProperty("Цена за отправку предмета из категории")]
            public Dictionary<ItemCategory, float> DeliveryPricePerCategory { get; set; } = Enum.GetValues(typeof(ItemCategory)).Cast<ItemCategory>().ToDictionary(x => x, x => 1f);

            [JsonProperty("Сколько часов бесплатного хранения")]
            public float FreeStorageHours { get; set; } = 3f;

            [JsonProperty("Стоимость хранения за час")]
            public float StorageCostPerHour { get; set; } = 10f;

            [JsonProperty("Максимальный стак предметов")]
            public Dictionary<int, int> ItemStackable { get; set; } = new Dictionary<int, int>();

            [JsonProperty("Отображать контейнеры только текущего протокола")]
            public bool VisibileOnlyCurrentProtocol { get; set; } = true;

            [JsonIgnore]
            public int Hash { get; set; }

            public static DatabaseConfiguration Load()
            {
                var list = Query(__Sql(
                    @"SELECT json FROM config WHERE id = 1"));

                if (list == null || list.Count == 0)
                    return DefaultConfig();
                else
                    return Load((string)list[0]["json"]);
            }

            public static DatabaseConfiguration Load(string json)
            {
                DatabaseConfiguration config = JsonConvert.DeserializeObject<DatabaseConfiguration>(json);
                config.Hash = json.GetHashCode();
                return config;
            }

            public void Save()
            {
                if (PluginInstance.databaseConnection == null)
                    return;

                string json = JsonConvert.SerializeObject(this);
                this.Hash = json.GetHashCode();
                NonQuery(__Sql(
                    @"REPLACE INTO config (id, json, hash) VALUES (1, @0, @1)", json, this.Hash));
            }

            public static DatabaseConfiguration DefaultConfig()
            {
                return new DatabaseConfiguration();
            }
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                config = Config.ReadObject<Configuration>();
                if (config == null) LoadDefaultConfig();
                SaveConfig();
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                PrintWarning("Creating new configuration file.");
                LoadDefaultConfig();
            }
        }

        protected override void LoadDefaultConfig() => config = Configuration.DefaultConfig();
        protected override void SaveConfig() => Config.WriteObject(config);
        #endregion
    }
}


#region Staff
namespace Oxide.Plugins
{
    #region PlayerAssociated
    partial class Cargo
    {
        public abstract class PlayerAssociated
        {
            private BasePlayer m_player;
            private Connection m_connection;
            private ulong m_UserId;

            protected BasePlayer Player
            {
                get
                {
                    if (m_player != null)
                        return m_player;

                    if (m_UserId > 0)
                        m_player = BasePlayer.FindByID(m_UserId);

                    return m_player;
                }
            }

            protected ulong UserId
            {
                get
                {
                    return m_UserId;
                }
            }

            protected Connection Connection
            {
                get
                {
                    return m_connection ?? m_player?.Connection;
                }
            }

            public PlayerAssociated(BasePlayer player) : this(player.userID)
            {
                this.m_player = player;
            }

            public PlayerAssociated(Connection connection) : this(connection.userid)
            {
                this.m_connection = connection;
                this.m_player = connection.player as BasePlayer;
            }

            public PlayerAssociated(ulong userID)
            {
                this.m_UserId = userID;
            }
        }

        public interface IPlayerAssociatedCollection<TKey, TValue> where TValue : PlayerAssociated
        {
            public abstract TValue Get(TKey player);

            public abstract TValue Create(TKey player);

            public abstract TValue GetOrCreate(TKey player);

            public abstract void Clear();
        }

        public class PlayerAssociatedCollection<TKey, TValue> : Dictionary<TKey, TValue>, IPlayerAssociatedCollection<TKey, TValue> where TValue : PlayerAssociated
        {
            public virtual TValue Get(TKey key)
            {
                this.TryGetValue(key, out TValue result);
                return result;
            }

            public virtual TValue Create(TKey key)
            {
                return (this[key] = (TValue)Activator.CreateInstance(typeof(TValue), new object[] { key }));
            }

            public virtual TValue GetOrCreate(TKey key)
            {
                if (key == null)
                    return default(TValue);

                return Get(key) ?? Create(key);
            }

            public virtual new void Clear()
            {
                foreach (TValue value in this.Values)
                {
                    if (value is IDisposable disposable)
                        disposable.Dispose();
                }

                base.Clear();
            }
        }

        public class BasePlayerAssociatedCollection<T> : PlayerAssociatedCollection<BasePlayer, T> where T : PlayerAssociated { }
        public class PlayerIDAssociatedCollection<T> : PlayerAssociatedCollection<ulong, T> where T : PlayerAssociated { }
        public class PlayerConnectionAssociatedCollection<T> : PlayerAssociatedCollection<Connection, T> where T : PlayerAssociated { }
    }
    #endregion
}
#endregion

namespace Oxide.Plugins
{
    #region 0xF UI Library 2.3.2
    partial class Cargo
    {
        public class CUI
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

            private static readonly Dictionary<Font, string> FontToString = new Dictionary<Font, string>
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

            private static readonly Dictionary<TextAnchor, string> TextAnchorToString = new Dictionary<TextAnchor, string>
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

            private static readonly Dictionary<VerticalWrapMode, string> VWMToString = new Dictionary<VerticalWrapMode, string>
            {
                { VerticalWrapMode.Truncate, VerticalWrapMode.Truncate.ToString() },
                { VerticalWrapMode.Overflow, VerticalWrapMode.Overflow.ToString() },
            };

            private static readonly Dictionary<Image.Type, string> ImageTypeToString = new Dictionary<Image.Type, string>
            {
                { Image.Type.Simple, Image.Type.Simple.ToString() },
                { Image.Type.Sliced, Image.Type.Sliced.ToString() },
                { Image.Type.Tiled, Image.Type.Tiled.ToString() },
                { Image.Type.Filled, Image.Type.Filled.ToString() },
            };

            private static readonly Dictionary<InputField.LineType, string> LineTypeToString = new Dictionary<InputField.LineType, string>
            {
                { InputField.LineType.MultiLineNewline, InputField.LineType.MultiLineNewline.ToString() },
                { InputField.LineType.MultiLineSubmit, InputField.LineType.MultiLineSubmit.ToString() },
                { InputField.LineType.SingleLine, InputField.LineType.SingleLine.ToString() },
            };

            private static readonly Dictionary<ScrollRect.MovementType, string> MovementTypeToString = new Dictionary<ScrollRect.MovementType, string>
            {
                { ScrollRect.MovementType.Unrestricted, ScrollRect.MovementType.Unrestricted.ToString() },
                { ScrollRect.MovementType.Elastic, ScrollRect.MovementType.Elastic.ToString() },
                { ScrollRect.MovementType.Clamped, ScrollRect.MovementType.Clamped.ToString() },
            };


            private static readonly Dictionary<TimerFormat, string> TimerFormatToString = new Dictionary<TimerFormat, string>
            {
                { TimerFormat.None, TimerFormat.None.ToString() },
                { TimerFormat.SecondsHundreth, TimerFormat.SecondsHundreth.ToString() },
                { TimerFormat.MinutesSeconds, TimerFormat.MinutesSeconds.ToString() },
                { TimerFormat.MinutesSecondsHundreth, TimerFormat.MinutesSecondsHundreth.ToString() },
                { TimerFormat.HoursMinutes, TimerFormat.HoursMinutes.ToString() },
                { TimerFormat.HoursMinutesSeconds, TimerFormat.HoursMinutesSeconds.ToString() },
                { TimerFormat.HoursMinutesSecondsMilliseconds, TimerFormat.HoursMinutesSecondsMilliseconds.ToString() },
                { TimerFormat.HoursMinutesSecondsTenths, TimerFormat.HoursMinutesSecondsTenths.ToString() },
                { TimerFormat.DaysHoursMinutes, TimerFormat.DaysHoursMinutes.ToString() },
                { TimerFormat.DaysHoursMinutesSeconds, TimerFormat.DaysHoursMinutesSeconds.ToString() },
                { TimerFormat.Custom, TimerFormat.Custom.ToString() },
            };

            public static class Defaults
            {
                public const string VectorZero = "0 0";
                public const string VectorOne = "1 1";
                public const string Color = "1 1 1 1";
                public const string OutlineColor = "0 0 0 1";
                public const string Sprite = "assets/content/ui/ui.background.tile.psd";
                public const string Material = "assets/content/ui/namefontmaterial.mat";
                public const string IconMaterial = "assets/icons/iconmaterial.mat";
                public const Image.Type ImageType = Image.Type.Simple;
                public const CUI.Font Font = CUI.Font.RobotoCondensedRegular;
                public const int FontSize = 14;
                public const TextAnchor Align = TextAnchor.UpperLeft;
                public const VerticalWrapMode VerticalOverflow = VerticalWrapMode.Overflow;
                public const InputField.LineType LineType = InputField.LineType.SingleLine;
            }

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
                CommunityEntity.ServerInstance.ClientRPCEx<string>(new SendInfo
                {
                    connection = connection
                }, null, "AddUI", json);
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

                void SerializeType() => CUI.SerializeType(IComponent, jsonWriter);
                void SerializeField(string key, object value, object defaultValue) => CUI.SerializeField(key, value, defaultValue, jsonWriter);
                void SerializeScrollbar(string key, CuiScrollbar value) => CUI.SerializeField(key, value, jsonWriter);

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
                            SerializeField("imagetype", ImageTypeToString[component.ImageType], ImageTypeToString[Image.Type.Simple]);
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
                            SerializeField("align", TextAnchorToString[component.Align], TextAnchorToString[TextAnchor.UpperLeft]);
                            SerializeField("color", component.Color, colorWhite);
                            SerializeField("verticalOverflow", VWMToString[component.VerticalOverflow], VWMToString[VerticalWrapMode.Truncate]);
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
                            SerializeField("imagetype", ImageTypeToString[component.ImageType], ImageTypeToString[Image.Type.Simple]);
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
                            SerializeField("align", TextAnchorToString[component.Align], TextAnchorToString[TextAnchor.UpperLeft]);
                            SerializeField("color", component.Color, colorWhite);
                            SerializeField("command", component.Command, null);
                            SerializeField("characterLimit", component.CharsLimit, 0);
                            SerializeField("lineType", LineTypeToString[component.LineType], LineTypeToString[InputField.LineType.SingleLine]);
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
                            SerializeField("movementType", MovementTypeToString[component.MovementType], MovementTypeToString[ScrollRect.MovementType.Clamped]);
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
                            SerializeField("timerFormat", TimerFormatToString[component.TimerFormat], TimerFormatToString[TimerFormat.None]);
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
                public ComponentList Components { get; set; } = new ComponentList();

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

                public Element() { }
                public Element(Element parent)
                {
                    AssignParent(parent);
                }

                public CUI.Element AssignParent(Element parent)
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
                    string color = Defaults.Color,
                    CUI.Font font = Defaults.Font,
                    int fontSize = Defaults.FontSize,
                    TextAnchor align = Defaults.Align,
                    VerticalWrapMode overflow = Defaults.VerticalOverflow,
                    string anchorMin = Defaults.VectorZero,
                    string anchorMax = Defaults.VectorOne,
                    string offsetMin = Defaults.VectorZero,
                    string offsetMax = Defaults.VectorZero,
                    string name = null)
                {
                    return Add(ElementContructor.CreateText(text, color, font, fontSize, align, overflow, anchorMin, anchorMax, offsetMin, offsetMax, name));
                }

                public Element AddOutlinedText(
                   string text,
                   string color = Defaults.Color,
                   CUI.Font font = Defaults.Font,
                   int fontSize = Defaults.FontSize,
                   TextAnchor align = Defaults.Align,
                   VerticalWrapMode overflow = Defaults.VerticalOverflow,
                   string outlineColor = Defaults.OutlineColor,
                   int outlineWidth = 1,
                   string anchorMin = Defaults.VectorZero,
                   string anchorMax = Defaults.VectorOne,
                   string offsetMin = Defaults.VectorZero,
                   string offsetMax = Defaults.VectorZero,
                   string name = null)
                {
                    return Add(ElementContructor.CreateOutlinedText(text, color, font, fontSize, align, overflow, outlineColor, outlineWidth, anchorMin, anchorMax, offsetMin, offsetMax, name));
                }

                public Element AddInputfield(
                    string command = null,
                    string text = "",
                    string color = Defaults.Color,
                    CUI.Font font = Defaults.Font,
                    int fontSize = Defaults.FontSize,
                    TextAnchor align = Defaults.Align,
                    InputField.LineType lineType = Defaults.LineType,
                    CUI.InputType inputType = CUI.InputType.Default,
                    bool @readonly = false,
                    bool autoFocus = false,
                    bool isPassword = false,
                    int charsLimit = 0,
                    string anchorMin = Defaults.VectorZero,
                    string anchorMax = Defaults.VectorOne,
                    string offsetMin = Defaults.VectorZero,
                    string offsetMax = Defaults.VectorZero,
                    string name = null)
                {
                    return Add(ElementContructor.CreateInputfield(command, text, color, font, fontSize, align, lineType, inputType, @readonly, autoFocus, isPassword, charsLimit, anchorMin, anchorMax, offsetMin, offsetMax, name));
                }

                public Element AddPanel(
                    string color = Defaults.Color,
                    string sprite = Defaults.Sprite,
                    string material = Defaults.Material,
                    Image.Type imageType = Defaults.ImageType,
                    string anchorMin = Defaults.VectorZero,
                    string anchorMax = Defaults.VectorOne,
                    string offsetMin = Defaults.VectorZero,
                    string offsetMax = Defaults.VectorZero,
                    bool cursorEnabled = false,
                    bool keyboardEnabled = false,
                    string name = null)
                {
                    return Add(ElementContructor.CreatePanel(color, sprite, material, imageType, anchorMin, anchorMax, offsetMin, offsetMax, cursorEnabled, keyboardEnabled, name));
                }

                public Element AddButton(
                   string command = null,
                   string close = null,
                   string color = Defaults.Color,
                   string sprite = Defaults.Sprite,
                   string material = Defaults.Material,
                   Image.Type imageType = Defaults.ImageType,
                   string anchorMin = Defaults.VectorZero,
                   string anchorMax = Defaults.VectorOne,
                   string offsetMin = Defaults.VectorZero,
                   string offsetMax = Defaults.VectorZero,
                   string name = null)
                {
                    return Add(ElementContructor.CreateButton(command, close, color, sprite, material, imageType, anchorMin, anchorMax, offsetMin, offsetMax, name));
                }

                public Element AddImage(
                    string content,
                    string color = Defaults.Color,
                    string material = null,
                    string anchorMin = Defaults.VectorZero,
                    string anchorMax = Defaults.VectorOne,
                    string offsetMin = Defaults.VectorZero,
                    string offsetMax = Defaults.VectorZero,
                    string name = null)
                {
                    return Add(ElementContructor.CreateImage(content, color, material, anchorMin, anchorMax, offsetMin, offsetMax, name));
                }

                public Element AddHImage(
                    string content,
                    string color = Defaults.Color,
                    string anchorMin = Defaults.VectorZero,
                    string anchorMax = Defaults.VectorOne,
                    string offsetMin = Defaults.VectorZero,
                    string offsetMax = Defaults.VectorZero,
                    string name = null)
                {
                    return AddImage(content, color, Defaults.IconMaterial, anchorMin, anchorMax, offsetMin, offsetMax, name);
                }

                public Element AddIcon(
                    int itemId,
                    ulong skin = 0,
                    string color = Defaults.Color,
                    string sprite = Defaults.Sprite,
                    string material = Defaults.IconMaterial,
                    Image.Type imageType = Defaults.ImageType,
                    string anchorMin = Defaults.VectorZero,
                    string anchorMax = Defaults.VectorOne,
                    string offsetMin = Defaults.VectorZero,
                    string offsetMax = Defaults.VectorZero,
                    string name = null)
                {
                    return Add(ElementContructor.CreateIcon(itemId, skin, color, sprite, material, imageType, anchorMin, anchorMax, offsetMin, offsetMax, name));
                }

                public Element AddContainer(
                    string anchorMin = Defaults.VectorZero,
                    string anchorMax = Defaults.VectorOne,
                    string offsetMin = Defaults.VectorZero,
                    string offsetMax = Defaults.VectorZero,
                    string name = null)
                {
                    return Add(ElementContructor.CreateContainer(anchorMin, anchorMax, offsetMin, offsetMax, name));
                }

                public CUI.Element WithRect(
                    string anchorMin = Defaults.VectorZero,
                    string anchorMax = Defaults.VectorOne,
                    string offsetMin = Defaults.VectorZero,
                    string offsetMax = Defaults.VectorZero)
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

                public CUI.Element WithFade(
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

                public CUI.Element WithComponents(params ICuiComponent[] components)
                {
                    AddComponents(components);
                    return this;
                }

                public CUI.Element CreateChild(string name = null, params ICuiComponent[] components)
                {
                    return CUI.Element.Create(name, components).AssignParent(this);
                }

                public static CUI.Element Create(string name = null, params ICuiComponent[] components)
                {
                    return new CUI.Element()
                    {
                        Name = name
                    }.WithComponents(components);
                }

                public class ComponentList : List<ICuiComponent>
                {
                    private Dictionary<Type, ICuiComponent> typeToComponent = new Dictionary<Type, ICuiComponent>();

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
                        string color = Defaults.Color,
                        string sprite = Defaults.Sprite,
                        string material = Defaults.Material,
                        Image.Type imageType = Defaults.ImageType,
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
                        string color = Defaults.Color,
                        string sprite = Defaults.Sprite,
                        string material = Defaults.IconMaterial)
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
                        string color = Defaults.Color,
                        string sprite = Defaults.Sprite,
                        string material = Defaults.Material,
                        Image.Type imageType = Defaults.ImageType)
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
                        string color = Defaults.Color,
                        CUI.Font font = Defaults.Font,
                        int fontSize = Defaults.FontSize,
                        TextAnchor align = Defaults.Align,
                        VerticalWrapMode overflow = Defaults.VerticalOverflow)
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
                        string color = Defaults.Color,
                        CUI.Font font = Defaults.Font,
                        int fontSize = Defaults.FontSize,
                        TextAnchor align = Defaults.Align,
                        InputField.LineType lineType = Defaults.LineType,
                        CUI.InputType inputType = CUI.InputType.Default,
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
                        CuiScrollbar horizonalScrollbar = null,
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
                            HorizontalScrollbar = horizonalScrollbar,
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
                        string color = Defaults.OutlineColor,
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
                public static CUI.Element CreateText(
                 string text,
                 string color = Defaults.Color,
                 CUI.Font font = Defaults.Font,
                 int fontSize = Defaults.FontSize,
                 TextAnchor align = Defaults.Align,
                 VerticalWrapMode overflow = Defaults.VerticalOverflow,
                 string anchorMin = Defaults.VectorZero,
                 string anchorMax = Defaults.VectorOne,
                 string offsetMin = Defaults.VectorZero,
                 string offsetMax = Defaults.VectorZero,
                 string name = null)
                {
                    CUI.Element element = CreateContainer(anchorMin, anchorMax, offsetMin, offsetMax, name);
                    element.Components.AddText(text, color, font, fontSize, align, overflow);
                    return element;
                }

                public static CUI.Element CreateOutlinedText(
                   string text,
                   string color = Defaults.Color,
                   CUI.Font font = Defaults.Font,
                   int fontSize = Defaults.FontSize,
                   TextAnchor align = Defaults.Align,
                   VerticalWrapMode overflow = Defaults.VerticalOverflow,
                   string outlineColor = Defaults.OutlineColor,
                   int outlineWidth = 1,
                   string anchorMin = Defaults.VectorZero,
                   string anchorMax = Defaults.VectorOne,
                   string offsetMin = Defaults.VectorZero,
                   string offsetMax = Defaults.VectorZero,
                   string name = null)
                {
                    CUI.Element element = CreateText(text, color, font, fontSize, align, overflow, anchorMin, anchorMax, offsetMin, offsetMax, name);
                    element.Components.AddOutline(outlineColor, outlineWidth);
                    return element;
                }

                public static CUI.Element CreateInputfield(
                      string command = null,
                      string text = "",
                      string color = Defaults.Color,
                      CUI.Font font = Defaults.Font,
                      int fontSize = Defaults.FontSize,
                      TextAnchor align = Defaults.Align,
                      InputField.LineType lineType = Defaults.LineType,
                      CUI.InputType inputType = CUI.InputType.Default,
                      bool @readonly = false,
                      bool autoFocus = false,
                      bool isPassword = false,
                      int charsLimit = 0,
                      string anchorMin = Defaults.VectorZero,
                      string anchorMax = Defaults.VectorOne,
                      string offsetMin = Defaults.VectorZero,
                      string offsetMax = Defaults.VectorZero,
                      string name = null)
                {
                    CUI.Element element = CreateContainer(anchorMin, anchorMax, offsetMin, offsetMax, name);
                    element.Components.AddInputfield(command, text, color, font, fontSize, align, lineType, inputType, @readonly, autoFocus, isPassword, charsLimit);
                    return element;
                }

                public static CUI.Element CreateButton(
                        string command = null,
                        string close = null,
                        string color = Defaults.Color,
                        string sprite = Defaults.Sprite,
                        string material = Defaults.Material,
                        Image.Type imageType = Defaults.ImageType,
                        string anchorMin = Defaults.VectorZero,
                        string anchorMax = Defaults.VectorOne,
                        string offsetMin = Defaults.VectorZero,
                        string offsetMax = Defaults.VectorZero,
                        string name = null)
                {
                    CUI.Element element = CreateContainer(anchorMin, anchorMax, offsetMin, offsetMax, name);
                    element.Components.AddButton(command, close, color, sprite, material, imageType);
                    return element;
                }

                public static CUI.Element CreatePanel(
                        string color = Defaults.Color,
                        string sprite = Defaults.Sprite,
                        string material = Defaults.Material,
                        Image.Type imageType = Defaults.ImageType,
                        string anchorMin = Defaults.VectorZero,
                        string anchorMax = Defaults.VectorOne,
                        string offsetMin = Defaults.VectorZero,
                        string offsetMax = Defaults.VectorZero,
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

                public static CUI.Element CreateImage(
                    string content,
                    string color = Defaults.Color,
                    string material = null,
                    string anchorMin = Defaults.VectorZero,
                    string anchorMax = Defaults.VectorOne,
                    string offsetMin = Defaults.VectorZero,
                    string offsetMax = Defaults.VectorZero,
                    string name = null)
                {
                    Element element = CreateContainer(anchorMin, anchorMax, offsetMin, offsetMax, name);
                    element.Components.AddRawImage(content, color, material: material);
                    return element;
                }

                public static CUI.Element CreateIcon(
                        int itemId,
                        ulong skin = 0,
                        string color = Defaults.Color,
                        string sprite = Defaults.Sprite,
                        string material = Defaults.IconMaterial,
                        Image.Type imageType = Defaults.ImageType,
                        string anchorMin = Defaults.VectorZero,
                        string anchorMax = Defaults.VectorOne,
                        string offsetMin = Defaults.VectorZero,
                        string offsetMax = Defaults.VectorZero,
                        string name = null)
                {
                    Element element = CreateContainer(anchorMin, anchorMax, offsetMin, offsetMax, name);
                    element.Components.AddImage(color, sprite, material, imageType, itemId, skin);
                    return element;
                }

                public static Element CreateContainer(
                       string anchorMin = Defaults.VectorZero,
                       string anchorMax = Defaults.VectorOne,
                       string offsetMin = Defaults.VectorZero,
                       string offsetMax = Defaults.VectorZero,
                       string name = null)
                {
                    return Element.Create(name).WithRect(anchorMin, anchorMax, offsetMin, offsetMax);
                }
            }


            public class Root : Element
            {
                public bool wasRendered = false;
                private static StringBuilder stringBuilder = new StringBuilder();

                public Root()
                {
                    Name = string.Empty;
                }

                public Root(string rootObjectName = "Overlay")
                {
                    Name = rootObjectName;
                }

                public override List<Element> Container { get; } = new List<Element>();

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
                    CUI.AddUI(connection, ToJson(Container));
                }

                public void Render(BasePlayer player)
                {
                    Render(player.Connection);
                }

                public void Update(Connection connection)
                {
                    foreach (Element element in Container)
                        element.Update = true;
                    CUI.AddUI(connection, ToJson(Container));
                }

                public void Update(BasePlayer player)
                {
                    Update(player.Connection);
                }

            }
        }
    }
    #endregion
}