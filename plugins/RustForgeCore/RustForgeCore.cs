// RustForgeCore — единый Core-плагин платформы RustForge Cloud.
// Совместим с Oxide (uMod) и Carbon (Carbon исполняет Oxide-плагины без изменений).
//
// Установка: положите этот файл в папку плагинов (oxide/plugins или carbon/plugins),
// затем в конфиге (oxide/config/RustForgeCore.json) укажите ApiUrl и ConnectionKey
// из веб-панели RustForge Cloud. Подробнее — README.md рядом с этим файлом.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Oxide.Core;
using Oxide.Core.Libraries;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("RustForgeCore", "RustForge Cloud", "1.0.2")]
    [Description("Core-плагин RustForge Cloud: синхронизация модулей, статистика и события")]
    public class RustForgeCore : RustPlugin
    {
        #region Конфигурация плагина

        private PluginSettings _settings;

        private class PluginSettings
        {
            [JsonProperty("ApiUrl (адрес платформы, например https://your-app.replit.app/api)")]
            public string ApiUrl = "https://YOUR-APP.replit.app/api";

            [JsonProperty("ConnectionKey (ключ подключения из панели RustForge Cloud)")]
            public string ConnectionKey = "";

            [JsonProperty("HeartbeatSeconds (период heartbeat)")]
            public int HeartbeatSeconds = 30;
        }

        protected override void LoadDefaultConfig() => _settings = new PluginSettings();

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try { _settings = Config.ReadObject<PluginSettings>() ?? new PluginSettings(); }
            catch { _settings = new PluginSettings(); }
            SaveConfig();
        }

        protected override void SaveConfig() => Config.WriteObject(_settings);

        #endregion

        #region Состояние

        private int _configVersion = -1;
        private bool _connected;
        private DateTime? _lastHeartbeatUtc;
        private readonly List<JObject> _eventQueue = new List<JObject>();
        private readonly Dictionary<string, GameModule> _modules = new Dictionary<string, GameModule>();

        #endregion

        #region Жизненный цикл

        private void OnServerInitialized()
        {
            // Реестр игровых модулей. Новый модуль = один класс + одна строка здесь.
            RegisterModule(new EconomyModule(this));
            RegisterModule(new ClansModule(this));
            RegisterModule(new ShopModule(this));
            RegisterModule(new TeleportModule(this));
            RegisterModule(new QuestsModule(this));
            RegisterModule(new NpcModule(this));
            RegisterModule(new EventsModule(this));

            if (string.IsNullOrEmpty(_settings.ConnectionKey))
            {
                PrintError("ConnectionKey не задан. Откройте oxide/config/RustForgeCore.json, вставьте ключ из панели RustForge Cloud и выполните: oxide.reload RustForgeCore");
                return;
            }

            Handshake();
            timer.Every(Math.Max(10, _settings.HeartbeatSeconds), SendHeartbeat);
            timer.Every(15f, FlushEvents);
            timer.Every(30f, FlushDirtyData);
        }

        private void Unload()
        {
            foreach (var m in _modules.Values)
                if (m.Enabled) m.OnDisabled();
            FlushDirtyData();
            FlushEvents();
        }

        private void RegisterModule(GameModule module) => _modules[module.Id] = module;

        #endregion

        #region Связь с платформой

        private void Api(string path, JObject body, Action<int, string> callback)
        {
            var headers = new Dictionary<string, string> { ["Content-Type"] = "application/json" };
            webrequest.Enqueue(
                _settings.ApiUrl.TrimEnd('/') + path,
                body.ToString(Formatting.None),
                (code, response) => callback?.Invoke(code, response),
                this, RequestMethod.POST, headers, 15f);
        }

        private void Handshake()
        {
            var body = new JObject
            {
                ["connectionKey"] = _settings.ConnectionKey,
                ["gameVersion"] = Facepunch.BuildInfo.Current?.Build?.Number.ToString() ?? Rust.Protocol.printable,
                ["frameworkVersion"] = FrameworkName(),
                ["maxPlayers"] = ConVar.Server.maxplayers,
            };
            Api("/agent/handshake", body, (code, response) =>
            {
                if (code != 200)
                {
                    PrintError($"Handshake не удался (HTTP {code}). Проверьте ApiUrl и ConnectionKey. Повтор через 60 сек.");
                    timer.Once(60f, Handshake);
                    return;
                }
                JObject cfg;
                try { cfg = JObject.Parse(response); }
                catch (Exception e) { PrintError($"Некорректный ответ handshake: {e.Message}. Повтор через 60 сек."); timer.Once(60f, Handshake); return; }
                _connected = true;
                Puts($"Подключено к RustForge Cloud: сервер \"{cfg["serverName"]}\" (id={cfg["serverId"]})");
                ApplyConfig(cfg);
            });
        }

        private static string FrameworkName()
        {
#if CARBON
            return "Carbon";
#else
            return "Oxide " + OxideMod.Version;
#endif
        }

        private void SendHeartbeat()
        {
            if (!_connected) return;
            var players = new JArray();
            foreach (var p in BasePlayer.activePlayerList)
                players.Add(PlayerState(p, true));
            foreach (var p in BasePlayer.sleepingPlayerList.Take(200))
                players.Add(PlayerState(p, false));

            int fps = 0;
            long memMb = 0;
            try { fps = (int)Performance.report.frameRate; } catch { }
            try { memMb = (long)Performance.report.memoryUsageSystem; } catch { }
            var body = new JObject
            {
                ["connectionKey"] = _settings.ConnectionKey,
                ["playersOnline"] = BasePlayer.activePlayerList.Count,
                ["fps"] = fps,
                ["memoryMb"] = memMb,
                ["players"] = players,
            };
            Api("/agent/heartbeat", body, (code, response) =>
            {
                if (code != 200) { _connected = false; timer.Once(30f, Handshake); return; }
                _lastHeartbeatUtc = DateTime.UtcNow;
                try
                {
                    var ack = JObject.Parse(response);
                    var version = ack.Value<int>("configVersion");
                    if (version != _configVersion) PullConfig();
                }
                catch (Exception e) { PrintWarning($"Некорректный ответ heartbeat: {e.Message}"); }
            });
        }

        private JObject PlayerState(BasePlayer p, bool online)
        {
            var economy = GetModule<EconomyModule>("economy");
            var clans = GetModule<ClansModule>("clans");
            return new JObject
            {
                ["steamId"] = p.UserIDString,
                ["name"] = p.displayName,
                ["online"] = online,
                ["kills"] = GetStat(p.userID, "kills"),
                ["deaths"] = GetStat(p.userID, "deaths"),
                ["balance"] = economy?.GetBalance(p.userID) ?? 0,
                ["clanName"] = clans?.GetClanName(p.userID),
                ["playtimeMinutes"] = GetStat(p.userID, "playtime"),
            };
        }

        private void PullConfig()
        {
            Api("/agent/config", new JObject { ["connectionKey"] = _settings.ConnectionKey },
                (code, response) =>
                {
                    if (code != 200) return;
                    try { ApplyConfig(JObject.Parse(response)); }
                    catch (Exception e) { PrintWarning($"Некорректный ответ config: {e.Message}"); }
                });
        }

        /// Применяет конфиг платформы: включает/выключает модули и обновляет их настройки на лету.
        private void ApplyConfig(JObject cfg)
        {
            _configVersion = cfg.Value<int>("configVersion");
            foreach (var entry in (JArray)cfg["modules"])
            {
                var moduleId = entry.Value<string>("moduleId");
                if (!_modules.TryGetValue(moduleId, out var module)) continue;
                var enabled = entry.Value<bool>("enabled");
                module.Config = (JObject)entry["config"] ?? new JObject();
                if (enabled && !module.Enabled) { module.Enabled = true; module.OnEnabled(); Puts($"Модуль включён: {moduleId}"); }
                else if (!enabled && module.Enabled) { module.Enabled = false; module.OnDisabled(); Puts($"Модуль выключен: {moduleId}"); }
                else if (enabled) module.OnConfigUpdated();
            }
            Puts($"Конфиг применён (версия {_configVersion}). Активные модули: {string.Join(", ", _modules.Values.Where(m => m.Enabled).Select(m => m.Id))}");
        }

        public void PushEvent(string category, string message, JObject meta = null)
        {
            _eventQueue.Add(new JObject
            {
                ["category"] = category,
                ["message"] = message,
                ["meta"] = meta,
                ["occurredAt"] = DateTime.UtcNow.ToString("o"),
            });
            if (_eventQueue.Count >= 25) FlushEvents();
        }

        private void FlushEvents()
        {
            if (!_connected || _eventQueue.Count == 0) return;
            var count = Math.Min(50, _eventQueue.Count);
            var batch = new JArray(_eventQueue.Take(count));
            Api("/agent/events", new JObject
            {
                ["connectionKey"] = _settings.ConnectionKey,
                ["events"] = batch,
            }, (code, response) =>
            {
                // Удаляем из очереди только после успешной доставки; иначе повторим позже.
                if (code == 200)
                    _eventQueue.RemoveRange(0, Math.Min(count, _eventQueue.Count));
                else if (_eventQueue.Count > 500)
                    _eventQueue.RemoveRange(0, _eventQueue.Count - 500); // защита от разрастания
            });
        }

        #endregion

        #region Крэш-безопасная запись данных (temp + rename, .bak-фолбэк)

        // Путь к файлу данных: oxide/data/<name>.json (тот же формат, что у DataFileSystem).
        private static string DataFilePath(string name) =>
            Path.Combine(Interface.Oxide.DataDirectory, name.Replace('/', Path.DirectorySeparatorChar) + ".json");

        /// Атомарная запись: пишем во временный файл, затем подменяем оригинал (rename).
        /// Предыдущая версия сохраняется как .bak. Даже при hard-crash посреди записи
        /// оригинал или .bak остаются целыми.
        public static void SafeWriteData<T>(string name, T data)
        {
            var path = DataFilePath(name);
            var tmp = path + ".tmp";
            var bak = path + ".bak";
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var json = JsonConvert.SerializeObject(data, Formatting.Indented);
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var sw = new StreamWriter(fs))
            {
                sw.Write(json);
                sw.Flush();
                fs.Flush(true); // fsync: данные реально на диске до rename
            }
            if (File.Exists(path))
            {
                try { File.Replace(tmp, path, bak); }
                catch (PlatformNotSupportedException)
                {
                    // На случай ФС без атомарного Replace — максимально близкий аналог.
                    File.Copy(path, bak, true);
                    File.Delete(path);
                    File.Move(tmp, path);
                }
            }
            else
            {
                File.Move(tmp, path);
            }
        }

        /// Чтение с фолбэком: если основной файл повреждён/обрезан — читаем .bak,
        /// вместо того чтобы молча начать с пустых данных.
        public static T SafeReadData<T>(string name) where T : class
        {
            var path = DataFilePath(name);
            var result = TryReadJson<T>(path, out var primaryBroken);
            if (result != null) return result;
            var bak = path + ".bak";
            if (File.Exists(bak))
            {
                var fromBak = TryReadJson<T>(bak, out _);
                if (fromBak != null)
                {
                    if (primaryBroken)
                        Interface.Oxide.LogWarning($"[RustForgeCore] Файл данных {name}.json повреждён — восстановлено из резервной копии {name}.json.bak");
                    return fromBak;
                }
            }
            if (primaryBroken)
                Interface.Oxide.LogWarning($"[RustForgeCore] Файл данных {name}.json повреждён и резервная копия недоступна — данные начаты заново");
            return null;
        }

        private static T TryReadJson<T>(string path, out bool broken) where T : class
        {
            broken = false;
            if (!File.Exists(path)) return null;
            try
            {
                var json = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(json)) { broken = true; return null; }
                var obj = JsonConvert.DeserializeObject<T>(json);
                if (obj == null) broken = true;
                return obj;
            }
            catch (Exception)
            {
                broken = true;
                return null;
            }
        }

        #endregion

        #region Статистика игроков (kills/deaths/playtime)

        private Dictionary<string, Dictionary<string, long>> _stats;

        private Dictionary<string, long> Stats(ulong userId)
        {
            if (_stats == null)
                _stats = SafeReadData<Dictionary<string, Dictionary<string, long>>>("RustForgeCore/stats")
                         ?? new Dictionary<string, Dictionary<string, long>>();
            var key = userId.ToString();
            if (!_stats.TryGetValue(key, out var s)) { s = new Dictionary<string, long>(); _stats[key] = s; }
            return s;
        }

        private long GetStat(ulong userId, string stat) => Stats(userId).TryGetValue(stat, out var v) ? v : 0;

        public void AddStat(ulong userId, string stat, long delta)
        {
            var s = Stats(userId);
            s[stat] = (s.TryGetValue(stat, out var v) ? v : 0) + delta;
            _statsDirty = true; // запись на диск — пакетно, в FlushDirtyData
        }

        private bool _statsDirty;

        /// Пакетная запись накопленных изменений (статистика + экономика).
        /// Вызывается по таймеру и при выгрузке плагина, чтобы не писать на диск на каждый килл.
        private void FlushDirtyData()
        {
            if (_statsDirty && _stats != null)
            {
                SafeWriteData("RustForgeCore/stats", _stats);
                _statsDirty = false;
            }
            GetModule<EconomyModule>("economy")?.Flush();
            GetModule<ClansModule>("clans")?.Flush();
            GetModule<TeleportModule>("teleport")?.Flush();
            GetModule<QuestsModule>("quests")?.Flush();
        }

        #endregion

        #region Маршрутизация игровых хуков в модули

        private T GetModule<T>(string id) where T : GameModule =>
            _modules.TryGetValue(id, out var m) && m.Enabled ? m as T : null;

        private IEnumerable<GameModule> ActiveModules() => _modules.Values.Where(m => m.Enabled);

        private void OnPlayerConnected(BasePlayer player)
        {
            PushEvent("player", "log.playerConnected", new JObject { ["player"] = player.displayName, ["steamId"] = player.UserIDString });
            foreach (var m in ActiveModules()) m.OnPlayerConnected(player);
        }

        private void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            PushEvent("player", "log.playerDisconnected", new JObject { ["player"] = player.displayName });
            foreach (var m in ActiveModules()) m.OnPlayerDisconnected(player);
        }

        private void OnPlayerDeath(BasePlayer victim, HitInfo info)
        {
            if (victim == null || victim.IsNpc) return;
            AddStat(victim.userID, "deaths", 1);
            var attacker = info?.InitiatorPlayer;
            if (attacker != null && !attacker.IsNpc && attacker.userID != victim.userID)
            {
                AddStat(attacker.userID, "kills", 1);
                PushEvent("player", "log.playerKill", new JObject { ["attacker"] = attacker.displayName, ["victim"] = victim.displayName });
            }
            foreach (var m in ActiveModules()) m.OnPlayerDeathHook(victim, attacker);
        }

        private object OnEntityTakeDamage(BasePlayer victim, HitInfo info)
        {
            foreach (var m in ActiveModules())
            {
                var result = m.OnPlayerDamage(victim, info);
                if (result != null) return result;
            }
            return null;
        }

        // Дополнительная страховка: сбрасываем накопленные изменения на диск
        // в момент world-save Rust (сервер и так пишет на диск в это время).
        private void OnServerSave() => FlushDirtyData();

        private void Init()
        {
            permission.RegisterPermission("rustforgecore.vip", this);
            permission.RegisterPermission("rustforgecore.admin", this);
            timer.Every(60f, () =>
            {
                foreach (var p in BasePlayer.activePlayerList) AddStat(p.userID, "playtime", 1);
            });
        }

        #endregion

        #region Чат-команды (делегируются модулям)

        [ChatCommand("balance")]
        private void CmdBalance(BasePlayer p, string cmd, string[] args) => GetModule<EconomyModule>("economy")?.CmdBalance(p);

        [ChatCommand("pay")]
        private void CmdPay(BasePlayer p, string cmd, string[] args) => GetModule<EconomyModule>("economy")?.CmdPay(p, args);

        [ChatCommand("baltop")]
        private void CmdBaltop(BasePlayer p, string cmd, string[] args) => GetModule<EconomyModule>("economy")?.CmdBaltop(p);

        [ChatCommand("clan")]
        private void CmdClan(BasePlayer p, string cmd, string[] args) => GetModule<ClansModule>("clans")?.CmdClan(p, args);

        [ChatCommand("shop")]
        private void CmdShop(BasePlayer p, string cmd, string[] args) => GetModule<ShopModule>("shop")?.CmdShop(p, args);

        [ChatCommand("sethome")]
        private void CmdSetHome(BasePlayer p, string cmd, string[] args) => GetModule<TeleportModule>("teleport")?.CmdSetHome(p, args);

        [ChatCommand("home")]
        private void CmdHome(BasePlayer p, string cmd, string[] args) => GetModule<TeleportModule>("teleport")?.CmdHome(p, args);

        [ChatCommand("quests")]
        private void CmdQuests(BasePlayer p, string cmd, string[] args) => GetModule<QuestsModule>("quests")?.CmdQuests(p);

        [ChatCommand("npc")]
        private void CmdNpc(BasePlayer p, string cmd, string[] args) => GetModule<NpcModule>("npc")?.CmdNpc(p, args);

        #endregion

        #region Админ-команды (/rfstatus, /rfreload)

        private bool IsAdmin(BasePlayer p) =>
            p != null && (p.IsAdmin || permission.UserHasPermission(p.UserIDString, "rustforgecore.admin"));

        private string StatusText()
        {
            var lastHb = _lastHeartbeatUtc.HasValue
                ? $"{(int)(DateTime.UtcNow - _lastHeartbeatUtc.Value).TotalSeconds} сек назад ({_lastHeartbeatUtc.Value:HH:mm:ss} UTC)"
                : "ещё не было";
            var active = _modules.Values.Where(m => m.Enabled).Select(m => m.Id).ToList();
            return "Статус RustForge Cloud:\n" +
                   $"- Подключение: {(_connected ? "подключено" : "оффлайн")}\n" +
                   $"- Версия конфига: {(_configVersion >= 0 ? _configVersion.ToString() : "не получена")}\n" +
                   $"- Активные модули: {(active.Count > 0 ? string.Join(", ", active) : "нет")}\n" +
                   $"- Последний heartbeat: {lastHb}";
        }

        private void ForceReload()
        {
            Puts("Принудительное переподключение: handshake + запрос конфига...");
            _connected = false;
            Handshake();
        }

        [ChatCommand("rfstatus")]
        private void CmdRfStatus(BasePlayer p, string cmd, string[] args)
        {
            if (!IsAdmin(p)) { p.ChatMessage("<color=#e8590c>[RustForge]</color> Недостаточно прав (rustforgecore.admin)."); return; }
            p.ChatMessage($"<color=#e8590c>[RustForge]</color> {StatusText()}");
        }

        [ChatCommand("rfreload")]
        private void CmdRfReload(BasePlayer p, string cmd, string[] args)
        {
            if (!IsAdmin(p)) { p.ChatMessage("<color=#e8590c>[RustForge]</color> Недостаточно прав (rustforgecore.admin)."); return; }
            if (string.IsNullOrEmpty(_settings.ConnectionKey)) { p.ChatMessage("<color=#e8590c>[RustForge]</color> ConnectionKey не задан — заполните конфиг плагина."); return; }
            ForceReload();
            p.ChatMessage("<color=#e8590c>[RustForge]</color> Переподключение запущено: handshake и обновление конфига. Проверьте /rfstatus через несколько секунд.");
        }

        [ConsoleCommand("rfstatus")]
        private void ConsoleRfStatus(ConsoleSystem.Arg arg)
        {
            var p = arg.Player();
            if (p != null && !IsAdmin(p)) { arg.ReplyWith("Недостаточно прав (rustforgecore.admin)."); return; }
            arg.ReplyWith(StatusText());
        }

        [ConsoleCommand("rfreload")]
        private void ConsoleRfReload(ConsoleSystem.Arg arg)
        {
            var p = arg.Player();
            if (p != null && !IsAdmin(p)) { arg.ReplyWith("Недостаточно прав (rustforgecore.admin)."); return; }
            if (string.IsNullOrEmpty(_settings.ConnectionKey)) { arg.ReplyWith("ConnectionKey не задан — заполните конфиг плагина."); return; }
            ForceReload();
            arg.ReplyWith("Переподключение запущено: handshake и обновление конфига.");
        }

        #endregion

        #region Базовый класс игрового модуля

        /// Общий интерфейс игрового модуля. Каждый модуль — отдельный класс.
        public abstract class GameModule
        {
            protected readonly RustForgeCore Plugin;
            public string Id { get; }
            public bool Enabled;
            public JObject Config = new JObject();

            protected GameModule(RustForgeCore plugin, string id) { Plugin = plugin; Id = id; }

            public virtual void OnEnabled() { }
            public virtual void OnDisabled() { }
            public virtual void OnConfigUpdated() { }
            public virtual void OnPlayerConnected(BasePlayer p) { }
            public virtual void OnPlayerDisconnected(BasePlayer p) { }
            public virtual void OnPlayerDeathHook(BasePlayer victim, BasePlayer attacker) { }
            public virtual object OnPlayerDamage(BasePlayer victim, HitInfo info) => null;

            protected int GetInt(string key, int def) => Config.Value<int?>(key) ?? def;
            protected double GetDouble(string key, double def) => Config.Value<double?>(key) ?? def;
            protected bool GetBool(string key, bool def) => Config.Value<bool?>(key) ?? def;
            protected string GetString(string key, string def) => Config.Value<string>(key) ?? def;

            protected void Reply(BasePlayer p, string message) => p.ChatMessage($"<color=#e8590c>[RustForge]</color> {message}");

            // Крэш-безопасные чтение/запись: temp+rename с .bak-фолбэком (см. SafeWriteData/SafeReadData).
            protected T LoadData<T>(string name) where T : class, new() =>
                SafeReadData<T>($"RustForgeCore/{name}") ?? new T();

            protected void SaveData<T>(string name, T data) =>
                SafeWriteData($"RustForgeCore/{name}", data);
        }

        #endregion

        #region Модуль: Экономика

        public class EconomyModule : GameModule
        {
            private Dictionary<string, double> _balances;

            // Last-seen timestamps: used to prune economy entries for players who never return.
            // Stored in a separate data file so the existing balance format stays unchanged.
            private Dictionary<string, DateTime> _lastSeen;

            public EconomyModule(RustForgeCore plugin) : base(plugin, "economy") { }

            private bool _dirty;
            private bool _lastSeenDirty;
            private Timer _pruneTimer;

            public override void OnEnabled()
            {
                _balances = LoadData<Dictionary<string, double>>("economy");
                _lastSeen = LoadData<Dictionary<string, DateTime>>("economy_lastseen");

                // Периодическая очистка балансов игроков, которые давно не заходили.
                // Помечаем dirty только если что-то было реально удалено.
                _pruneTimer = Plugin.timer.Every(600f, () =>
                {
                    if (PruneEconomy()) _dirty = true;
                });
            }

            // Save() лишь помечает данные как изменённые; фактическая запись — пакетно в Flush().
            private void Save() => _dirty = true;

            public void Flush()
            {
                if (_lastSeenDirty && _lastSeen != null)
                {
                    PruneEconomy();
                    SaveData("economy_lastseen", _lastSeen);
                    _lastSeenDirty = false;
                }
                if (!_dirty || _balances == null) return;
                PruneEconomy();
                SaveData("economy", _balances);
                _dirty = false;
            }

            // Удаляет записи игроков, которые не заходили дольше порога (по умолчанию 30 дней).
            // Порог можно изменить в конфиге модуля (поле "pruneAfterDays").
            // Возвращает true, если хотя бы одна запись была удалена.
            private bool PruneEconomy()
            {
                if (_balances == null || _lastSeen == null) return false;
                var threshold = GetInt("pruneAfterDays", 30);
                var cutoff = DateTime.UtcNow.AddDays(-threshold);
                // Pruning is safe: if the player reconnects, OnPlayerConnected re-creates their entry.
                var stale = _lastSeen
                    .Where(kv => kv.Value < cutoff)
                    .Select(kv => kv.Key)
                    .ToList();
                foreach (var key in stale)
                {
                    _balances.Remove(key);
                    _lastSeen.Remove(key);
                }
                return stale.Count > 0;
            }

            public override void OnDisabled()
            {
                _pruneTimer?.Destroy();
                _pruneTimer = null;
                Flush();
            }

            public double GetBalance(ulong userId)
            {
                if (_balances == null) return 0;
                return _balances.TryGetValue(userId.ToString(), out var v) ? v : GetInt("startBalance", 500);
            }

            public bool Charge(BasePlayer p, double amount)
            {
                var bal = GetBalance(p.userID);
                if (bal < amount) { Reply(p, $"Недостаточно {GetString("currencyName", "RP")}: нужно {amount}, у вас {bal:0}."); return false; }
                _balances[p.userID.ToString()] = bal - amount; Save();
                return true;
            }

            public void Deposit(ulong userId, double amount)
            {
                _balances[userId.ToString()] = GetBalance(userId) + amount; Save();
            }

            public override void OnPlayerConnected(BasePlayer p)
            {
                if (!_balances.ContainsKey(p.UserIDString))
                {
                    _balances[p.UserIDString] = GetInt("startBalance", 500); Save();
                }
                // Record last-seen so the prune pass can age out inactive players.
                if (_lastSeen != null)
                {
                    _lastSeen[p.UserIDString] = DateTime.UtcNow;
                    _lastSeenDirty = true;
                }
            }

            public override void OnPlayerDeathHook(BasePlayer victim, BasePlayer attacker)
            {
                var penalty = GetInt("deathPenalty", 25);
                if (penalty > 0) { _balances[victim.UserIDString] = Math.Max(0, GetBalance(victim.userID) - penalty); Save(); }
                if (attacker != null)
                {
                    var reward = GetInt("killReward", 50);
                    if (reward > 0)
                    {
                        Deposit(attacker.userID, reward);
                        Reply(attacker, $"+{reward} {GetString("currencyName", "RP")} за убийство {victim.displayName}.");
                    }
                }
            }

            public void CmdBalance(BasePlayer p) =>
                Reply(p, $"Баланс: {GetBalance(p.userID):0} {GetString("currencyName", "RP")}");

            public void CmdPay(BasePlayer p, string[] args)
            {
                if (args.Length < 2 || !double.TryParse(args[1], out var amount) || amount <= 0)
                { Reply(p, "Использование: /pay <имя игрока> <сумма>"); return; }
                var target = BasePlayer.activePlayerList.FirstOrDefault(x =>
                    x.displayName.IndexOf(args[0], StringComparison.OrdinalIgnoreCase) >= 0);
                if (target == null || target == p) { Reply(p, "Игрок не найден."); return; }
                var commission = amount * GetDouble("transferCommission", 2) / 100.0;
                if (!Charge(p, amount + commission)) return;
                Deposit(target.userID, amount);
                var cur = GetString("currencyName", "RP");
                Reply(p, $"Переведено {amount:0} {cur} игроку {target.displayName} (комиссия {commission:0.#}).");
                Reply(target, $"Вам перевели {amount:0} {cur} от {p.displayName}.");
                Plugin.PushEvent("module", "log.economyTransfer", new JObject { ["from"] = p.displayName, ["to"] = target.displayName, ["amount"] = amount });
            }

            public void CmdBaltop(BasePlayer p)
            {
                if (!GetBool("enableTopList", true)) { Reply(p, "Топ игроков отключён."); return; }
                var top = _balances.OrderByDescending(kv => kv.Value).Take(5).ToList();
                var cur = GetString("currencyName", "RP");
                Reply(p, "Топ балансов:\n" + string.Join("\n", top.Select((kv, i) =>
                {
                    var pl = Plugin.covalence.Players.FindPlayerById(kv.Key);
                    return $"{i + 1}. {(pl != null ? pl.Name : kv.Key)} — {kv.Value:0} {cur}";
                })));
            }
        }

        #endregion

        #region Модуль: Кланы

        public class ClansModule : GameModule
        {
            public class Clan
            {
                public string Name;
                public string Owner;
                public List<string> Members = new List<string>();
            }

            private Dictionary<string, Clan> _clans; // имя клана -> клан
            public ClansModule(RustForgeCore plugin) : base(plugin, "clans") { }

            private bool _dirty;

            public override void OnEnabled() => _clans = LoadData<Dictionary<string, Clan>>("clans");

            // Save() лишь помечает данные как изменённые; фактическая запись — пакетно в Flush().
            private void Save() => _dirty = true;

            public void Flush()
            {
                if (!_dirty || _clans == null) return;
                SaveData("clans", _clans);
                _dirty = false;
            }

            public override void OnDisabled() => Flush();

            public string GetClanName(ulong userId) =>
                _clans?.Values.FirstOrDefault(c => c.Members.Contains(userId.ToString()))?.Name;

            private Clan ClanOf(BasePlayer p) =>
                _clans.Values.FirstOrDefault(c => c.Members.Contains(p.UserIDString));

            public override object OnPlayerDamage(BasePlayer victim, HitInfo info)
            {
                if (GetBool("friendlyFire", false)) return null;
                var attacker = info?.InitiatorPlayer;
                if (attacker == null || victim == null || attacker.IsNpc || victim.IsNpc) return null;
                var clanA = GetClanName(attacker.userID);
                if (clanA != null && clanA == GetClanName(victim.userID))
                {
                    Reply(attacker, "Урон по соклановцам отключён.");
                    return true; // блокируем урон
                }
                return null;
            }

            public void CmdClan(BasePlayer p, string[] args)
            {
                if (args.Length == 0)
                {
                    var clan = ClanOf(p);
                    Reply(p, clan == null
                        ? "Вы не в клане. /clan create <имя> — создать, /clan join <имя> — вступить, /clan leave — выйти."
                        : $"Клан: {clan.Name}. Участников: {clan.Members.Count}/{GetInt("maxMembers", 8)}.");
                    return;
                }
                switch (args[0].ToLower())
                {
                    case "create":
                        if (args.Length < 2) { Reply(p, "Использование: /clan create <имя>"); return; }
                        if (ClanOf(p) != null) { Reply(p, "Вы уже в клане."); return; }
                        var name = args[1].ToUpper();
                        if (_clans.ContainsKey(name)) { Reply(p, "Клан с таким именем уже существует."); return; }
                        var cost = GetInt("creationCost", 1000);
                        var economy = Plugin.GetModule<EconomyModule>("economy");
                        if (cost > 0 && economy != null && !economy.Charge(p, cost)) return;
                        _clans[name] = new Clan { Name = name, Owner = p.UserIDString, Members = { p.UserIDString } };
                        Save();
                        Reply(p, $"Клан {name} создан.");
                        Plugin.PushEvent("module", "log.clanCreated", new JObject { ["clan"] = name, ["owner"] = p.displayName });
                        break;
                    case "join":
                        if (args.Length < 2 || !_clans.TryGetValue(args[1].ToUpper(), out var target)) { Reply(p, "Клан не найден."); return; }
                        if (ClanOf(p) != null) { Reply(p, "Сначала выйдите из текущего клана: /clan leave"); return; }
                        if (target.Members.Count >= GetInt("maxMembers", 8)) { Reply(p, "Клан заполнен."); return; }
                        target.Members.Add(p.UserIDString); Save();
                        Reply(p, $"Вы вступили в клан {target.Name}.");
                        break;
                    case "leave":
                        var mine = ClanOf(p);
                        if (mine == null) { Reply(p, "Вы не в клане."); return; }
                        mine.Members.Remove(p.UserIDString);
                        if (mine.Members.Count == 0) _clans.Remove(mine.Name);
                        Save();
                        Reply(p, $"Вы покинули клан {mine.Name}.");
                        break;
                    default:
                        Reply(p, "Команды: /clan create <имя>, /clan join <имя>, /clan leave");
                        break;
                }
            }
        }

        #endregion

        #region Модуль: Магазин

        public class ShopModule : GameModule
        {
            public class ShopItem
            {
                public string Name;
                public string Shortname;
                public int Amount;
                public int Price;
            }

            // Запасной ассортимент — используется, если панель ещё не прислала список товаров.
            private static readonly ShopItem[] FallbackItems =
            {
                new ShopItem { Name = "дерево", Shortname = "wood", Amount = 1000, Price = 100 },
                new ShopItem { Name = "камень", Shortname = "stones", Amount = 1000, Price = 150 },
                new ShopItem { Name = "металл", Shortname = "metal.fragments", Amount = 500, Price = 300 },
                new ShopItem { Name = "сера", Shortname = "sulfur", Amount = 500, Price = 400 },
                new ShopItem { Name = "ткань", Shortname = "cloth", Amount = 200, Price = 120 },
                new ShopItem { Name = "аптечка", Shortname = "syringe.medical", Amount = 3, Price = 250 },
            };

            public ShopModule(RustForgeCore plugin) : base(plugin, "shop") { }

            /// Ассортимент из конфига панели (поле items); при отсутствии — запасной список.
            private List<ShopItem> Items()
            {
                var result = new List<ShopItem>();
                if (Config["items"] is JArray arr)
                {
                    foreach (var token in arr)
                    {
                        if (!(token is JObject o)) continue;
                        var name = o.Value<string>("name");
                        var shortname = o.Value<string>("shortname");
                        var amount = o.Value<int?>("amount") ?? 0;
                        var price = o.Value<int?>("price") ?? -1;
                        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(shortname) || amount < 1 || price < 0) continue;
                        result.Add(new ShopItem { Name = name.Trim(), Shortname = shortname.Trim(), Amount = amount, Price = price });
                    }
                }
                return result.Count > 0 ? result : FallbackItems.ToList();
            }

            public void CmdShop(BasePlayer p, string[] args)
            {
                var economy = Plugin.GetModule<EconomyModule>("economy");
                if (economy == null) { Reply(p, "Магазин требует включённого модуля Экономика."); return; }
                var discount = p.IPlayer.HasPermission("rustforgecore.vip") ? GetInt("vipDiscount", 0) : 0;
                var items = Items();

                if (args.Length == 0)
                {
                    Reply(p, "Магазин (покупка: /shop buy <название>):\n" + string.Join("\n",
                        items.Select(i => $"• {i.Name} x{i.Amount} — {Price(i.Price, discount)}")));
                    return;
                }
                if (args[0].ToLower() == "buy" && args.Length >= 2)
                {
                    var query = string.Join(" ", args.Skip(1));
                    var item = items.FirstOrDefault(i => i.Name.Equals(query, StringComparison.OrdinalIgnoreCase));
                    if (item == null) { Reply(p, "Такого товара нет. /shop — список."); return; }
                    var def = ItemManager.FindItemDefinition(item.Shortname);
                    if (def == null) { Reply(p, $"Товар «{item.Name}» настроен неверно (предмет {item.Shortname} не найден). Сообщите администратору."); return; }
                    var price = Price(item.Price, discount);
                    if (!economy.Charge(p, price)) return;
                    var created = ItemManager.Create(def, item.Amount);
                    if (created == null)
                    {
                        economy.Deposit(p.userID, price);
                        Reply(p, "Ошибка создания предмета. Деньги возвращены.");
                        return;
                    }
                    if (!p.inventory.GiveItem(created))
                        created.Drop(p.transform.position + Vector3.up, Vector3.zero);
                    Reply(p, $"Куплено: {item.Name} x{item.Amount} за {price}.");
                    Plugin.PushEvent("module", "log.shopPurchase", new JObject { ["player"] = p.displayName, ["item"] = item.Shortname, ["price"] = price });
                }
            }

            private int Price(int basePrice, int discountPercent) =>
                (int)Math.Ceiling(basePrice * (100 - discountPercent) / 100.0);
        }

        #endregion

        #region Модуль: Телепорты

        public class TeleportModule : GameModule
        {
            private Dictionary<string, Dictionary<string, float[]>> _homes; // userId -> имя дома -> позиция

            // Состояние лимитов телепортов. Персистится в data-файл, чтобы
            // перезагрузка плагина не сбрасывала кулдауны, дневные лимиты
            // и raid-block (последнее время получения урона).
            private class TeleportState
            {
                public Dictionary<string, DateTime> Cooldowns = new Dictionary<string, DateTime>();
                public Dictionary<string, int> UsedToday = new Dictionary<string, int>();
                public string UsageDay = DateTime.UtcNow.ToString("yyyy-MM-dd");
                // Raid-block: ключ — строковый Steam-ID, значение — UTC-время последнего урона.
                // Записывается на диск, чтобы перезагрузка плагина не обнуляла таймер.
                public Dictionary<string, DateTime> LastDamage = new Dictionary<string, DateTime>();
            }
            private TeleportState _state = new TeleportState();

            private int UsedToday(ulong userId)
            {
                var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
                if (today != _state.UsageDay)
                {
                    _state.UsageDay = today;
                    _state.UsedToday.Clear();
                    Save();
                }
                return _state.UsedToday.TryGetValue(userId.ToString(), out var u) ? u : 0;
            }

            // Players currently waiting through a warmup (one at a time per player).
            private readonly HashSet<ulong> _pendingTeleports = new HashSet<ulong>();

            public TeleportModule(RustForgeCore plugin) : base(plugin, "teleport") { }

            private bool _dirty;
            // Handle for the periodic prune timer — stored so we can cancel it in OnDisabled().
            // Oxide/Carbon timers are NOT stopped automatically when a module is disabled; without
            // this we'd keep firing against a stale _state after the module is turned off.
            private Timer _pruneTimer;

            // Generation counter: incremented on every OnEnabled(). Warmup closures capture the
            // generation at start time; if the module has been cycled (disable→enable) since the
            // warmup began, the stale closure sees a different generation and bails, so an "old"
            // closure can never teleport a player against freshly re-initialised state.
            private int _enableGeneration;

            public override void OnEnabled()
            {
                _enableGeneration++;
                _homes = LoadData<Dictionary<string, Dictionary<string, float[]>>>("homes");
                _state = LoadData<TeleportState>("teleport_state");
                if (_state.Cooldowns == null) _state.Cooldowns = new Dictionary<string, DateTime>();
                if (_state.UsedToday == null) _state.UsedToday = new Dictionary<string, int>();
                if (string.IsNullOrEmpty(_state.UsageDay)) _state.UsageDay = DateTime.UtcNow.ToString("yyyy-MM-dd");
                if (_state.LastDamage == null) _state.LastDamage = new Dictionary<string, DateTime>();

                // Периодическая очистка устаревших записей (кулдауны, raid-block) независимо от _dirty.
                // Без этого таймера записи игроков, которые давно отключились, копились бы в файле
                // до следующего dirty-флаша, постепенно раздувая teleport_state.json на больших серверах.
                _pruneTimer = Plugin.timer.Every(60f, () =>
                {
                    if (PruneState()) Save(); // помечаем dirty только если что-то было реально удалено
                });
            }

            // Save() лишь помечает данные как изменённые; фактическая запись — пакетно в Flush().
            private void Save() => _dirty = true;

            public void Flush()
            {
                if (!_dirty || _homes == null) return;
                PruneState();
                SaveData("homes", _homes);
                SaveData("teleport_state", _state);
                _dirty = false;
            }

            // Удаляет истёкшие кулдауны и устаревшие raid-block-записи,
            // чтобы data-файл не рос бесконечно.
            // Счётчики UsedToday очищаются целиком при смене дня в UsedToday().
            // Возвращает true, если хотя бы одна запись была удалена (нужно сохранить на диск).
            private bool PruneState()
            {
                var now = DateTime.UtcNow;
                var expired = _state.Cooldowns.Where(kv => kv.Value <= now).Select(kv => kv.Key).ToList();
                foreach (var key in expired) _state.Cooldowns.Remove(key);
                // Raid-block: 30 секунд. Записи старше этого окна больше не нужны.
                var raidBlockSeconds = 30;
                var oldDamage = _state.LastDamage
                    .Where(kv => (now - kv.Value).TotalSeconds >= raidBlockSeconds)
                    .Select(kv => kv.Key).ToList();
                foreach (var key in oldDamage) _state.LastDamage.Remove(key);
                return expired.Count > 0 || oldDamage.Count > 0;
            }

            public override void OnDisabled()
            {
                // Cancel the prune timer so it cannot fire against _state after the module is off.
                // Oxide/Carbon timers are NOT stopped automatically when a module is disabled.
                _pruneTimer?.Destroy();
                _pruneTimer = null;
                // Clear any outstanding warmup entries so that if the module is re-enabled the
                // player is not permanently stuck behind "Телепорт уже ожидает".  The warmup
                // closure will still fire (Oxide timers cannot be cancelled retroactively), but
                // it bails at the !Enabled guard and then finds an empty set on re-enable.
                _pendingTeleports.Clear();
                Flush();
            }

            public override object OnPlayerDamage(BasePlayer victim, HitInfo info)
            {
                if (victim != null && !victim.IsNpc)
                {
                    _state.LastDamage[victim.UserIDString] = DateTime.UtcNow;
                    Save(); // персистируем, чтобы reload плагина не сбрасывал raid-block
                }
                return null;
            }

            public void CmdSetHome(BasePlayer p, string[] args)
            {
                if (!GetBool("allowHomes", true)) { Reply(p, "Дома отключены на этом сервере."); return; }
                var name = args.Length > 0 ? args[0].ToLower() : "home";
                if (!_homes.TryGetValue(p.UserIDString, out var homes)) { homes = new Dictionary<string, float[]>(); _homes[p.UserIDString] = homes; }
                if (!homes.ContainsKey(name) && homes.Count >= GetInt("maxHomes", 3))
                { Reply(p, $"Лимит домов: {GetInt("maxHomes", 3)}."); return; }
                var pos = p.transform.position;
                homes[name] = new[] { pos.x, pos.y, pos.z };
                Save();
                Reply(p, $"Дом «{name}» сохранён.");
            }

            public override void OnPlayerDisconnected(BasePlayer p) => _pendingTeleports.Remove(p.userID);

            public void CmdHome(BasePlayer p, string[] args)
            {
                if (!GetBool("allowHomes", true)) { Reply(p, "Дома отключены на этом сервере."); return; }
                var name = args.Length > 0 ? args[0].ToLower() : "home";
                if (!_homes.TryGetValue(p.UserIDString, out var homes) || !homes.TryGetValue(name, out var pos))
                { Reply(p, $"Дом «{name}» не найден. /sethome <имя> — сохранить."); return; }

                // Only one pending warmup teleport per player at a time.
                if (_pendingTeleports.Contains(p.userID))
                { Reply(p, "Телепорт уже ожидает. Дождитесь завершения или отмены."); return; }

                if (GetBool("blockWhileRaid", true) && _state.LastDamage.TryGetValue(p.UserIDString, out var dmg) &&
                    (DateTime.UtcNow - dmg).TotalSeconds < 30)
                { Reply(p, "Телепорт заблокирован: вы недавно получали урон."); return; }

                // Check limits immediately so the player gets instant feedback.
                var limit = GetInt("dailyLimit", 10);
                if (limit > 0 && UsedToday(p.userID) >= limit) { Reply(p, $"Дневной лимит телепортов исчерпан ({limit})."); return; }

                if (_state.Cooldowns.TryGetValue(p.UserIDString, out var next) && next > DateTime.UtcNow)
                { Reply(p, $"Подождите {(next - DateTime.UtcNow).TotalSeconds:0} сек до следующего телепорта."); return; }

                var warmup = GetInt("warmupSeconds", 10);
                var startPos = p.transform.position;
                var startTime = DateTime.UtcNow;
                var generation = _enableGeneration; // captured: identifies the enable-cycle this warmup belongs to
                _pendingTeleports.Add(p.userID);

                Reply(p, warmup > 0 ? $"Телепортация через {warmup} сек. Не двигайтесь." : "Телепортация...");
                Plugin.timer.Once(warmup, () =>
                {
                    _pendingTeleports.Remove(p.userID);
                    // Guard: module may have been disabled while the warmup was running.
                    // _state is invalid after OnDisabled(), so bail out silently.
                    if (!Enabled) return;
                    // Stale-closure guard: if the module was disabled and re-enabled while this
                    // warmup was outstanding, the closure belongs to a previous enable-cycle and
                    // must not act on the fresh state.
                    if (generation != _enableGeneration) return;
                    if (p == null || !p.IsConnected) return;

                    // Cancel if player took damage during the warmup window.
                    if (GetBool("blockWhileRaid", true) && _state.LastDamage.TryGetValue(p.UserIDString, out var dmgAt) && dmgAt >= startTime)
                    { Reply(p, "Телепорт отменён: вы получили урон."); return; }

                    // Cancel if player moved during warmup.
                    if (Vector3.Distance(p.transform.position, startPos) > 1.5f)
                    { Reply(p, "Телепорт отменён: вы двигались."); return; }

                    // Re-read limits at teleport time to prevent double-counting from parallel warmups.
                    var limitNow = GetInt("dailyLimit", 10);
                    var usedNow = UsedToday(p.userID);
                    if (limitNow > 0 && usedNow >= limitNow) { Reply(p, $"Дневной лимит телепортов исчерпан ({limitNow})."); return; }
                    if (_state.Cooldowns.TryGetValue(p.UserIDString, out var nextNow) && nextNow > DateTime.UtcNow)
                    { Reply(p, $"Подождите {(nextNow - DateTime.UtcNow).TotalSeconds:0} сек до следующего телепорта."); return; }

                    p.Teleport(new Vector3(pos[0], pos[1], pos[2]));
                    _state.Cooldowns[p.UserIDString] = DateTime.UtcNow.AddSeconds(GetInt("cooldownSeconds", 300));
                    _state.UsedToday[p.UserIDString] = usedNow + 1;
                    // Crash-safe accounting: flush the increment to disk synchronously at
                    // the commit point. Without this, a hard-crash before the next 30s
                    // FlushDirtyData tick would lose the increment and grant the player
                    // an extra free use after restart. Teleports are rare enough that the
                    // atomic write (temp + rename) cost is negligible here.
                    Save();
                    Flush();
                    Reply(p, $"Вы дома («{name}»).");
                    Plugin.PushEvent("module", "log.teleportUsed", new JObject { ["player"] = p.displayName, ["home"] = name });
                });
            }
        }

        #endregion

        #region Модуль: Квесты

        public class QuestsModule : GameModule
        {
            private class QuestProgress { public int Kills; public bool Claimed; public string Day; }
            private Dictionary<string, QuestProgress> _progress;

            public QuestsModule(RustForgeCore plugin) : base(plugin, "quests") { }
            private bool _dirty;
            private Timer _pruneTimer;

            public override void OnEnabled()
            {
                _progress = LoadData<Dictionary<string, QuestProgress>>("quests");

                // Периодическая очистка устаревших записей игроков, которые давно не заходили.
                // Без этого таймера quest-записи неактивных игроков накапливались бы в quests.json
                // бесконечно. Помечаем dirty только если что-то было реально удалено.
                _pruneTimer = Plugin.timer.Every(300f, () =>
                {
                    if (PruneProgress()) Save();
                });
            }

            // Save() лишь помечает данные как изменёнными; фактическая запись — пакетно в Flush().
            private void Save() => _dirty = true;

            public void Flush()
            {
                if (!_dirty || _progress == null) return;
                PruneProgress();
                SaveData("quests", _progress);
                _dirty = false;
            }

            // Удаляет записи игроков, чей день квеста старше порога (по умолчанию 7 дней).
            // Если игрок вернётся, его прогресс всё равно сбросится при первом обращении
            // (autoReset = true, день изменился), так что удаление записи безопасно.
            // Возвращает true, если хотя бы одна запись была удалена (нужно сохранить на диск).
            private bool PruneProgress()
            {
                if (_progress == null) return false;
                var threshold = GetInt("pruneAfterDays", 7);
                var cutoff = DateTime.UtcNow.AddDays(-threshold);
                var stale = _progress
                    .Where(kv =>
                    {
                        if (string.IsNullOrEmpty(kv.Value.Day)) return true;
                        return DateTime.TryParse(kv.Value.Day, out var d) && d < cutoff;
                    })
                    .Select(kv => kv.Key)
                    .ToList();
                foreach (var key in stale) _progress.Remove(key);
                return stale.Count > 0;
            }

            public override void OnDisabled()
            {
                _pruneTimer?.Destroy();
                _pruneTimer = null;
                Flush();
            }

            private int KillTarget()
            {
                switch (GetString("difficulty", "normal"))
                {
                    case "easy": return 3;
                    case "hard": return 15;
                    default: return 7;
                }
            }

            private QuestProgress Progress(BasePlayer p)
            {
                var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
                if (!_progress.TryGetValue(p.UserIDString, out var q) || (GetBool("autoReset", true) && q.Day != today))
                {
                    q = new QuestProgress { Day = today };
                    _progress[p.UserIDString] = q;
                }
                return q;
            }

            public override void OnPlayerDeathHook(BasePlayer victim, BasePlayer attacker)
            {
                if (attacker == null) return;
                var q = Progress(attacker);
                q.Kills++;
                var target = KillTarget();
                if (q.Kills == target && !q.Claimed)
                {
                    q.Claimed = true;
                    var reward = (int)(500 * GetDouble("rewardMultiplier", 1));
                    Plugin.GetModule<EconomyModule>("economy")?.Deposit(attacker.userID, reward);
                    Reply(attacker, $"Квест «{target} убийств» выполнен! Награда: {reward}.");
                    Plugin.PushEvent("module", "log.questCompleted", new JObject { ["player"] = attacker.displayName, ["quest"] = $"kills_{target}" });
                }
                Save();
            }

            public void CmdQuests(BasePlayer p)
            {
                var q = Progress(p);
                var target = KillTarget();
                Reply(p, q.Claimed
                    ? "Дневной квест выполнен. Новый — завтра."
                    : $"Дневной квест: убийства {q.Kills}/{target}. Награда: {(int)(500 * GetDouble("rewardMultiplier", 1))}.");
            }
        }

        #endregion

        #region Модуль: NPC

        public class NpcModule : GameModule
        {
            private const string ScientistPrefab = "assets/rust.ai/agents/npcplayer/humannpc/scientist/scientistnpc_roam.prefab";
            private readonly Dictionary<ulong, List<BaseEntity>> _spawned = new Dictionary<ulong, List<BaseEntity>>();

            public NpcModule(RustForgeCore plugin) : base(plugin, "npc") { }

            public override void OnDisabled()
            {
                foreach (var list in _spawned.Values)
                    foreach (var e in list.Where(e => e != null && !e.IsDestroyed))
                        e.Kill();
                _spawned.Clear();
            }

            public void CmdNpc(BasePlayer p, string[] args)
            {
                if (args.Length == 0 || args[0].ToLower() != "spawn")
                { Reply(p, "Использование: /npc spawn — призвать NPC-охранника рядом."); return; }

                if (!_spawned.TryGetValue(p.userID, out var list)) { list = new List<BaseEntity>(); _spawned[p.userID] = list; }
                list.RemoveAll(e => e == null || e.IsDestroyed);
                var max = GetInt("maxNpcPerPlayer", 2);
                if (list.Count >= max) { Reply(p, $"Лимит NPC: {max}."); return; }

                var pos = p.transform.position + p.transform.forward * 3f;
                var npc = GameManager.server.CreateEntity(ScientistPrefab, pos);
                if (npc == null) { Reply(p, "Не удалось создать NPC."); return; }
                npc.Spawn();
                list.Add(npc);
                if (!GetBool("dropLoot", true))
                    (npc as ScientistNPC)?.inventory?.containerMain?.Clear();
                Reply(p, $"NPC создан ({list.Count}/{max}). Режим: {GetString("aggression", "defensive")}.");
                Plugin.PushEvent("module", "log.npcSpawned", new JObject { ["player"] = p.displayName });

                var respawn = GetInt("respawnSeconds", 600);
                Plugin.timer.Once(respawn, () => { if (npc != null && !npc.IsDestroyed) npc.Kill(); });
            }
        }

        #endregion

        #region Модуль: События

        public class EventsModule : GameModule
        {
            private Timer _timer;
            public EventsModule(RustForgeCore plugin) : base(plugin, "events") { }

            public override void OnEnabled() => Reschedule();
            public override void OnConfigUpdated() => Reschedule();
            public override void OnDisabled() { _timer?.Destroy(); _timer = null; }

            private void Reschedule()
            {
                _timer?.Destroy();
                var minutes = Math.Max(10, GetInt("eventFrequencyMinutes", 90));
                _timer = Plugin.timer.Every(minutes * 60f, RunEvent);
            }

            private void RunEvent()
            {
                var online = BasePlayer.activePlayerList.Count;
                if (online < GetInt("minPlayers", 5)) return;

                // MVP-событие: аирдроп + распределение призового фонда.
                // CargoPlane сам выбирает маршрут — InitDropPosition убран для максимальной совместимости.
                var plane = GameManager.server.CreateEntity("assets/prefabs/npc/cargo plane/cargo_plane.prefab");
                if (plane != null) plane.Spawn();
                var pos = plane?.transform.position ?? Vector3.zero;

                var pool = GetInt("rewardPool", 2000);
                var economy = Plugin.GetModule<EconomyModule>("economy");
                if (economy != null && pool > 0 && online > 0)
                {
                    var share = pool / online;
                    foreach (var p in BasePlayer.activePlayerList) economy.Deposit(p.userID, share);
                }

                if (GetBool("announceInChat", true))
                {
                    var share = online > 0 ? GetInt("rewardPool", 2000) / online : 0;
                    foreach (var p in BasePlayer.activePlayerList)
                        Reply(p, $"Событие «Аирдроп»! Карго-самолёт в пути. Всем онлайн начислено {share}.");
                }

                Plugin.PushEvent("module", "log.eventStarted", new JObject { ["event"] = "airdrop", ["players"] = online });
            }
        }

        #endregion
    }
}
