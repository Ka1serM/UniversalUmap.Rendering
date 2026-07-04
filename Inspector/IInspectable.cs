namespace UniversalUmap.Rendering.Inspector;

public interface IInspectable
{
    string InspectorTitle { get; }
}

public enum DetailEditorKind
{
    ReadOnlyText,
    Text,
    Number,
    Slider,
    Toggle,
    Enum,
    Color,
    Vector,
    ObjectReference
}
