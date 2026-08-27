using Godot;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.TopBar;

namespace Sts2SpireRace.Replay;

public static class ReplayUiInteractionPolicy
{
    public static bool ShouldBlock(Control control)
    {
        return ReplayInputGate.BlockGameplayInput && !IsReadOnlyViewerControl(control);
    }

    public static void ApplyReadOnlyState(Node root)
    {
        if (!ReplayInputGate.BlockGameplayInput) return;
        ApplyRecursive(root);
    }

    private static void ApplyRecursive(Node node)
    {
        if (node is NClickableControl clickable && ShouldBlock(clickable) && clickable.IsEnabled)
            clickable.Disable();
        foreach (Node child in node.GetChildren()) ApplyRecursive(child);
    }

    private static bool IsReadOnlyViewerControl(Control control)
    {
        if (control is NTopBarMapButton or NTopBarDeckButton or NCombatCardPile)
            return true;
        if (control is NMapPoint)
            return false;
        for (Node? node = control; node != null; node = node.GetParent())
        {
            if (node is NDeckViewScreen or NCardPileScreen or NInspectCardScreen)
                return true;
            if (node is NMapScreen)
                return true;
        }
        return false;
    }
}
