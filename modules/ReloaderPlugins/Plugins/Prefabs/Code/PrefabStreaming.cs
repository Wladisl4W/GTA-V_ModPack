using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GTA;
using GTA.Math;
using GTA.Native;
using LemonUI.Menus;
using ModPack.Core;
using Newtonsoft.Json;

namespace ModPack.Prefabs
{
    public sealed class PrefabPlacement
    {
        public string Kind { get; set; }
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
        public float DirectionX { get; set; }
        public float DirectionY { get; set; }
        public float Spacing { get; set; }
        public float Radius { get; set; }

        public PrefabPlacement Copy()
        {
            return (PrefabPlacement)MemberwiseClone();
        }
    }

    public sealed partial class PrefabsPlugin
    {
        public static PrefabsPlugin Current { get; private set; }
        private Action<PrefabPlacement> _presetPlacementCompleted;
        private Action _presetPlacementCancelled;
        private bool _presetMenuPending;
        private int _presetMenuRequestedAt;
        private List<PrefabPlacement> _pendingPresetPlacements = new List<PrefabPlacement>();
        private int _pendingPresetStartedAt;

        public void ApplyPresetPlacements(IList<PrefabPlacement> placements)
        {
            StopPrefabStreaming();
            _streamGroups.Clear();
            _pendingPresetPlacements.Clear();
            if (placements == null || placements.Count == 0) return;
            foreach (PrefabPlacement placement in placements)
                if (placement != null && (placement.Kind == "Balls" || placement.Kind == "Ramp"))
                    _pendingPresetPlacements.Add(placement.Copy());
            if (_pendingPresetPlacements.Count == 0) return;
            _pendingPresetStartedAt = Game.GameTime;
            _streamEnabled = true;
            _streamToggle.Title = "~r~Выключить стриминг";
            _streamToggle.Description = "Остановить поток и удалить его объекты";
            _streamCheckAt = 0;
        }

        private void BuildPendingPresetGroups()
        {
            if (_pendingPresetPlacements.Count == 0) return;
            int rampHash = unchecked((int)StringHash.AtStringHash(RampModelName));
            int completed = 0;
            for (int i = 0; i < _pendingPresetPlacements.Count && completed < 4;)
            {
                PrefabPlacement placement = _pendingPresetPlacements[i];
                if (placement.Kind == "Ramp" && !Function.Call<bool>(Hash.HAS_MODEL_LOADED, rampHash))
                {
                    if (Game.GameTime - _pendingPresetStartedAt > 5000)
                    {
                        PluginLog.Error("PrefabStreaming", "Модель горки не загрузилась за 5 секунд");
                        _pendingPresetPlacements.RemoveAt(i);
                        completed++;
                        continue;
                    }
                    Function.Call(Hash.REQUEST_MODEL, rampHash);
                    i++;
                    continue;
                }
                try
                {
                    if (placement.Kind == "Balls") _streamGroups.Add(BuildPresetBallGroup(placement));
                    else _streamGroups.Add(BuildPresetRampGroup(placement));
                    _pendingPresetPlacements.RemoveAt(i);
                    completed++;
                }
                catch (Exception ex)
                {
                    PluginLog.Error("PrefabStreaming: BuildPreset", ex);
                    _pendingPresetPlacements.RemoveAt(i);
                    completed++;
                }
            }
        }

        private static StreamedGroup BuildPresetBallGroup(PrefabPlacement placement)
        {
            if (placement.Spacing <= 0f || placement.Radius <= 0f)
                throw new InvalidDataException("Invalid ball placement");
            var group = new StreamedGroup { PresetOwned = true };
            var center = new Vector3(placement.X, placement.Y, placement.Z);
            var right = new Vector3(placement.DirectionX, placement.DirectionY, 0f);
            for (int row = 0; row < Rows; row++)
            for (int column = 0; column < Columns; column++)
                group.Pieces.Add(new StreamedPiece
                {
                    Position = center + right * ((column - (Columns - 1) * 0.5f) * placement.Spacing) +
                        new Vector3(0f, 0f, placement.Radius + row * placement.Spacing)
                });
            return group;
        }

