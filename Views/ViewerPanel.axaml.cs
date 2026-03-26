using Avalonia.Controls;
using UniversalUmap.Rendering.Controls;

namespace UniversalUmap.Rendering.Views;

public partial class ViewerPanel : UserControl
{
    public VulkanViewerControl? Viewer => this.FindControl<VulkanViewerControl>("ViewportControl");

    public ViewerPanel()
    {
        InitializeComponent();
    }
}
