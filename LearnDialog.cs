using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace HockeyRoller;

public class LearnDialog : Window
{
    private readonly List<ComboBox> _boxes = new();

    /// <param name="tierNames">the tiers the user can pick from</param>
    /// <param name="title">window title</param>
    /// <param name="hint">the instruction line</param>
    /// <param name="okText">label of the confirm button</param>
    public LearnDialog(string[] tierNames, string title = "Learn cards from region",
                        string hint = "Label the four cards (top-left, top-right, bottom-left, bottom-right).",
                        string okText = "Save references")
    {
        Title = title;
        Width = 420; Height = 340;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;

        var sp = new StackPanel { Margin = new Thickness(12) };
        sp.Children.Add(new TextBlock
        {
            Text = hint,
            Margin = new Thickness(0, 0, 0, 10),
            TextWrapping = TextWrapping.Wrap
        });

        string[] positions = { "Top left", "Top right", "Bottom left", "Bottom right" };
        for (int i = 0; i < 4; i++)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };
            row.Children.Add(new TextBlock { Text = positions[i], Width = 110, VerticalAlignment = VerticalAlignment.Center });
            var combo = new ComboBox { Width = 230 };
            combo.Items.Add("(skip)");
            foreach (var t in tierNames) combo.Items.Add(t);
            combo.SelectedIndex = 0;          // nothing is labelled until the user says so
            row.Children.Add(combo);
            _boxes.Add(combo);
            sp.Children.Add(row);
        }

        var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var ok = new Button { Content = okText, Width = 140, Margin = new Thickness(0, 0, 6, 0) };
        ok.Click += (s, e) => { DialogResult = true; Close(); };
        var cancel = new Button { Content = "Cancel", Width = 80 };
        cancel.Click += (s, e) => { DialogResult = false; Close(); };
        btns.Children.Add(ok); btns.Children.Add(cancel);
        sp.Children.Add(btns);
        Content = sp;
    }

    /// <summary>The tier picked for each card ("" when the card was skipped).</summary>
    public string[] Selected => _boxes.Select(b =>
    {
        var s = b.SelectedItem?.ToString() ?? "";
        return s == "(skip)" ? "" : s;
    }).ToArray();
}
