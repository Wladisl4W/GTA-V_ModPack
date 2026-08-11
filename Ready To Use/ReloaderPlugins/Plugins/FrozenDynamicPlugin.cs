using GTA;
using GTA.Native;
using LemonUI;
using LemonUI.Menus;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace FrozenDynamic
{
    /// <summary>
    /// Мод для заморозки/разморозки всех NPC в GTA V.
    /// Управление: K - открыть меню.
    ///
    /// Адаптировано под ReloaderPlugins:
    /// - вместо Script.Tick/KeyDown используются OnTick/OnKeyDown интерфейса IGtaPlugin
    /// - блокирующие Script.Wait заменены на очередь отложенного восстановления анимаций
    /// - свой лог в ReloaderPlugins\FrozenDynamic.log
    /// </summary>
    public class FrozenDynamicPlugin : IGtaPlugin
    {
        private readonly NativeMenu _menu;
        private readonly NativeItem _freezeItem;
        private readonly NativeItem _unfreezeItem;
        private ObjectPool _pool;
        private bool _isFrozen = false;

        // Трекинг замороженных педов
        private readonly HashSet<int> _frozenPeds = new HashSet<int>();

        // Сохранённые анимации педов (только танцы/сценарии которые мы можем определить)
        private readonly Dictionary<int, PedAnimState> _pedAnimStates = new Dictionary<int, PedAnimState>();

        // Очередь педов, ожидающих загрузки анимационного словаря (неблокирующее восстановление)
        private readonly List<int> _pendingRestores = new List<int>();
        private readonly Dictionary<int, long> _pendingRestoreStart = new Dictionary<int, long>();

        private long _lastMaintainMs = 0;
        private const long MAINTAIN_INTERVAL_MS = 500;
        private const long PENDING_RESTORE_TIMEOUT_MS = 3000;
        private const Keys MenuToggleKey = Keys.K;

        /// <summary>
        /// Ссылка на анимацию (dict + anim)
        /// </summary>
        private class AnimRef
        {
            public string Dict;
            public string Anim;

            public AnimRef(string dict, string anim)
            {
                this.Dict = dict;
                this.Anim = anim;
            }
        }

        /// <summary>
        /// Список известных танцевальных анимаций для определения
        /// </summary>
        private static readonly AnimRef[] DanceAnims = new AnimRef[]
        {
            // MP Celebration анимации (мужские)
            new AnimRef("anim@mp_player_intcelebrationmale", "idle_a"),
            new AnimRef("anim@mp_player_intcelebrationmale", "idle_b"),
            new AnimRef("anim@mp_player_intcelebrationmale", "idle_c"),
            new AnimRef("anim@mp_player_intcelebrationmale", "idle_d"),
            new AnimRef("anim@mp_player_intcelebrationmale", "idle_e"),
            new AnimRef("anim@mp_player_intcelebrationmale", "idle_f"),
            // MP Celebration анимации (женские)
            new AnimRef("anim@mp_player_intcelebrationfemale", "idle_a"),
            new AnimRef("anim@mp_player_intcelebrationfemale", "idle_b"),
            new AnimRef("anim@mp_player_intcelebrationfemale", "idle_c"),
            new AnimRef("anim@mp_player_intcelebrationfemale", "idle_d"),
            new AnimRef("anim@mp_player_intcelebrationfemale", "idle_e"),
            new AnimRef("anim@mp_player_intcelebrationfemale", "idle_f"),
            // Clown / Gumbo Dance
            new AnimRef("move_clown@p_m_zero_idles@", "fidget_short_dance"),
            new AnimRef("move_clown@p_m_one_idles@", "fidget_short_dance"),
            new AnimRef("move_clown@p_m_zero_idles@", "fidget_dance_enter"),
            new AnimRef("move_clown@p_m_one_idles@", "fidget_dance_enter"),
            // World Human Dancing
            new AnimRef("amb@world_human_dancing@male@base", "base"),
            new AnimRef("amb@world_human_dancing@female@base", "base"),
            // Strip Club Pole Dance
            new AnimRef("mini@strip_club@pole_dance@pole_a_2_stage", "pd_a2_stage"),
            new AnimRef("mini@strip_club@pole_dance@pole_a_1_stage", "pd_a1_stage"),
            new AnimRef("mini@strip_club@pole_dance@pole_b_2_stage", "pd_b2_stage"),
            new AnimRef("mini@strip_club@pole_dance@pole_b_1_stage", "pd_b1_stage"),
            // Dancing @ Club
            new AnimRef("anim@amb@nightclub@mini@dance@dance_01@dance@", "solo"),
            new AnimRef("anim@amb@nightclub@mini@dance@dance_02@dance@", "solo"),
            new AnimRef("anim@amb@nightclub@mini@dance@dance_03@dance@", "solo"),
            new AnimRef("anim@amb@nightclub@mini@dance@dance_04@dance@", "solo"),
            new AnimRef("anim@amb@nightclub@mini@dance@dance_05@dance@", "solo"),
            new AnimRef("anim@amb@nightclub@mini@dance@dance_06@dance@", "solo"),
            // Club dance couple
            new AnimRef("anim@amb@nightclub@mini@dance@dance_paired@dance_01@", "couple_dance"),
            new AnimRef("anim@amb@nightclub@mini@dance@dance_paired@dance_02@", "couple_dance"),
            new AnimRef("anim@amb@nightclub@mini@dance@dance_paired@dance_03@", "couple_dance"),
            // Hands up dancing
            new AnimRef("anim@mp_player_intupperair_shagging", "idle_a"),
            new AnimRef("anim@mp_player_intupperuncle_disco", "idle_a"),
            new AnimRef("anim@mp_player_intupperfind_the_tensor", "idle_a"),
            new AnimRef("anim@mp_player_intupperpeace", "idle_a"),
        };

        public FrozenDynamicPlugin()
        {
            _menu = new NativeMenu("Frozen Dynamic", "Управление NPC");

            _freezeItem = new NativeItem("Заморозить NPC", "Остановить всех пешеходов на месте");
            _unfreezeItem = new NativeItem("Разморозить NPC", "Вернуть NPC к обычному поведению");

            _menu.Add(_freezeItem);
            _menu.Add(_unfreezeItem);

            _freezeItem.Activated += OnFreezeActivated;
            _unfreezeItem.Activated += OnUnfreezeActivated;
        }

        public void OnStart()
        {
            try
            {
                _pool = new ObjectPool();
                _pool.Add(_menu);

                Log("Мод успешно загружен");
                GTA.UI.Notification.PostTicker("~g~Frozen Dynamic Mod~w~\nНажмите ~b~K~w~ для открытия меню", false, false);
            }
            catch (Exception ex)
            {
                Log("OnStart: " + ex.Message);
            }
        }

        public void OnTick()
        {
            try
            {
                long now = NowMs();

                if (_pool != null && _menu.Visible)
                    _pool.Process();

                if (_isFrozen && now - _lastMaintainMs >= MAINTAIN_INTERVAL_MS)
                {
                    _lastMaintainMs = now;
                    MaintainFrozenState();
                }

                ProcessPendingRestores(now);
            }
            catch (Exception ex)
            {
                Log("OnTick: " + ex.Message);
            }
        }

        public void OnKeyDown(Keys key)
        {
            try
            {
                if (key == MenuToggleKey && _menu != null)
                    _menu.Visible = !_menu.Visible;
            }
            catch (Exception ex)
            {
                Log("OnKeyDown: " + ex.Message);
            }
        }

        public void OnAbort()
        {
            try
            {
                Log("FrozenDynamic выгружается, размораживаем NPC...");
                if (_pool != null) _pool.HideAll();
                UnfreezeAllPeds();
                _isFrozen = false;
            }
            catch (Exception ex)
            {
                Log("OnAbort: " + ex.Message);
            }
        }

        private void OnFreezeActivated(object sender, EventArgs e)
        {
            try
            {
                if (_isFrozen)
                {
                    ShowNotification("~y~NPC уже заморожены!");
                    return;
                }

                int count = FreezeAllPeds();
                _isFrozen = true;
                _lastMaintainMs = NowMs();

                Log("Заморожено NPC: " + count + " (сохранено анимаций: " + _pedAnimStates.Count + ")");
                ShowNotification("~g~Заморожено NPC: " + count);
            }
            catch (Exception ex)
            {
                Log("OnFreezeActivated: " + ex.Message);
            }
        }

        private void OnUnfreezeActivated(object sender, EventArgs e)
        {
            try
            {
                if (!_isFrozen)
                {
                    ShowNotification("~y~NPC уже разморожены!");
                    return;
                }

                int count = UnfreezeAllPeds();
                _isFrozen = false;

                Log("Разморожено NPC: " + count);
                ShowNotification("~g~Разморожено NPC: " + count);
            }
            catch (Exception ex)
            {
                Log("OnUnfreezeActivated: " + ex.Message);
            }
        }

        /// <summary>
        /// Определяет текущую танцевальную анимацию педа
        /// </summary>
        private AnimRef GetCurrentDanceAnim(Ped ped)
        {
            foreach (AnimRef anim in DanceAnims)
            {
                Function.Call(Hash.REQUEST_ANIM_DICT, anim.Dict);

                if (Function.Call<bool>(Hash.IS_ENTITY_PLAYING_ANIM, ped.Handle, anim.Dict, anim.Anim, 3))
                {
                    return anim;
                }
            }

            // Проверяем сценарные анимации (WORLD_HUMAN_*)
            if (Function.Call<bool>(Hash.IS_PED_USING_ANY_SCENARIO, ped.Handle))
            {
                // GET_PED_SCENARIO_NAME hash = 0x3B0B693D
                string scenarioName = Function.Call<string>((Hash)0x3B0B693D, ped.Handle);
                if (!string.IsNullOrEmpty(scenarioName))
                {
                    return new AnimRef(null, scenarioName); // Dict = null означает сценарий
                }
            }

            return null;
        }

        /// <summary>
        /// Замораживает всех педов
        /// </summary>
        private int FreezeAllPeds()
        {
            int count = 0;
            Ped playerPed = Game.Player.Character;

            if (playerPed == null || !playerPed.Exists())
            {
                ShowNotification("~y~Игрок не найден");
                return 0;
            }

            Ped[] allPeds = World.GetAllPeds();

            if (allPeds == null || allPeds.Length == 0)
            {
                ShowNotification("~y~NPC не найдены в игре");
                return 0;
            }

            foreach (Ped ped in allPeds)
            {
                // Пропускаем null, несуществующих и игрока
                if (ped == null || !ped.Exists() || ped.Handle == playerPed.Handle)
                    continue;

                try
                {
                    // Сохраняем анимацию (если это танец или сценарий)
                    AnimRef currentAnim = GetCurrentDanceAnim(ped);
                    if (currentAnim != null)
                    {
                        double progress = 0.0;

                        if (currentAnim.Dict != null)
                        {
                            // Обычная анимация — сохраняем прогресс
                            progress = Function.Call<double>(
                                Hash.GET_ENTITY_ANIM_CURRENT_TIME, ped.Handle, currentAnim.Dict, currentAnim.Anim);
                        }

                        _pedAnimStates[ped.Handle] = new PedAnimState
                        {
                            Dict = currentAnim.Dict,
                            Anim = currentAnim.Anim,
                            Progress = progress,
                            IsScenario = currentAnim.Dict == null
                        };

                        Log("Сохранена анимация для педа " + ped.Handle + ": " + (currentAnim.Dict ?? "SCENARIO") + "/" + currentAnim.Anim);
                    }

                    // Обнуляем скорость перед заморозкой (чтобы физика не накапливалась)
                    Function.Call(Hash.SET_ENTITY_VELOCITY, ped.Handle, 0f, 0f, 0f);

                    // Замораживаем позицию
                    ped.IsPositionFrozen = true;

                    // Блокируем permanent events
                    ped.BlockPermanentEvents = true;

                    // === ПОЛНЫЙ ИГНОР — УСТАНАВЛИВАЕТСЯ ОДИН РАЗ ===

                    // Блокируем non-temporary events — пед не реагирует на AI-триггеры
                    Function.Call(Hash.SET_BLOCKING_OF_NON_TEMPORARY_EVENTS, ped.Handle, true);

                    // Relationship — полный игнор
                    SetPedIgnoredByEveryone(ped);

                    // Flee — не убегать (0, 0 = все flee атрибуты отключены)
                    Function.Call(Hash.SET_PED_FLEE_ATTRIBUTES, ped.Handle, 0, 0);

                    // Combat — не драться
                    Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, ped.Handle, 5, true);  // BF_CanFight = false
                    Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, ped.Handle, 46, true); // BF_CanFleeInCombat = false

                    // Добавляем в HashSet замороженных
                    _frozenPeds.Add(ped.Handle);

                    count++;
                }
                catch (Exception ex)
                {
                    Log("Ошибка при заморозке педа " + ped.Handle + ": " + ex.Message);
                }
            }

            return count;
        }

        /// <summary>
        /// Размораживает всех педов
        /// Педы остаются стоять с полным игнором, танцы перезапускаются
        /// </summary>
        private int UnfreezeAllPeds()
        {
            int count = 0;
            Ped playerPed = Game.Player.Character;

            if (playerPed == null || !playerPed.Exists())
            {
                ShowNotification("~y~Игрок не найден");
                _frozenPeds.Clear();
                _pedAnimStates.Clear();
                _pendingRestores.Clear();
                _pendingRestoreStart.Clear();
                return 0;
            }

            foreach (int handle in _frozenPeds.ToList()) // ToList для безопасного удаления
            {
                try
                {
                    Ped ped = (Ped)GTA.Entity.FromHandle(handle);

                    if (ped == null || !ped.Exists())
                    {
                        _frozenPeds.Remove(handle);
                        _pedAnimStates.Remove(handle);
                        continue;
                    }

                    // === 1. СНИМАЕМ ЗАМОРОЗКУ ===
                    ped.IsPositionFrozen = false;

                    // === 2. ОБНУЛЯЕМ СКОРОСТЬ ===
                    Function.Call(Hash.SET_ENTITY_VELOCITY, ped.Handle, 0f, 0f, 0f);

                    // === 3. РАЗБЛОКИРОВКА PERMANENT EVENTS ===
                    ped.BlockPermanentEvents = false;

                    // === 4. УСТАНАВЛИВАЕМ ИГНОР (пед стоит на месте) ===
                    SetPedIgnoredByEveryone(ped);
                    Function.Call(Hash.SET_BLOCKING_OF_NON_TEMPORARY_EVENTS, ped.Handle, true);
                    Function.Call(Hash.SET_PED_FLEE_ATTRIBUTES, ped.Handle, 0, 0);
                    Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, ped.Handle, 5, true);
                    Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, ped.Handle, 46, true);

                    // === 5. ВОССТАНАВЛИВАЕМ АНИМАЦИЮ (неблокирующе) ===
                    QueuePedAnimationRestore(ped);

                    _frozenPeds.Remove(handle);
                    count++;
                }
                catch (Exception ex)
                {
                    Log("Ошибка при разморозке педа " + handle + ": " + ex.Message);
                    _frozenPeds.Remove(handle);
                    _pedAnimStates.Remove(handle);
                }
            }

            _frozenPeds.Clear();
            _pedAnimStates.Clear();
            return count;
        }

        /// <summary>
        /// Ставит педа в очередь восстановления анимации.
        /// Анимация запустится в ProcessPendingRestores, как только словарь загрузится.
        /// </summary>
        private void QueuePedAnimationRestore(Ped ped)
        {
            int handle = ped.Handle;

            PedAnimState animState;
            if (!_pedAnimStates.TryGetValue(handle, out animState))
                return;

            try
            {
                if (animState.IsScenario && animState.Anim != null)
                {
                    // Сценарная анимация — не можем перезапустить без сценария
                    // Пед будет продолжать сам
                    Log("Пед " + handle + " был в сценарии " + animState.Anim + ", не перезапускаем");
                }
                else if (animState.Dict != null && animState.Anim != null)
                {
                    // Обычная анимация — запрашиваем словарь и ставим в очередь
                    Function.Call(Hash.REQUEST_ANIM_DICT, animState.Dict);

                    if (!_pendingRestores.Contains(handle))
                    {
                        _pendingRestores.Add(handle);
                        _pendingRestoreStart[handle] = NowMs();
                        Log("Поставлен в очередь на восстановление анимации: " + handle + " (" + animState.Dict + "/" + animState.Anim + ")");
                    }
                }
            }
            catch (Exception ex)
            {
                Log("Ошибка при постановке анимации в очередь педа " + handle + ": " + ex.Message);
            }
        }

        /// <summary>
        /// Обрабатывает очередь восстановления анимаций (вызывается каждый тик).
        /// Не блокирует поток — запускает анимацию, как только словарь загрузился.
        /// </summary>
        private void ProcessPendingRestores(long now)
        {
            for (int i = _pendingRestores.Count - 1; i >= 0; i--)
            {
                int handle = _pendingRestores[i];

                try
                {
                    long start;
                    _pendingRestoreStart.TryGetValue(handle, out start);
                    if (now - start > PENDING_RESTORE_TIMEOUT_MS)
                    {
                        Log("Таймаут загрузки анимации для педа " + handle);
                        _pendingRestores.RemoveAt(i);
                        _pendingRestoreStart.Remove(handle);
                        continue;
                    }

                    Ped ped = (Ped)GTA.Entity.FromHandle(handle);
                    if (ped == null || !ped.Exists())
                    {
                        _pendingRestores.RemoveAt(i);
                        _pendingRestoreStart.Remove(handle);
                        continue;
                    }

                    PedAnimState animState;
                    if (!_pedAnimStates.TryGetValue(handle, out animState))
                    {
                        _pendingRestores.RemoveAt(i);
                        _pendingRestoreStart.Remove(handle);
                        continue;
                    }

                    if (animState.Dict == null || !Function.Call<bool>(Hash.HAS_ANIM_DICT_LOADED, animState.Dict))
                        continue;

                    // Запускаем анимацию заново
                    Function.Call(Hash.TASK_PLAY_ANIM,
                        ped.Handle,
                        animState.Dict,
                        animState.Anim,
                        8.0f,           // blendInSpeed
                        -8.0f,          // blendOutSpeed
                        -1,             // duration (-1 = loop)
                        1,              // flags (1 = LOOP)
                        1.0f,           // playbackRate
                        false,          // lockX
                        false,          // lockY
                        false           // lockZ
                    );

                    // Восстанавливаем прогресс анимации
                    Function.Call(Hash.SET_ENTITY_ANIM_CURRENT_TIME,
                        ped.Handle, animState.Dict, animState.Anim, animState.Progress);

                    Log("Восстановлена анимация для педа " + handle + ": " + animState.Dict + "/" + animState.Anim + " (progress: " + animState.Progress.ToString("F2") + ")");

                    _pendingRestores.RemoveAt(i);
                    _pendingRestoreStart.Remove(handle);
                }
                catch (Exception ex)
                {
                    Log("Ошибка при восстановлении анимации педа " + handle + ": " + ex.Message);
                    _pendingRestores.RemoveAt(i);
                    _pendingRestoreStart.Remove(handle);
                }
            }
        }

        /// <summary>
        /// Поддерживает состояние замороженных педов (вызывается каждые ~500ms)
        /// ТОЛЬКО для проверки мёртвых/удалённых педов — не для установки флагов
        /// </summary>
        private void MaintainFrozenState()
        {
            foreach (int handle in _frozenPeds.ToList())
            {
                try
                {
                    Ped ped = (Ped)GTA.Entity.FromHandle(handle);

                    if (ped == null || !ped.Exists() || ped.IsDead)
                    {
                        _frozenPeds.Remove(handle);
                        _pedAnimStates.Remove(handle);
                    }
                    else
                    {
                        // Просто убеждаемся что позиция всё ещё заморожена
                        ped.IsPositionFrozen = true;
                    }
                }
                catch (Exception ex)
                {
                    Log("Ошибка при проверке педа " + handle + ": " + ex.Message);
                    _frozenPeds.Remove(handle);
                    _pedAnimStates.Remove(handle);
                }
            }
        }

        /// <summary>
        /// Устанавливает полный игнор игрока для педа
        /// </summary>
        private void SetPedIgnoredByEveryone(Ped ped)
        {
            try
            {
                Ped playerPed = Game.Player.Character;
                if (playerPed == null || !playerPed.Exists())
                    return;

                int playerGroup = Function.Call<int>(Hash.GET_PED_RELATIONSHIP_GROUP_HASH, playerPed.Handle);
                int pedGroup = Function.Call<int>(Hash.GET_PED_RELATIONSHIP_GROUP_HASH, ped.Handle);

                // Relationship level 5 = Companion/Ignore (максимальный игнор)
                Function.Call(Hash.SET_RELATIONSHIP_BETWEEN_GROUPS, 5, pedGroup, playerGroup);
                Function.Call(Hash.SET_RELATIONSHIP_BETWEEN_GROUPS, 5, playerGroup, pedGroup);
            }
            catch (Exception ex)
            {
                Log("Ошибка при установке IgnoredByEveryone: " + ex.Message);
            }
        }

        /// <summary>
        /// Состояние анимации педа
        /// </summary>
        private class PedAnimState
        {
            public string Dict { get; set; }
            public string Anim { get; set; }
            public double Progress { get; set; }
            public bool IsScenario { get; set; }
        }

        private static long NowMs()
        {
            return DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
        }

        private void Log(string message)
        {
            try
            {
                string line = "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " + message;
                File.AppendAllText(
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ReloaderPlugins", "FrozenDynamic.log"),
                    line + Environment.NewLine);
            }
            catch
            {
            }
        }

        private void ShowNotification(string message)
        {
            try
            {
                GTA.UI.Notification.PostTicker(message, false, false);
            }
            catch (Exception ex)
            {
                Log("Ошибка при показе уведомления: " + ex.Message);
            }
        }
    }
}
