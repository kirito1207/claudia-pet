namespace Claudia
{
    sealed partial class Pet
    {
        // ---------------- 位置 ----------------
        // 记录离 Claude 窗口右下角多远（按物理像素），具体见 Pet.Follow.cs
        void RestorePosition() => PlaceInClaude();
        void SavePosition() => SaveFollowOffsets();
    }
}
