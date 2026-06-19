using System.Collections.Generic;

namespace UniversalUmap.Rendering.Scenes;

public abstract class SceneObject
{
    public string Name { get; }
    public SceneObject? Parent { get; private set; }
    private readonly List<SceneObject> children = [];
    public IReadOnlyList<SceneObject> Children => children;

    protected SceneObject(string name)
    {
        Name = name;
    }

    public void AddChild(SceneObject child)
    {
        child.Parent = this;
        children.Add(child);
    }
}
