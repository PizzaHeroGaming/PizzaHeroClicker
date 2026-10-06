using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace PizzaHeroClicker.Controls;

/// <summary>Attached behaviour that restricts a TextBox to numeric characters (digits, minus, decimal point).</summary>
public static class NumericInput
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(NumericInput), new PropertyMetadata(false, OnChanged));

    public static bool GetIsEnabled(DependencyObject d) => (bool)d.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject d, bool value) => d.SetValue(IsEnabledProperty, value);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox box) return;
        box.PreviewTextInput -= OnTextInput;
        DataObject.RemovePastingHandler(box, OnPaste);
        if (e.NewValue is true)
        {
            box.PreviewTextInput += OnTextInput;
            DataObject.AddPastingHandler(box, OnPaste);
            InputMethod.SetIsInputMethodEnabled(box, false);
        }
    }

    private static bool IsNumeric(string text) => text.All(c => char.IsAsciiDigit(c) || c is '-' or '.');

    private static void OnTextInput(object sender, TextCompositionEventArgs e) => e.Handled = !IsNumeric(e.Text);

    private static void OnPaste(object sender, DataObjectPastingEventArgs e)
    {
        if (e.DataObject.GetData(DataFormats.UnicodeText) is not string text || !IsNumeric(text.Trim())) e.CancelCommand();
    }
}
