using System;
using GTA.Native;

namespace ModdedCamera
{
    public class AnalogStickChangedEventArgs : EventArgs
    {
        public int X { get; private set; }
        public int Y { get; private set; }

        public AnalogStickChangedEventArgs(int x, int y)
        {
            this.X = x;
            this.Y = y;
        }
    }

    public class ButtonPressedEventArgs : EventArgs
    {
        public int Value { get; private set; }

        public ButtonPressedEventArgs(int value)
        {
            this.Value = value;
        }
    }

    public class TriggerChangedEventArgs : EventArgs
    {
        public int Value { get; private set; }

        public TriggerChangedEventArgs(int value)
        {
            this.Value = value;
        }
    }

    public delegate void AnalogStickChangedEventHandler(object sender, AnalogStickChangedEventArgs e);
    public delegate void ButtonPressedEventHandler(object sender, ButtonPressedEventArgs e);
    public delegate void TriggerChangedEventHandler(object sender, TriggerChangedEventArgs e);

    namespace Gamepad
    {
        public class GamepadHandler
        {
            public event ButtonPressedEventHandler AButtonPressed;
            public event ButtonPressedEventHandler BButtonPressed;
            public event ButtonPressedEventHandler XButtonPressed;
            public event ButtonPressedEventHandler YButtonPressed;
            public event TriggerChangedEventHandler RightTriggerChanged;
            public event TriggerChangedEventHandler LeftTriggerChanged;
            public event ButtonPressedEventHandler RightBumperPressed;
            public event ButtonPressedEventHandler LeftBumperPressed;
            public event AnalogStickChangedEventHandler LeftStickChanged;
            public event AnalogStickChangedEventHandler RightStickChanged;
            public event ButtonPressedEventHandler LeftStickPressed;
            public event ButtonPressedEventHandler RightStickPressed;

            public void Update()
            {
                if (GetControlValue(220) != 127 || GetControlValue(221) != 127)
                    OnRightStickChanged(new AnalogStickChangedEventArgs(GetControlValue(220), GetControlValue(221)));
                if (GetControlValue(218) != 127 || GetControlValue(219) != 127)
                    OnLeftStickChanged(new AnalogStickChangedEventArgs(GetControlValue(218), GetControlValue(219)));
                if (GetControlInput(230))
                    OnLeftStickPressed(new ButtonPressedEventArgs(GetControlValue(230)));
                if (GetControlInput(231))
                    OnRightStickPressed(new ButtonPressedEventArgs(GetControlValue(231)));
                if (GetControlValue(229) > 127)
                    OnRightTriggerChanged(new TriggerChangedEventArgs(GetControlValue(229)));
                if (GetControlValue(228) > 127)
                    OnLeftTriggerChanged(new TriggerChangedEventArgs(GetControlValue(228)));
                if (GetControlInput(222))
                    OnYPressed(new ButtonPressedEventArgs(GetControlValue(222)));
                if (GetControlInput(223))
                    OnAPressed(new ButtonPressedEventArgs(GetControlValue(223)));
                if (GetControlInput(224))
                    OnXPressed(new ButtonPressedEventArgs(GetControlValue(224)));
                if (GetControlInput(225))
                    OnBPressed(new ButtonPressedEventArgs(GetControlValue(225)));
                if (GetControlInput(226))
                    OnLBPressed(new ButtonPressedEventArgs(GetControlValue(226)));
                if (GetControlInput(227))
                    OnRBPressed(new ButtonPressedEventArgs(GetControlValue(227)));
            }

            protected virtual void OnAPressed(ButtonPressedEventArgs e)
            {
                if (AButtonPressed != null) AButtonPressed(this, e);
            }

            protected virtual void OnBPressed(ButtonPressedEventArgs e)
            {
                if (BButtonPressed != null) BButtonPressed(this, e);
            }

            protected virtual void OnXPressed(ButtonPressedEventArgs e)
            {
                if (XButtonPressed != null) XButtonPressed(this, e);
            }

            protected virtual void OnYPressed(ButtonPressedEventArgs e)
            {
                if (YButtonPressed != null) YButtonPressed(this, e);
            }

            protected virtual void OnLBPressed(ButtonPressedEventArgs e)
            {
                if (LeftBumperPressed != null) LeftBumperPressed(this, e);
            }

            protected virtual void OnRBPressed(ButtonPressedEventArgs e)
            {
                if (RightBumperPressed != null) RightBumperPressed(this, e);
            }

            protected virtual void OnLeftTriggerChanged(TriggerChangedEventArgs e)
            {
                if (LeftTriggerChanged != null) LeftTriggerChanged(this, e);
            }

            protected virtual void OnRightTriggerChanged(TriggerChangedEventArgs e)
            {
                if (RightTriggerChanged != null) RightTriggerChanged(this, e);
            }

            protected virtual void OnLeftStickChanged(AnalogStickChangedEventArgs e)
            {
                if (LeftStickChanged != null) LeftStickChanged(this, e);
            }

            protected virtual void OnRightStickChanged(AnalogStickChangedEventArgs e)
            {
                if (RightStickChanged != null) RightStickChanged(this, e);
            }

            protected virtual void OnLeftStickPressed(ButtonPressedEventArgs e)
            {
                if (LeftStickPressed != null) LeftStickPressed(this, e);
            }

            protected virtual void OnRightStickPressed(ButtonPressedEventArgs e)
            {
                if (RightStickPressed != null) RightStickPressed(this, e);
            }

            private bool GetControlInput(int control)
            {
                return Function.Call<bool>(NativeHashes.IS_DISABLED_CONTROL_PRESSED, 0, control);
            }

            private int GetControlValue(int control)
            {
                // GET_CONTROL_NORMAL collided with GET_DISABLED_CONTROL_NORMAL in
                // NativeHashes. The selector disables player controls, so reading the
                // "disabled" control value is both correct and avoids a wrong native hash.
                return Function.Call<int>(NativeHashes.GET_DISABLED_CONTROL_NORMAL, 0, control);
            }

            public void Dispose()
            {
                this.AButtonPressed = null;
                this.BButtonPressed = null;
                this.XButtonPressed = null;
                this.YButtonPressed = null;
                this.LeftStickPressed = null;
                this.RightStickPressed = null;
                this.LeftStickChanged = null;
                this.RightStickChanged = null;
                this.LeftTriggerChanged = null;
                this.RightTriggerChanged = null;
            }
        }
    }
}
