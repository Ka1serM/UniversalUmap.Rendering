using Avalonia.Controls;

namespace UniversalUmap.Rendering.Controls;

/// <summary>Details-panel row of labeled numeric fields (X/Y/Z, Pitch/Yaw/Roll, ...) - the widget shown for FVector/FRotator/etc. DetailItems.</summary>
public partial class VectorFieldControl : UserControl
{
    public VectorFieldControl()
    {
        InitializeComponent();
    }
}
