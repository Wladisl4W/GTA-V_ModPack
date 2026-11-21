using System.Windows.Forms;

public interface IGtaPlugin
{
    void OnStart();
    void OnTick();
    void OnKeyDown(Keys key);
    void OnAbort();
}
