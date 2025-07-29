namespace EmbyIcons.Services
{
    internal interface IOverlayInfo
    {
        IconAlignment Alignment { get; }
        int Priority { get; }
        bool HorizontalLayout { get; }
    }
}
