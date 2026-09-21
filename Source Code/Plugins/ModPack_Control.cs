using System;
using GTA;
using GTA.Native;

namespace ModPack
{
    internal sealed class ControlSession : IDisposable
    {
        private static readonly ControlGate Gate = new ControlGate();
        private Ped _player;
        private bool _frozen, _visible, _collision, _invincible;
        private float _timeScale;
        private bool _changedTime, _changedGravity;
        public static string Owner { get { return Gate.Name; } }
        public bool Held { get { return Gate.Owns(this); } }
        public Ped Player { get { return _player; } }
        public float OriginalTimeScale { get { return _timeScale; } }
        public bool Valid
        {
            get { return Held && _player != null && _player.Exists() && !_player.IsDead &&
                Game.Player.Character != null && Game.Player.Character.Handle == _player.Handle; }
        }
        public bool Acquire(string name, bool manual)
        {
            if (Held) return Valid;
            if (!Gate.Acquire(this, name))
            {
                DevelopmentDiagnostics.Event(name, "Blocked by " + Owner);
                if (manual) GTA.UI.Screen.ShowSubtitle("~y~Управление занято: " + Owner, 2500);
                return false;
            }
            try
            {
                _player = Game.Player.Character;
                if (_player == null || !_player.Exists() || _player.IsDead) { Dispose(); return false; }
                _frozen = _player.IsPositionFrozen;
                _visible = _player.IsVisible;
                _collision = _player.IsCollisionEnabled;
                _invincible = _player.IsInvincible;
                _timeScale = Game.TimeScale;
                DevelopmentDiagnostics.Event(name, "Control acquired");
                return true;
            }
            catch { Gate.Release(this); _player = null; throw; }
        }
        public void TimeScale(float value)
        {
            if (!Held) return;
            _changedTime = true;
            Function.Call(Hash.SET_TIME_SCALE, value);
        }
        public void Gravity(int level)
        {
            if (!Held) return;
            _changedGravity = true;
            Function.Call(Hash.SET_GRAVITY_LEVEL, level);
        }
        public void Dispose()
        {
            if (!Held) return;
            try
            {
                if (_player != null && _player.Exists())
                {
                    Restore(delegate { _player.IsPositionFrozen = _frozen; });
                    Restore(delegate { _player.IsVisible = _visible; });
                    Restore(delegate { _player.IsCollisionEnabled = _collision; });
                    Restore(delegate { _player.IsInvincible = _invincible; });
                }
                if (_changedTime) Restore(delegate { Function.Call(Hash.SET_TIME_SCALE, _timeScale); });
                // GTA exposes no reliable getter for the gravity-level preset.
                if (_changedGravity) Restore(delegate { Function.Call(Hash.SET_GRAVITY_LEVEL, 0); });
                DevelopmentDiagnostics.Event(Owner, "Control released");
            }
            finally
            {
                Gate.Release(this);
                _player = null;
                _changedTime = _changedGravity = false;
            }
        }
        private static void Restore(Action action)
        {
            try { action(); }
            catch (Exception ex) { PluginLogging.PluginLog.Error("Control restore", ex); }
        }
    }
}