        private StreamedGroup BuildPresetRampGroup(PrefabPlacement placement)
        {
            Vector3 oldStart = _rampStart, oldDirection = _rampDirection, oldEnd = _rampEnd;
            float oldOffset = _rampEndOffsetFactor;
            float[] oldLateral = _rampLateralWaves, oldHeight = _rampHeightWaves;
            Vector3[] oldTangents = _rampPreviewTangents;
            float[] oldBanks = _rampPreviewBanks;
            bool oldAlongX = _rampAlongX;
            try
            {
                _rampStart = new Vector3(placement.X, placement.Y, placement.Z);
                _rampDirection = new Vector3(placement.DirectionX, placement.DirectionY, 0f);
                if (_rampDirection.Length() < 0.5f) throw new InvalidDataException("Invalid ramp direction");
                _rampDirection.Normalize();
                for (int attempt = 0; attempt < 8; attempt++)
                {
                    _rampEndOffsetFactor = ((float)_random.NextDouble() * 2f - 1f) * 0.08f;
                    _rampLateralWaves = null;
                    _rampHeightWaves = null;
                    Vector3[] points = BuildRampPreview();
                    if (points == null) continue;
                    var group = new StreamedGroup { Ramp = true, Generated = true, PresetOwned = true, AlongX = _rampAlongX };
                    for (int i = 1; i < points.Length; i++)
                        group.Pieces.Add(new StreamedPiece
                        {
                            Position = points[i], Tangent = _rampPreviewTangents[i], Bank = _rampPreviewBanks[i]
                        });
                    return group;
                }
                throw new InvalidOperationException("Не удалось построить форму горки");
            }
            finally
            {
                _rampStart = oldStart; _rampDirection = oldDirection; _rampEnd = oldEnd;
                _rampEndOffsetFactor = oldOffset; _rampLateralWaves = oldLateral;
                _rampHeightWaves = oldHeight; _rampPreviewTangents = oldTangents;
                _rampPreviewBanks = oldBanks; _rampAlongX = oldAlongX;
            }
        }

        public bool BeginPresetPlacement(Action<PrefabPlacement> completed, Action cancelled)
        {
            if (!IsSpoonerActive() || _preview || _rampSelecting)
            {
                GTA.UI.Screen.ShowSubtitle("~y~Открой Object Spooner (F9) и повтори выбор префаба.", 3500);
                return false;
            }
            _presetPlacementCompleted = completed;
            _presetPlacementCancelled = cancelled;
            _presetMenuPending = true;
            _presetMenuRequestedAt = Game.GameTime;
            GTA.UI.Screen.ShowSubtitle("~b~Выбери стенку или горку; Enter запомнит место без создания объектов.", 4500);
            return true;
        }

        private bool CapturingPreset { get { return _presetPlacementCompleted != null; } }

        private void CompletePresetPlacement(PrefabPlacement placement)
        {
            Action<PrefabPlacement> completed = _presetPlacementCompleted;
            Action cancelled = _presetPlacementCancelled;
            _presetPlacementCompleted = null;
            _presetPlacementCancelled = null;
            _presetMenuPending = false;
            _preview = false;
            _rampSelecting = false;
            _menu.Visible = false;
            try { if (completed != null) completed(placement); }
            catch (Exception ex)
            {
                PluginLog.Error("PrefabStreaming: CompletePlacement", ex);
                if (cancelled != null) cancelled();
            }
        }

        private void CancelPresetPlacement(bool notify = true)
        {
            Action cancelled = _presetPlacementCancelled;
            _presetPlacementCompleted = null;
            _presetPlacementCancelled = null;
            _presetMenuPending = false;
            _menu.Visible = false;
            try { if (notify && cancelled != null) cancelled(); }
            catch (Exception ex) { PluginLog.Error("PrefabStreaming: CancelPlacement", ex); }
        }
        private sealed class StreamedPiece
        {
            public Vector3 Position;
            public Vector3 Tangent;
            public Vector3 Rotation;
            public bool UseRotation;
            public float Bank;
            public int Handle;
            public bool AwaitExit;
            public bool ReleaseQueued;
        }

