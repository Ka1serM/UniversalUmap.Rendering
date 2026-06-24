using System.Numerics;

namespace UniversalUmap.Rendering.Scenes;

public enum LightType : byte
{
    Directional,
    Point,
    Spot,
    Rect,
    Sky
}

public sealed class LightSceneObject : SceneObject
{
    public LightType LightType { get; }
    public Vector3 Color { get; set; }
    public float Intensity { get; set; }
    public Matrix4x4 WorldTransform { get; set; }

    public float Range { get; set; } = 100f;
    public float InnerConeAngle { get; set; }
    public float OuterConeAngle { get; set; }
    public float SourceRadius { get; set; }
    public float SourceLength { get; set; }
    public float SoftSourceRadius { get; set; }
    public float SourceWidth { get; set; }
    public float SourceHeight { get; set; }
    public bool UseInverseSquaredFalloff { get; set; } = true;
    public float LightFalloffExponent { get; set; } = 8f;
    public float LightSourceAngle { get; set; }
    public float LightSourceSoftAngle { get; set; }
    public float Temperature { get; set; } = 6500f;
    public bool UseTemperature { get; set; }

    internal int SceneIndex { get; set; } = -1;

    public LightSceneObject(
        string name,
        LightType lightType,
        Vector3 color,
        float intensity,
        Matrix4x4 worldTransform)
        : base(name)
    {
        LightType = lightType;
        Color = color;
        Intensity = intensity;
        WorldTransform = worldTransform;
    }

    public Vector3 Position => WorldTransform.Translation;

    public Vector3 Direction
    {
        get
        {
            var fwd = -Vector3.UnitZ;
            return Vector3.TransformNormal(fwd, WorldTransform);
        }
    }

    internal LightGpu BuildGpuData()
    {
        var dir = Direction;
        return new LightGpu
        {
            Position = Position,
            Type = (int)LightType,
            Direction = dir,
            Color = Color,
            Intensity = Intensity,
            Range = Range,
            InnerConeAngle = InnerConeAngle,
            OuterConeAngle = OuterConeAngle,
            SourceRadius = SourceRadius,
            SoftSourceRadius = SoftSourceRadius,
            SourceLength = SourceLength,
            SourceWidth = SourceWidth,
            SourceHeight = SourceHeight,
            LightSourceAngle = LightSourceAngle,
            LightSourceSoftAngle = LightSourceSoftAngle,
            LightFalloffExponent = LightFalloffExponent,
            UseInverseSquaredFalloff = UseInverseSquaredFalloff ? 1 : 0,
            Pad4 = 0,
        };
    }
}
