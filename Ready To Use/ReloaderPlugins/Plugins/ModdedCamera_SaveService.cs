using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using GTA;
using GTA.Math;
using GTA.Native;
using LemonUI;
using LemonUI.Menus;

namespace ModdedCamera.Services
{
    public class SaveService
    {
        public enum SaveState { None, Typing, ConfirmOverwrite }
        public SaveState State { get; private set; }

        private readonly CameraService _cameraService;
        private string _pendingPathName = "";
        private long _nameInputTimer = 0;
        private const int NAME_INPUT_TIMEOUT = 60000;

        public event Action<string> OnPathSaved;
        public event Action<string> OnPathLoaded;
        public event Action<string> OnPathDeleted;
        public event Action<string> OnError;

        public SaveService(CameraService cameraService)
        {
            if (cameraService == null) throw new ArgumentNullException("cameraService");
            _cameraService = cameraService;
            State = SaveState.None;
        }

        public bool StartSave()
        {
            if (_cameraService.SplineCamera != null && _cameraService.SplineCamera.Nodes.Count < 2)
            {
                GTA.UI.Notification.PostTicker("Нужно минимум 2 узла!", false, false);
                return false;
            }

            State = SaveState.Typing;
            _nameInputTimer = Utils.NowMs();
            Function.Call(Hash.DISPLAY_ONSCREEN_KEYBOARD, 1, "FMMC_MPM_NA", "", "", "", "", "", 64);
            Logger.Info("SaveService: Save initiated");
            return true;
        }

        public void Update()
        {
            if (State == SaveState.None) return;
            try
            {
                if (State == SaveState.Typing)
                    UpdateTypingState();
                else if (State == SaveState.ConfirmOverwrite)
                    UpdateConfirmOverwriteState();
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "SaveService: Error in Update");
                State = SaveState.None;
            }
        }

        private void UpdateTypingState()
        {
            long currentTime = Utils.NowMs();
            long elapsed = currentTime - _nameInputTimer;
            if (elapsed < 0) elapsed = int.MaxValue;
            if (elapsed > NAME_INPUT_TIMEOUT)
            {
                Logger.Warn("SaveService: Name input timed out");
                State = SaveState.None;
                return;
            }

            int status = Function.Call<int>(Hash.UPDATE_ONSCREEN_KEYBOARD);
            if (status == 0) return;
            if (status == 2)
            {
                Logger.Info("SaveService: Save cancelled");
                State = SaveState.None;
                return;
            }

            string text = Function.Call<string>(Hash.GET_ONSCREEN_KEYBOARD_RESULT);
            if (string.IsNullOrEmpty(text))
            {
                State = SaveState.None;
                return;
            }

            if (PathManager.PathExists(text))
            {
                _pendingPathName = text;
                State = SaveState.ConfirmOverwrite;
                GTA.UI.Notification.PostTicker("~r~Имя уже существует! ~y~Пробел~w~=перезаписать, ~b~B~w~=переименовать", false, false);
                return;
            }

            DoSave(text);
            State = SaveState.None;
        }

        private void UpdateConfirmOverwriteState()
        {
            GTA.UI.Notification.PostTicker("~r~'" + _pendingPathName + "' уже существует! ~y~Пробел~w~=перезаписать, ~r~B~w~=переименовать", false, false);
            if (Game.IsControlJustPressed((GTA.Control)223))
            {
                DoSave(_pendingPathName);
                State = SaveState.None;
            }
            else if (Game.IsControlJustPressed(GTA.Control.FrontendAccept))
            {
                State = SaveState.Typing;
                _nameInputTimer = Utils.NowMs();
                Function.Call(Hash.DISPLAY_ONSCREEN_KEYBOARD, 1, "FMMC_MPM_NA", "", _pendingPathName, "", "", "", 64);
            }
            else if (Game.IsControlJustPressed(GTA.Control.FrontendCancel) || Game.IsControlJustPressed(GTA.Control.FrontendPause))
            {
                Logger.Info("SaveService: Overwrite cancelled");
                State = SaveState.None;
                _pendingPathName = "";
            }
        }