        private sealed class StreamedGroup
        {
            public bool Ramp;
            public bool Generated;
            public bool PresetOwned;
            public bool AlongX;
            public readonly List<StreamedPiece> Pieces = new List<StreamedPiece>();
            public bool AwaitExit;
            public bool[] Touched;
            public int[] DeleteAt;
            public int UniqueTouches;
            public int FinalCleanupAt;
        }

        private sealed class PrefabStreamSettings
        {
            public int ScanRadius = 3000;
            public int LoadRadius = 80;
            public int UnloadRadius = 200;
            public int Limit = 1000;
        }

        private readonly List<StreamedGroup> _streamGroups = new List<StreamedGroup>();
        private readonly Queue<StreamedPiece> _streamBallRelease = new Queue<StreamedPiece>();
        private int _streamBallReleaseAt;
        private StreamedGroup _observedRampGroup;
        private readonly PrefabStreamSettings _streamSettings = new PrefabStreamSettings();
        private NativeMenu _streamMenu;
        private NativeMenu _streamSettingsMenu;
        private NativeItem _streamToggle;
        private NativeListItem<int> _streamUnloadList;
        private bool _streamEnabled;
        private int _streamCheckAt;
        private string StreamSettingsPath { get { return PluginStorage.FilePath("Prefabs", "PrefabStreaming.json"); } }

        private void InitializePrefabStreaming()
        {
            try
            {
                if (File.Exists(StreamSettingsPath))
                {
                    var saved = JsonConvert.DeserializeObject<PrefabStreamSettings>(File.ReadAllText(StreamSettingsPath));
                    if (saved != null)
                    {
                        _streamSettings.ScanRadius = saved.ScanRadius;
                        _streamSettings.LoadRadius = saved.LoadRadius;
                        _streamSettings.UnloadRadius = saved.UnloadRadius;
                        _streamSettings.Limit = saved.Limit;
                    }
                }
            }
            catch (Exception ex) { PluginLog.Error("PrefabStreaming: Settings", ex); }
            _streamSettings.ScanRadius = Math.Max(100, Math.Min(5000, _streamSettings.ScanRadius));
            _streamSettings.LoadRadius = Math.Max(10, Math.Min(2000, _streamSettings.LoadRadius));
            _streamSettings.UnloadRadius = Math.Max(_streamSettings.LoadRadius + 10, Math.Min(2500, _streamSettings.UnloadRadius));
            _streamSettings.Limit = Math.Max(50, Math.Min(1000, _streamSettings.Limit));

            _streamMenu = new NativeMenu("Стриминг префабов", "Горки и стенки мячиков");
            _streamSettingsMenu = new NativeMenu("Настройки стриминга", "Радиусы и лимит объектов");
            _streamToggle = new NativeItem("~g~Включить стриминг", "Запомнить поставленные горки и мячики");
            _streamToggle.Activated += (s, e) =>
            {
                if (_streamEnabled) StopPrefabStreaming();
                else StartPrefabStreaming();
            };
            _streamMenu.Add(_streamToggle);
            var settings = new NativeItem("~b~Настройки", "Радиусы и лимит объектов");
            settings.Activated += (s, e) =>
            {
                _streamSettingsMenu.Visible = true;
                _streamMenu.Visible = false;
            };
            _streamMenu.Add(settings);
            _streamSettingsMenu.Closed += (s, e) => { _streamMenu.Visible = true; };
            AddStreamSetting("Радиус сканирования", 100, 5000, 100, _streamSettings.ScanRadius,
                value => _streamSettings.ScanRadius = value);
            AddStreamSetting("Радиус загрузки", 10, 2000, 10, _streamSettings.LoadRadius,
                value => _streamSettings.LoadRadius = value);
            AddStreamSetting("Радиус выгрузки", 20, 2500, 10, _streamSettings.UnloadRadius,
                value => _streamSettings.UnloadRadius = value);
            AddStreamSetting("Лимит объектов", 50, 1000, 50, _streamSettings.Limit,
                value => _streamSettings.Limit = value);
            _pool.Add(_streamMenu);
            _pool.Add(_streamSettingsMenu);
            MenuRegistry.Register("PrefabStreamer", _streamMenu);
            MenuRegistry.Register("PrefabStreamerSettings", _streamSettingsMenu);
        }

