using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using XboxPc.Domain;

namespace XboxPc.DesktopApp;

/// <summary>Applies UI actions (from controller or remapped keyboard) to the focused element of a window.</summary>
public static class InputRouter
{
    /// <summary>Returns true if the action was handled.</summary>
    public static bool Apply(Window window, UiAction action, Action<int>? changePage = null, Action? back = null, Action? focusMenu = null)
    {
        ArgumentNullException.ThrowIfNull(window);
        var focused = Keyboard.FocusedElement as UIElement;
        if (focused is null || !window.IsAncestorOf((DependencyObject)focused))
        {
            // Nothing focused yet in this window: focus the first focusable element.
            window.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
            return true;
        }

        switch (action)
        {
            case UiAction.Up: return Move(focused, FocusNavigationDirection.Up);
            case UiAction.Down: return Move(focused, FocusNavigationDirection.Down);
            case UiAction.Left: return MoveHorizontal(focused, FocusNavigationDirection.Left);
            case UiAction.Right: return MoveHorizontal(focused, FocusNavigationDirection.Right);
            case UiAction.Accept: return Invoke(focused);
            case UiAction.Back:
                if (focused is ComboBox { IsDropDownOpen: true } open) { open.IsDropDownOpen = false; return true; }
                back?.Invoke();
                return back is not null;
            case UiAction.PreviousPage: changePage?.Invoke(-1); return changePage is not null;
            case UiAction.NextPage: changePage?.Invoke(1); return changePage is not null;
            case UiAction.Menu: focusMenu?.Invoke(); return focusMenu is not null;
            default: return false;
        }
    }

    private static bool Move(UIElement focused, FocusNavigationDirection direction)
    {
        if (focused is ComboBoxItem || focused is ComboBox { IsDropDownOpen: true }) return false; // let the dropdown handle it
        if (focused.MoveFocus(new TraversalRequest(direction))) return true;
        return focused.MoveFocus(new TraversalRequest(direction == FocusNavigationDirection.Up ? FocusNavigationDirection.Previous : FocusNavigationDirection.Next));
    }

    private static bool MoveHorizontal(UIElement focused, FocusNavigationDirection direction)
    {
        if (focused is Slider slider)
        {
            slider.Value = Math.Clamp(slider.Value + (direction == FocusNavigationDirection.Left ? -slider.SmallChange : slider.SmallChange), slider.Minimum, slider.Maximum);
            return true;
        }
        return focused.MoveFocus(new TraversalRequest(direction));
    }

    private static bool Invoke(UIElement focused)
    {
        switch (focused)
        {
            case RadioButton radio:
                radio.IsChecked = true;
                if (radio.Command?.CanExecute(radio.CommandParameter) == true) radio.Command.Execute(radio.CommandParameter);
                return true;
            case ToggleButton toggle:
                toggle.IsChecked = !(toggle.IsChecked ?? false);
                return true;
            case ButtonBase button:
                if (!button.IsEnabled) return true;
                if (button.Command is { } cmd)
                {
                    if (cmd.CanExecute(button.CommandParameter)) cmd.Execute(button.CommandParameter);
                }
                else button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button));
                return true;
            case ComboBox combo:
                combo.IsDropDownOpen = !combo.IsDropDownOpen;
                return true;
            case ComboBoxItem item:
                item.IsSelected = true;
                if (ItemsControl.ItemsControlFromItemContainer(item) is ComboBox owner) owner.IsDropDownOpen = false;
                return true;
            case ListBoxItem listItem:
                listItem.IsSelected = true;
                return true;
            default:
                return false;
        }
    }

    /// <summary>Text input owns arrows/Enter; remapped navigation must not steal them.</summary>
    public static bool IsTextInput(object? element) => element is TextBox or PasswordBox;
}
