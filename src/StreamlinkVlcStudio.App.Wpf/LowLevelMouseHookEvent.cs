namespace StreamlinkVlcStudio.App.Wpf;

internal readonly record struct LowLevelMouseHookEvent(int Message, int ScreenX, int ScreenY, int MouseData)
{
    public const int WmMouseMove = 0x0200;
    public const int WmLeftButtonDown = 0x0201;
    public const int WmLeftButtonUp = 0x0202;
    public const int WmRightButtonDown = 0x0204;
    public const int WmMouseWheel = 0x020A;

    public int WheelDelta => unchecked((short)((MouseData >> 16) & 0xFFFF));
}