        private void AddStreamSetting(string title, int min, int max, int step, int current, Action<int> changed)
        {
            var values = Enumerable.Range(0, (max - min) / step + 1).Select(i => min + i * step).ToArray();
            var item = new NativeListItem<int>(title, values);
            if (title == "Радиус выгрузки") _streamUnloadList = item;
            int index = Array.IndexOf(values, current);
            item.SelectedIndex = index >= 0 ? index : 0;
            item.ItemChanged += (s, e) =>
            {
                changed(item.SelectedItem);
                if (_streamSettings.UnloadRadius <= _streamSettings.LoadRadius)
                {
                    _streamSettings.UnloadRadius = _streamSettings.LoadRadius + 10;
                    if (_streamUnloadList != null)
                        _streamUnloadList.SelectedIndex = Array.IndexOf(
                            _streamUnloadList.Items.ToArray(), _streamSettings.UnloadRadius);
                }
                try { SafeFiles.Write(StreamSettingsPath, JsonConvert.SerializeObject(_streamSettings, Formatting.Indented),
                    text => { if (JsonConvert.DeserializeObject<PrefabStreamSettings>(text) == null) throw new InvalidDataException(); }); }
                catch (Exception ex) { PluginLog.Error("PrefabStreaming: SaveSettings", ex); }
            };
            _streamSettingsMenu.Add(item);
        }

        private void RememberBallWall(List<int> handles)
        {
            var group = new StreamedGroup();
            int index = 0;
            for (int row = 0; row < Rows; row++)
            for (int column = 0; column < Columns; column++)
                group.Pieces.Add(new StreamedPiece { Position = BallPosition(column, row), Handle = handles[index++] });
            _streamGroups.Add(group);
        }

        private void RememberRamp(List<int> handles, Vector3[] points, Vector3[] tangents, float[] banks, bool alongX)
        {
            // The first segment is deliberately removed by the placement tool.
            _streamGroups.RemoveAll(existing => existing.Ramp && existing.Generated);
            var group = new StreamedGroup { Ramp = true, Generated = true, AlongX = alongX };
            for (int i = 1; i < handles.Count; i++)
                group.Pieces.Add(new StreamedPiece
                {
                    Position = points[i], Tangent = tangents[i], Bank = banks[i], Handle = handles[i]
                });
            _streamGroups.Add(group);
            _observedRampGroup = group;
        }

        private void StartPrefabStreaming()
        {
            if (_pendingRelease.Count != 0)
            {
                GTA.UI.Screen.ShowSubtitle("~y~Дождись окончания сборки мячиков.", 2500);
                return;
            }
            Ped player = Game.Player.Character;
            if (player == null || !player.Exists()) return;
            Vector3 origin = player.Position;
            float scanSquared = _streamSettings.ScanRadius * _streamSettings.ScanRadius;
            _streamGroups.RemoveAll(group => !group.Pieces.Any(piece => DistanceSquared(origin, piece.Position) <= scanSquared));
            try { CaptureExistingPrefabs(origin, scanSquared); }
            catch (Exception ex)
            {
                PluginLog.Error("PrefabStreaming: Capture", ex);
                GTA.UI.Screen.ShowSubtitle("~r~Не удалось проверить объекты: " + ex.Message, 3500);
                return;
            }
            if (_streamGroups.Count == 0)
            {
                GTA.UI.Screen.ShowSubtitle("~y~Поставленные горки и мячики в радиусе сканирования не найдены.", 3500);
                return;
            }
            _streamEnabled = true;
            _streamToggle.Title = "~r~Выключить стриминг";
            _streamToggle.Description = "Остановить поток и удалить его объекты";
            _streamCheckAt = 0;
            int count = _streamGroups.Sum(group => group.Pieces.Count);
            GTA.UI.Screen.ShowSubtitle("~g~Стриминг префабов включён: ~w~" + count + " объектов.", 2500);
        }

