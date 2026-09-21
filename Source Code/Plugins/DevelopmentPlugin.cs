using System;
using System.Drawing;
using System.IO;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using GTA;
using LemonUI;
using LemonUI.Menus;
using ModPack;

public class DevelopmentPlugin : IGtaPlugin
{
    private readonly ObjectPool _pool = new ObjectPool();
    private NativeMenu _menu;
    private NativeCheckboxItem _enabled, _panel;
    private long _nextPanelUpdate;
    private GTA.UI.TextElement _text;
    private readonly string _path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ReloaderPlugins", "DevelopmentSettings.json");
    public sealed class Settings { public bool Enabled { get; set; } }
    private readonly JavaScriptSerializer _json = new JavaScriptSerializer();
    private void Validate(string text)
    {
        if (_json.Deserialize<Settings>(text) == null) throw new InvalidDataException("Invalid development settings");
    }
    public void OnStart()
    {
        DevelopmentDiagnostics.BeginSession();
        try
        {
            if (SafeFiles.Exists(_path))
                DevelopmentDiagnostics.Enabled = _json.Deserialize<Settings>(SafeFiles.Read(_path, Validate)).Enabled;
        }
        catch (Exception ex) { PluginLogging.PluginLog.Error("Development settings", ex); }
        _menu = new NativeMenu("ModPack", "Разработка");
        _enabled = new NativeCheckboxItem("Режим разработчика", DevelopmentDiagnostics.Enabled);
        _panel = new NativeCheckboxItem("Показывать панель", false);
        _enabled.CheckboxChanged += delegate {
            DevelopmentDiagnostics.Enabled = _enabled.Checked;
            if (!_enabled.Checked) { _panel.Checked = false; DevelopmentDiagnostics.PanelVisible = false; }
            try { SafeFiles.Write(_path, _json.Serialize(new Settings { Enabled = _enabled.Checked }), Validate); }
            catch (Exception ex) { PluginLogging.PluginLog.Error("Development settings save", ex); }
        };
        _panel.CheckboxChanged += delegate { DevelopmentDiagnostics.PanelVisible = _panel.Checked; };
        _menu.Add(_enabled);
        _menu.Add(_panel);
        _pool.Add(_menu);
    }
    public void OnTick()
    {
        _pool.Process();
        if (!DevelopmentDiagnostics.Enabled || !DevelopmentDiagnostics.PanelVisible) return;
        if (_text == null || DevelopmentDiagnostics.Now >= _nextPanelUpdate)
        {
            _nextPanelUpdate = DevelopmentDiagnostics.Now + 200;
            _text = new GTA.UI.TextElement(DevelopmentDiagnostics.Panel(ControlSession.Owner),
                new PointF(24, 220), 0.28f) { Color = Color.White, Outline = true };
        }
        _text.Draw();
    }
    public void OnKeyDown(Keys key)
    {
        if (key == Keys.F10 && Game.IsKeyPressed(Keys.ControlKey) && _menu != null) _menu.Visible = !_menu.Visible;
    }
    public void OnAbort()
    {
        DevelopmentDiagnostics.PanelVisible = false;
        if (_menu != null) _menu.Visible = false;
        _text = null;
    }
}
