namespace Claudia
{
    sealed partial class Pet
    {
        // ---------------- 位置 ----------------
        // 窗口模式记离 Claude 窗口右下角多远，桌面模式记屏幕上的位置（都按物理像素），具体见 Pet.Follow.cs
        void RestorePosition() => PlaceHome();
        void SavePosition() { if (desktopMode) SaveDeskPosition(); else SaveFollowOffsets(); }
    }
}
