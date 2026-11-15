namespace Bifrost.Core.Models;

/// <summary>How a device paired (design §5, §7.3). Extensions treat <see cref="BrowserFallback"/> as view-only.</summary>
public enum DeviceKind
{
    App,
    BrowserFallback,
}
