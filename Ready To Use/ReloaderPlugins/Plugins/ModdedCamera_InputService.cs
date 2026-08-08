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
    public class InputService
    {
        private static readonly int[] WeaponControls = { 24, 25, 26, 45, 138, 140, 141, 142, 143, 241, 242 };

        public event Action OnToggleMenu;
        public event Action OnBackNavigation;
        public event Action OnAddNode;
        public event Action OnExitPointSelector;
        public event Action OnScrollDurationUp;
        public event Action OnScrollDurationDown;

        public void ProcessKeyUp(Keys key)
        {
            if (key == Keys.T)
            {
                if (OnToggleMenu != null) OnToggleMenu();
            }
        }

        public bool ProcessKeyDown(Keys key)
        {
            if (key == Keys.Back)
            {
                if (OnBackNavigation != null) OnBackNavigation();
                return true;
            }
            return false;
        }

        public void ProcessPointSelectorInput()
        {
            try
            {
                if (Function.Call<bool>(Hash.IS_DISABLED_CONTROL_JUST_PRESSED, 0, 24))
                {
                    if (OnAddNode != null) OnAddNode();
                }

                Function.Call(Hash.DISABLE_CONTROL_ACTION, 0, 241, true);
                Function.Call(Hash.DISABLE_CONTROL_ACTION, 0, 242, true);
                Function.Call(Hash.DISABLE_CONTROL_ACTION, 2, 241, true);
                Function.Call(Hash.DISABLE_CONTROL_ACTION, 2, 242, true);

                float scrollUp = Function.Call<float>(NativeHashes.GET_DISABLED_CONTROL_NORMAL, 0, 241);
                float scrollDown = Function.Call<float>(NativeHashes.GET_DISABLED_CONTROL_NORMAL, 0, 242);

                bool scrollUpPressed = scrollUp > 0.5f || Function.Call<bool>(Hash.IS_DISABLED_CONTROL_JUST_PRESSED, 0, 241);
                bool scrollDownPressed = scrollDown > 0.5f || Function.Call<bool>(Hash.IS_DISABLED_CONTROL_JUST_PRESSED, 0, 242);

                if (scrollUpPressed)
                {
                    if (OnScrollDurationUp != null) OnScrollDurationUp();
                }
                else if (scrollDownPressed)
                {
                    if (OnScrollDurationDown != null) OnScrollDurationDown();
                }

                if (Function.Call<bool>(Hash.IS_DISABLED_CONTROL_JUST_PRESSED, 0, 25))
                {
                    if (OnExitPointSelector != null) OnExitPointSelector();
                }
                else if (Game.IsControlJustPressed(GTA.Control.FrontendAccept))
                {
                    if (OnExitPointSelector != null) OnExitPointSelector();
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "InputService: Error in ProcessPointSelectorInput");
            }
        }

        public void DisableWeaponControls()
        {
            foreach (int control in WeaponControls)
            {
                Function.Call(Hash.DISABLE_CONTROL_ACTION, 0, control, true);
                Function.Call(Hash.DISABLE_CONTROL_ACTION, 2, control, true);
            }
        }

        public void DisableInterferingControls()
        {
            Function.Call(Hash.DISABLE_CONTROL_ACTION, 0, 199, true);
            Function.Call(Hash.DISABLE_CONTROL_ACTION, 0, 200, true);
            Function.Call(Hash.DISABLE_CONTROL_ACTION, 2, 199, true);
            Function.Call(Hash.DISABLE_CONTROL_ACTION, 2, 200, true);
        }
    }

}