        private void CaptureExistingPrefabs(Vector3 origin, float scanSquared)
        {
            int rampHash = unchecked((int)StringHash.AtStringHash(RampModelName));
            var known = new HashSet<int>(_streamGroups.SelectMany(g => g.Pieces).Select(p => p.Handle));
            var balls = new StreamedGroup();
            var ramps = new List<StreamedPiece>();
            Prop[] props = World.GetAllProps(new Model(BallHash), new Model(rampHash));
            foreach (Prop prop in props)
            {
                if (prop == null || !prop.Exists() || known.Contains(prop.Handle) ||
                    DistanceSquared(origin, prop.Position) > scanSquared ||
                    Function.Call<bool>(Hash.IS_ENTITY_ATTACHED, prop.Handle)) continue;
                int model = prop.Model.Hash;
                var piece = new StreamedPiece { Position = prop.Position, Handle = prop.Handle };
                if (model == BallHash) balls.Pieces.Add(piece);
                else if (model == rampHash)
                {
                    piece.Rotation = prop.Rotation;
                    piece.UseRotation = true;
                    ramps.Add(piece);
                }
            }
            if (balls.Pieces.Count > 0) _streamGroups.Add(balls);
            // Separate distant ramps so each can unload when the player leaves it.
            while (ramps.Count > 0)
            {
                var group = new StreamedGroup { Ramp = true };
                group.Pieces.Add(ramps[0]);
                ramps.RemoveAt(0);
                for (int i = 0; i < group.Pieces.Count; i++)
                    for (int j = ramps.Count - 1; j >= 0; j--)
                        if (DistanceSquared(group.Pieces[i].Position, ramps[j].Position) <= 400f)
                        {
                            group.Pieces.Add(ramps[j]);
                            ramps.RemoveAt(j);
                        }
                group.Pieces.Sort((a, b) => b.Position.Z.CompareTo(a.Position.Z));
                // A lone halfpipe can be part of the ordinary map, not a placed slide.
                if (group.Pieces.Count >= 8) _streamGroups.Add(group);
            }
        }

        private static float DistanceSquared(Vector3 a, Vector3 b)
        {
            float x = a.X - b.X, y = a.Y - b.Y;
            return x * x + y * y;
        }

        private static bool ExistsAs(int handle, int model)
        {
            return handle != 0 && Function.Call<bool>(Hash.DOES_ENTITY_EXIST, handle) &&
                Function.Call<int>(Hash.GET_ENTITY_MODEL, handle) == model;
        }

        private static void DeleteOwned(StreamedPiece piece, int model)
        {
            if (ExistsAs(piece.Handle, model))
            {
                Entity entity = Entity.FromHandle(piece.Handle);
                if (entity != null && entity.Exists()) entity.Delete();
            }
            piece.Handle = 0;
        }

        private void StopPrefabStreaming()
        {
            _pendingPresetPlacements.Clear();
            if (!_streamEnabled) return;
            int rampHash = unchecked((int)StringHash.AtStringHash(RampModelName));
            foreach (StreamedGroup group in _streamGroups)
                foreach (StreamedPiece piece in group.Pieces)
                    try { DeleteOwned(piece, group.Ramp ? rampHash : BallHash); }
                    catch (Exception ex) { PluginLog.Error("PrefabStreaming: Stop", ex); }
            _streamGroups.Clear();
            _streamBallRelease.Clear();
            _rampPieces.Clear();
            _observedRampGroup = null;
            _rampTouched = null;
            _rampDeleteAt = null;
            _rampPiecePositions = null;
            _streamEnabled = false;
            if (_streamToggle != null)
            {
                _streamToggle.Title = "~g~Включить стриминг";
                _streamToggle.Description = "Запомнить поставленные горки и мячики";
            }
        }

