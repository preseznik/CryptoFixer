using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace CryptoFixer.Controls;

/// <summary>
/// Items control for the List display mode. The default ItemsControl peer hides the content of its
/// items from UI Automation (they show up as off-screen, non-control elements), so screen readers
/// can't reach the combo boxes. This peer builds the tree from the visual tree instead, where each
/// <see cref="ProtocolCard"/> is a list item containing its real controls.
/// </summary>
public sealed class ProtocolCardList : ItemsControl
{
    protected override AutomationPeer OnCreateAutomationPeer() => new ListPeer(this);

    private sealed class ListPeer(ProtocolCardList owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.List;

        protected override string GetClassNameCore() => nameof(ProtocolCardList);
    }
}

/// <summary>Root element of one protocol in <see cref="ProtocolCardList"/>; exposed as a list item.</summary>
public sealed class ProtocolCard : Border
{
    protected override AutomationPeer OnCreateAutomationPeer() => new CardPeer(this);

    private sealed class CardPeer(ProtocolCard owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.ListItem;

        protected override string GetClassNameCore() => nameof(ProtocolCard);
    }
}