        private void DoSave(string name)
        {
            try
            {
                if (_cameraService.SplineCamera == null)
                {
                    GTA.UI.Notification.PostTicker("~r~SplineCamera не найдена!", false, false);
                    Logger.Error("DoSave: SplineCamera is null");
                    return;
                }

                var positions = _cameraService.SplineCamera.GetPositions();
                var rotations = _cameraService.SplineCamera.GetRotations();
                var durations = _cameraService.SplineCamera.GetDurations();
                var nodeModes = _cameraService.SplineCamera.GetNodeInterpolationModes();
                var nodeColors = _cameraService.SplineCamera.GetNodeColors();

                if (positions.Count < 2)
                {
                    GTA.UI.Notification.PostTicker("~r~Нужно минимум 2 узла!", false, false);
                    Logger.Warn("DoSave: Insufficient nodes");
                    return;
                }

                CameraPath cp = new CameraPath(
                    name, positions, rotations, durations, nodeModes,
                    _cameraService.NodeDuration,
                    _cameraService.CurrentFov,
                    _cameraService.CurrentSpeed,
                    2
                );
                cp.NodeColors = new List<int>(nodeColors);
                cp.NodeFovs = new List<int>(_cameraService.SplineCamera.GetNodeFovs());

                string result = PathManager.SavePath(cp);
                if (result != null)
                {
                    GTA.UI.Notification.PostTicker("~g~Сохранено: " + name, false, false);
                    _pendingPathName = "";
                    Logger.Info("SaveService: Path saved: " + result);
                    if (OnPathSaved != null) OnPathSaved(name);
                }
                else
                {
                    GTA.UI.Notification.PostTicker("~r~Не удалось сохранить! Смотрите лог.", false, false);
                    Logger.Error("DoSave: PathManager.SavePath returned null");
                    if (OnError != null) OnError("Save failed");
                }
            }
            catch (Exception ex)
            {
                GTA.UI.Notification.PostTicker("~r~Ошибка: " + ex.Message, false, false);
                Logger.Error(ex, "SaveService: Error in DoSave");
                if (OnError != null) OnError(ex.Message);
            }
        }

        public bool LoadPath(string pathName)
        {
            try
            {
                CameraPath cp = PathManager.LoadPath(pathName);
                if (cp == null)
                {
                    GTA.UI.Notification.PostTicker("~r~Не удалось загрузить!", false, false);
                    if (OnError != null) OnError("Failed to load path");
                    return false;
                }

                bool success = _cameraService.LoadPath(cp);
                if (success)
                {
                    GTA.UI.Notification.PostTicker("~g~Загружено: " + pathName, false, false);
                    if (OnPathLoaded != null) OnPathLoaded(pathName);
                    Logger.Info("SaveService: Path loaded: " + pathName);
                }
                else
                {
                    GTA.UI.Notification.PostTicker("~r~Не удалось применить путь!", false, false);
                    if (OnError != null) OnError("Failed to apply path");
                }
                return success;
            }
            catch (Exception ex)
            {
                GTA.UI.Notification.PostTicker("~r~Failed to load!", false, false);
                Logger.Error(ex, "SaveService: Error loading path");
                if (OnError != null) OnError("Error loading path: " + ex.Message);
                return false;
            }
        }

        public bool DeletePath(string pathName)
        {
            try
            {
                bool success = PathManager.DeletePath(pathName);
                if (success)
                {
                    GTA.UI.Notification.PostTicker("~g~Удалено: " + pathName, false, false);
                    if (OnPathDeleted != null) OnPathDeleted(pathName);
                    Logger.Info("SaveService: Path deleted: " + pathName);
                }
                else
                {
                    GTA.UI.Notification.PostTicker("~r~Не удалось удалить!", false, false);
                    if (OnError != null) OnError("Failed to delete path");
                }
                return success;
            }
            catch (Exception ex)
            {
                GTA.UI.Notification.PostTicker("~r~Ошибка: " + ex.Message, false, false);
                Logger.Error(ex, "SaveService: Error deleting path");
                if (OnError != null) OnError(ex.Message);
                return false;
            }
        }

        public bool RenamePath(string oldName, string newName)
        {
            try
            {
                if (string.IsNullOrEmpty(newName))
                {
                    GTA.UI.Notification.PostTicker("~r~Имя не может быть пустым!", false, false);
                    return false;
                }
                bool success = PathManager.RenamePath(oldName, newName);
                if (success)
                {
                    GTA.UI.Notification.PostTicker("~g~Переименовано: " + oldName + " → " + newName, false, false);
                    Logger.Info("SaveService: Path renamed: " + oldName + " → " + newName);
                }
                else
                {
                    GTA.UI.Notification.PostTicker("~r~Не удалось переименовать!", false, false);
                    if (OnError != null) OnError("Failed to rename path");
                }
                return success;
            }
            catch (Exception ex)
            {
                GTA.UI.Notification.PostTicker("~r~Ошибка: " + ex.Message, false, false);
                Logger.Error(ex, "SaveService: Error renaming path");
                if (OnError != null) OnError(ex.Message);
                return false;
            }
        }

        public void Cancel()
        {
            State = SaveState.None;
            _pendingPathName = "";
        }
    }

}