        private void UpdatePrefabStreaming()
        {
            ReleaseStreamedBall();
            if (!_streamEnabled || Game.GameTime - _streamCheckAt < 500) return;
            _streamCheckAt = Game.GameTime;
            BuildPendingPresetGroups();
            Ped player = Game.Player.Character;
            if (player == null || !player.Exists()) return;
            Vector3 position = player.Position;
            float load = _streamSettings.LoadRadius * _streamSettings.LoadRadius;
            float unload = _streamSettings.UnloadRadius * _streamSettings.UnloadRadius;
            int rampHash = unchecked((int)StringHash.AtStringHash(RampModelName));
            int live = _streamGroups.Sum(g => g.Pieces.Count(p => ExistsAs(p.Handle, g.Ramp ? rampHash : BallHash)));
            foreach (StreamedGroup group in _streamGroups)
            {
                try
                {
                    if (group.Ramp)
                    {
                        if (group.PresetOwned) UpdatePresetRampCleanup(group, player);
                        float nearest = group.Pieces.Min(p => DistanceSquared(position, p.Position));
                        if (IsObservedRamp(group) && _rampTouched != null && _rampUniqueTouches > 0 &&
                            group.Pieces.All(p => !ExistsAs(p.Handle, rampHash)))
                            group.AwaitExit = true;
                        if (nearest > unload)
                        {
                            foreach (StreamedPiece piece in group.Pieces) DeleteOwned(piece, rampHash);
                            group.AwaitExit = false;
                            group.Touched = null;
                            group.DeleteAt = null;
                            group.UniqueTouches = 0;
                            group.FinalCleanupAt = 0;
                            if (IsObservedRamp(group)) ResetRampObservation();
                        }
                        else if (nearest <= load && !group.AwaitExit)
                        {
                            int current = group.Pieces.Count(p => ExistsAs(p.Handle, rampHash));
                            if (current == 0 && live + group.Pieces.Count <= _streamSettings.Limit &&
                                Function.Call<bool>(Hash.HAS_MODEL_LOADED, rampHash))
                            {
                                foreach (StreamedPiece piece in group.Pieces)
                                {
                                    int handle = Function.Call<int>(Hash.CREATE_OBJECT_NO_OFFSET, rampHash,
                                        piece.Position.X, piece.Position.Y, piece.Position.Z, false, false, false);
                                    if (handle == 0) throw new InvalidOperationException("Не удалось восстановить горку");
                                    piece.Handle = handle;
                                    Function.Call(Hash.FREEZE_ENTITY_POSITION, handle, true);
                                    if (piece.UseRotation) Function.Call(Hash.SET_ENTITY_ROTATION, handle,
                                        piece.Rotation.X, piece.Rotation.Y, piece.Rotation.Z, 2, true);
                                    else SetRampOrientation(handle, piece.Tangent, group.AlongX, piece.Bank);
                                    live++;
                                }
                                if (group.PresetOwned)
                                {
                                    group.Touched = new bool[group.Pieces.Count];
                                    group.DeleteAt = new int[group.Pieces.Count];
                                    group.UniqueTouches = 0;
                                    group.FinalCleanupAt = 0;
                                }
                                else if (group.Generated || _rampPieces.Count == 0) RestoreRampObservation(group);
                            }
                            else if (current == 0) Function.Call(Hash.REQUEST_MODEL, rampHash);
                        }
                        if (group.Pieces.All(p => !ExistsAs(p.Handle, rampHash)) && nearest <= unload)
                            group.AwaitExit = true;
                    }
                    else
                    {
                        foreach (StreamedPiece piece in group.Pieces)
                        {
                            bool exists = ExistsAs(piece.Handle, BallHash);
                            float anchorDistance = DistanceSquared(position, piece.Position);
                            if (exists)
                            {
                                Vector3 actual = Function.Call<Vector3>(Hash.GET_ENTITY_COORDS, piece.Handle, true);
                                if (DistanceSquared(position, actual) > unload)
                                {
                                    DeleteOwned(piece, BallHash);
                                    piece.AwaitExit = anchorDistance <= unload;
                                    live--;
                                }
                            }
                            else if (anchorDistance > unload) piece.AwaitExit = false;
                            else if (anchorDistance <= load && !piece.AwaitExit && live < _streamSettings.Limit)
                            {
                                if (!Function.Call<bool>(Hash.HAS_MODEL_LOADED, BallHash))
                                    Function.Call(Hash.REQUEST_MODEL, BallHash);
                                else
                                {
                                    int handle = Function.Call<int>(Hash.CREATE_OBJECT_NO_OFFSET, BallHash,
                                        piece.Position.X, piece.Position.Y, piece.Position.Z, false, false, false);
                                    if (handle == 0) throw new InvalidOperationException("Не удалось восстановить мячик");
                                    piece.Handle = handle;
                                    Function.Call(Hash.FREEZE_ENTITY_POSITION, handle, true);
                                    Function.Call(Hash.SET_ENTITY_DYNAMIC, handle, true);
                                    if (!piece.ReleaseQueued)
                                    {
                                        piece.ReleaseQueued = true;
                                        if (_streamBallRelease.Count == 0) _streamBallReleaseAt = Game.GameTime + 150;
                                        _streamBallRelease.Enqueue(piece);
                                    }
                                    live++;
                                }
                            }
                        }
                    }
                }
                catch (Exception ex) { PluginLog.Error("PrefabStreaming: Update", ex); }
            }
        }

