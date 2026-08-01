using System;
using System.Windows.Forms;
using GTA;
using GTA.Math;
using GTA.Native;
using ModdedCamera.Services;

namespace ModdedCamera
{
    public class ModdedCameraPlugin : IGtaPlugin
    {
        private CameraService _cameraService;
        private SaveService _saveService;
        private InputService _inputService;
        private MenuService _menuService;

        public void OnStart()
        {
            try
            {
                Logger.Info("=== ModdedCamera Mod Starting ===");

                _cameraService = new CameraService();
                _saveService = new SaveService(_cameraService);
                _inputService = new InputService();
                _menuService = new MenuService(_cameraService, _saveService, _inputService);

                WireInputEvents();
                WireServiceEvents();

                _cameraService.Initialize();
                _menuService.Initialize();
                _menuService.SyncCameraOptionsWithMenu();

                Logger.Info("=== ModdedCamera Mod Started Successfully ===");
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "CRITICAL: Error during mod initialization");
                GTA.UI.Notification.PostTicker("~r~ModdedCamera: Initialization failed! Check log.", false, false);
            }
        }

        private void WireInputEvents()
        {
            _inputService.OnToggleMenu += _menuService.ToggleMenu;
            _inputService.OnBackNavigation += OnBackNavigation;
            _inputService.OnAddNode += OnAddNode;
            _inputService.OnExitPointSelector += delegate { _cameraService.ExitPointSelector(); };
            _inputService.OnScrollDurationUp += OnScrollDurationUp;
            _inputService.OnScrollDurationDown += OnScrollDurationDown;
        }

        private void WireServiceEvents()
        {
            _saveService.OnPathSaved += delegate(string pathName) { _menuService.RefreshSavedPathsMenu(); };
            _saveService.OnPathDeleted += delegate(string pathName) { _menuService.RefreshSavedPathsMenu(); };
            _saveService.OnPathLoaded += delegate(string pathName) { _menuService.SyncCameraOptionsWithMenu(); };
        }

        public void OnTick()
        {
            try
            {
                if (!Game.Player.Character.Exists()) return;

                _saveService.Update();
                if (_saveService.State != SaveService.SaveState.None)
                {
                    _menuService.Process();
                    return;
                }

                bool pointSelectorActive = _cameraService.IsSelectorActive;
                bool menusVisible = _menuService.AreAnyVisible;

                if (pointSelectorActive && !menusVisible)
                {
                    _inputService.ProcessPointSelectorInput();
                }

                bool modActive = _cameraService.IsAnyCameraActive || menusVisible;
                if (modActive)
                {
                    _inputService.DisableInterferingControls();
                }

                _menuService.Process();
                _cameraService.Update();
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Error in OnTick");
            }
        }

        public void OnKeyDown(Keys key)
        {
            try
            {
                if (key == Keys.T)
                {
                    _inputService.ProcessKeyUp(key);
                    return;
                }

                if (key == Keys.Back)
                {
                    _inputService.ProcessKeyDown(key);
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Error in OnKeyDown");
            }
        }

        public void OnAbort()
        {
            try
            {
                Logger.Info("Disposing ModdedCamera...");
                if (_cameraService != null) _cameraService.Dispose();
                if (_menuService != null) _menuService.Dispose();
                Logger.Flush();
                Logger.Info("ModdedCamera disposed successfully.");
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Error during Dispose");
            }
        }

        private void OnBackNavigation()
        {
            Logger.Info("OnBackNavigation called");
            _menuService.HandleBackNavigation();
        }

        private void OnAddNode()
        {
            if (_cameraService.AddNodeAtCurrentPosition())
            {
                Vector3 pos = _cameraService.GetActiveCameraPosition();
                int durationMs = _menuService.GetNodeDuration();
                GTA.UI.Notification.PostTicker("Node added\nPos: (" + pos.X.ToString("F1") + ", " + pos.Y.ToString("F1") + ", " + pos.Z.ToString("F1") + ")\nDuration: " + ((float)durationMs / 1000f).ToString("F2") + "s", false, false);
            }
        }

        private void OnScrollDurationUp()
        {
            _menuService.IncreaseNodeDuration();
            ShowDurationNotification();
        }

        private void OnScrollDurationDown()
        {
            _menuService.DecreaseNodeDuration();
            ShowDurationNotification();
        }

        private void ShowDurationNotification()
        {
            int durationMs = _menuService.GetNodeDuration();
            GTA.UI.Screen.ShowSubtitle("Duration: " + ((float)durationMs / 1000f).ToString("F2") + "s", 1500);
        }
    }
}
