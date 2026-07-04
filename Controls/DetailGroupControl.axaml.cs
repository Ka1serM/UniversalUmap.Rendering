using Avalonia;
using Avalonia.Controls;
using UniversalUmap.Rendering.Inspector;

namespace UniversalUmap.Rendering.Controls;

public partial class DetailGroupControl : UserControl
{
    public static readonly StyledProperty<string> GroupNameProperty =
        AvaloniaProperty.Register<DetailGroupControl, string>(nameof(GroupName));

    public string GroupName
    {
        get => GetValue(GroupNameProperty);
        set => SetValue(GroupNameProperty, value);
    }

    public DetailGroupControl()
    {
        InitializeComponent();
        GroupNameProperty.Changed.AddClassHandler<DetailGroupControl>((ctrl, _) =>
            ctrl.HeaderText.Text = ctrl.GroupName);
    }

    public void AddItem(string? label, Control editor)
    {
        var grid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(160, GridUnitType.Pixel),
                new ColumnDefinition(1, GridUnitType.Star)
            }
        };

        if (!string.IsNullOrEmpty(label))
        {
            var labelBlock = new TextBlock
            {
                Classes = { "renderLabel" },
                Text = label,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            };
            Grid.SetColumn(labelBlock, 0);
            grid.Children.Add(labelBlock);
            Grid.SetColumn(editor, 1);
            editor.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right;
        }
        else
        {
            Grid.SetColumnSpan(editor, 2);
            Grid.SetColumn(editor, 0);
        }

        grid.Children.Add(editor);

        ItemsPanel.Children.Add(grid);
    }

    public void AddSubGroup(DetailGroupControl subGroup)
    {
        ItemsPanel.Children.Add(subGroup);
    }
}