        private void UpdatePresetRampCleanup(StreamedGroup group, Ped player)
        {
            if (group.Touched == null || group.DeleteAt == null) return;
            int now = Game.GameTime;
            int rampHash = unchecked((int)StringHash.AtStringHash(RampModelName));
            if (player != null && player.Exists() && player.IsAlive)
            {
                for (int i = 0; i < group.Pieces.Count; i++)
                {
                    StreamedPiece piece = group.Pieces[i];
                    if (group.Touched[i] || !ExistsAs(piece.Handle, rampHash) ||
                        !Function.Call<bool>(Hash.IS_ENTITY_TOUCHING_ENTITY, player.Handle, piece.Handle)) continue;
                    group.Touched[i] = true;
                    group.UniqueTouches++;
                    for (int j = 0; j <= i; j++)
                        if (group.DeleteAt[j] == 0) group.DeleteAt[j] = now + 5000;
                    if (group.UniqueTouches == 6) group.FinalCleanupAt = now + 60000;
                }
            }
            for (int i = 0; i < group.Pieces.Count; i++)
                if ((group.DeleteAt[i] != 0 && now >= group.DeleteAt[i]) ||
                    (group.FinalCleanupAt != 0 && now >= group.FinalCleanupAt))
                    DeleteOwned(group.Pieces[i], rampHash);
            if (group.UniqueTouches > 0 && group.Pieces.All(p => !ExistsAs(p.Handle, rampHash)))
                group.AwaitExit = true;
        }

        private void ReleaseStreamedBall()
        {
            if (!_streamEnabled || _streamBallRelease.Count == 0 || Game.GameTime < _streamBallReleaseAt) return;
            StreamedPiece piece = _streamBallRelease.Dequeue();
            piece.ReleaseQueued = false;
            if (ExistsAs(piece.Handle, BallHash))
            {
                Function.Call(Hash.SET_ENTITY_VELOCITY, piece.Handle, 0f, 0f, 0f);
                Function.Call(Hash.FREEZE_ENTITY_POSITION, piece.Handle, false);
            }
            if (_streamBallRelease.Count > 0) _streamBallReleaseAt = Game.GameTime + 16;
        }

        private void ResetRampObservation()
        {
            _rampPieces.Clear();
            _observedRampGroup = null;
            _rampTouched = null;
            _rampDeleteAt = null;
            _rampPiecePositions = null;
            _rampFinalCleanupAt = 0;
            _rampUniqueTouches = 0;
        }

        private bool IsObservedRamp(StreamedGroup group)
        {
            return ReferenceEquals(_observedRampGroup, group);
        }

        private void RestoreRampObservation(StreamedGroup group)
        {
            _observedRampGroup = group;
            _rampPieces.Clear();
            _rampPieces.Add(0);
            _rampPieces.AddRange(group.Pieces.Select(p => p.Handle));
            _rampPiecePositions = new Vector3[_rampPieces.Count];
            for (int i = 0; i < group.Pieces.Count; i++) _rampPiecePositions[i + 1] = group.Pieces[i].Position;
            _rampTouched = new bool[_rampPieces.Count];
            _rampDeleteAt = new int[_rampPieces.Count];
            _rampUniqueTouches = 0;
            _rampFinalCleanupAt = 0;
            _rampContactCheckedAt = 0;
        }
    }
}
