using System;
using System.Numerics;
using Avalonia;
using Avalonia.Input;
using Serilog;

namespace UniversalUmap.Rendering;

public sealed class Camera
{
    private const float FixedSensorWidthMm = 32f;
    private static readonly Vector3 WorldUp = new(0f, -1f, 0f);
    private static readonly Vector3 LocalForward = new(0f, 0f, 1f);
    private static readonly Vector3 LocalUp = new(0f, -1f, 0f);
    private static readonly Vector3 LocalRight = new(1f, 0f, 0f);
    private const float FlySensitivityDegrees = 0.1f;
    private const float SpeedBoostMultiplier = 10f;
    private const float DataEpsilon = 0.0001f;
    private const float WheelDollyScale = 0.8f;

    private readonly Input input;
    private CameraDataGpu data;
    private Vector3 position = new(0f, 0f, -2f);
    private Quaternion rotation = Quaternion.Identity;
    private PixelSize lastRenderSize = new(1, 1);
    private double lastInputLogSeconds;

    internal Camera(Input input)
    {
        this.input = input ?? throw new ArgumentNullException(nameof(input));
        data = new CameraDataGpu();
        RebuildData();
    }

    public float FocalLengthMm
    {
        get => data.FocalLength;
        set
        {
            var clamped = Math.Max(0.001f, value);
            if (MathF.Abs(data.FocalLength - clamped) <= DataEpsilon)
                return;

            data.FocalLength = clamped;
            RebuildData();
        }
    }

    public float Aperture
    {
        get => data.Aperture;
        set
        {
            var clamped = Math.Max(0f, value);
            if (MathF.Abs(data.Aperture - clamped) <= DataEpsilon)
                return;

            data.Aperture = clamped;
            RebuildData();
        }
    }

    public float FocusDistance
    {
        get => data.FocusDistance;
        set
        {
            var clamped = Math.Max(0.001f, value);
            if (MathF.Abs(data.FocusDistance - clamped) <= DataEpsilon)
                return;

            data.FocusDistance = clamped;
            RebuildData();
        }
    }

    public float BokehBias
    {
        get => data.BokehBias;
        set
        {
            var clamped = Math.Max(0.001f, value);
            if (MathF.Abs(data.BokehBias - clamped) <= DataEpsilon)
                return;

            data.BokehBias = clamped;
            RebuildData();
        }
    }

    public float MoveSpeed { get; private set; } = 12f;
    public Vector3 ArcballPivot { get; private set; } = Vector3.Zero;
    public Vector3 Position => position;
    public Quaternion Rotation => rotation;
    public int IsMoving { get; private set; }
    internal CameraDataGpu Data => data;
    internal event Action? Changed;

    public void SetArcballPivot(Vector3 pivot)
    {
        ArcballPivot = pivot;
        RebuildData();
    }

    public void OrbitAroundPivot(float yawRadians, float pitchRadians)
    {
        if (Math.Abs(yawRadians) < 0.000001f && Math.Abs(pitchRadians) < 0.000001f)
            return;

        var yawQuat = Quaternion.CreateFromAxisAngle(Vector3.Normalize(WorldUp), yawRadians);
        var localRight = Vector3.Transform(LocalRight, rotation);
        var pitchQuat = Quaternion.CreateFromAxisAngle(Vector3.Normalize(localRight), pitchRadians);
        var offset = position - ArcballPivot;
        var rotationDelta = Quaternion.Normalize(yawQuat * pitchQuat);
        offset = Vector3.Transform(offset, rotationDelta);
        position = ArcballPivot + offset;
        rotation = Quaternion.Normalize(yawQuat * pitchQuat * rotation);
        RebuildData();
    }

    public void PanInViewPlane(float deltaX, float deltaY)
    {
        if (Math.Abs(deltaX) < 0.000001f && Math.Abs(deltaY) < 0.000001f)
            return;

        var right = Vector3.Normalize(Vector3.Transform(LocalRight, rotation));
        var up = Vector3.Normalize(Vector3.Transform(LocalUp, rotation));
        var distance = Math.Max(1f, Vector3.Distance(position, ArcballPivot));
        var sensitivity = 0.0015f * distance;
        position += right * (-deltaX * sensitivity) + up * (deltaY * sensitivity);
        RebuildData();
    }

    public void Dolly(float amount)
    {
        if (Math.Abs(amount) < 0.000001f)
            return;

        var forward = Vector3.Normalize(Vector3.Transform(LocalForward, rotation));
        var distance = Math.Max(1f, Vector3.Distance(position, ArcballPivot));
        position += forward * (amount * Math.Max(0.25f, distance * 0.05f));
        RebuildData();
    }

    public void SetPosition(Vector3 value)
    {
        position = value;
        RebuildData();
    }

    public void SetRotation(Quaternion value)
    {
        rotation = Quaternion.Normalize(value);
        RebuildData();
    }

    public void Update(PixelSize renderSize, float deltaTimeSeconds)
    {
        lastRenderSize = renderSize;

        var wheelDelta = input.ConsumeWheelDelta();
        var mouseDelta = input.ConsumeMouseDelta();
        var leftMouseDown = input.LeftMouseDown;
        var rightMouseDown = input.RightMouseDown;
        var dragActive = leftMouseDown || rightMouseDown;
        var hasMovementKeys = input.IsKeyDown(Key.W) || input.IsKeyDown(Key.A) || input.IsKeyDown(Key.S) ||
                              input.IsKeyDown(Key.D) || input.IsKeyDown(Key.Q) || input.IsKeyDown(Key.E);
        var moving = false;

        if (Math.Abs(wheelDelta) > 0.0001f)
        {
            Dolly(wheelDelta * WheelDollyScale);
            moving = true;
        }

        if (dragActive)
            moving |= UpdateFly(mouseDelta, deltaTimeSeconds);

        if ((Math.Abs(wheelDelta) > 0.0001f || mouseDelta.LengthSquared() > 0.0001f || hasMovementKeys || dragActive) &&
            (System.Environment.TickCount64 / 1000.0 - lastInputLogSeconds) > 0.25)
        {
            lastInputLogSeconds = System.Environment.TickCount64 / 1000.0;
            Log.Debug(
                "Camera input frame: LMB={Lmb} RMB={Rmb} Fly={Fly} mouseDelta=({Dx:0.00},{Dy:0.00}) wheel={Wheel:0.00} moveKeys={MoveKeys} pos=({Px:0.00},{Py:0.00},{Pz:0.00})",
                leftMouseDown,
                rightMouseDown,
                dragActive,
                mouseDelta.X,
                mouseDelta.Y,
                wheelDelta,
                hasMovementKeys,
                position.X,
                position.Y,
                position.Z);
        }

        IsMoving = moving ? 1 : 0;
        UpdateData(renderSize);
    }

    internal static bool IsDataEquivalent(in CameraDataGpu a, in CameraDataGpu b)
    {
        var epsilonSq = DataEpsilon * DataEpsilon;
        return Vector3.DistanceSquared(a.Position, b.Position) <= epsilonSq &&
               Vector3.DistanceSquared(a.Direction, b.Direction) <= epsilonSq &&
               Vector3.DistanceSquared(a.Horizontal, b.Horizontal) <= epsilonSq &&
               Vector3.DistanceSquared(a.Vertical, b.Vertical) <= epsilonSq &&
               MathF.Abs(a.FocalLength - b.FocalLength) <= DataEpsilon &&
               MathF.Abs(a.FocusDistance - b.FocusDistance) <= DataEpsilon &&
               MathF.Abs(a.Aperture - b.Aperture) <= DataEpsilon &&
               MathF.Abs(a.BokehBias - b.BokehBias) <= DataEpsilon;
    }

    private void RebuildData() => UpdateData(lastRenderSize);

    private void UpdateData(PixelSize renderSize)
    {
        var width = Math.Max(1, renderSize.Width);
        var height = Math.Max(1, renderSize.Height);
        var aspectRatio = (float)width / height;
        var direction = Vector3.Normalize(Vector3.Transform(LocalForward, rotation));
        var right = Vector3.Normalize(Vector3.Transform(LocalRight, rotation));
        var up = Vector3.Normalize(Vector3.Transform(LocalUp, rotation));
        var sensorHeightMm = FixedSensorWidthMm / aspectRatio;

        var next = data;
        next.Position = position;
        next.Direction = direction;
        next.Horizontal = right * (FixedSensorWidthMm * 0.001f);
        next.Vertical = up * (sensorHeightMm * 0.001f);

        if (IsDataEquivalent(data, next))
            return;

        data = next;
        Changed?.Invoke();
    }

    private bool UpdateFly(Vector2 mouseDeltaPixels, float deltaTimeSeconds)
    {
        var positionBefore = position;
        var rotationBefore = rotation;

        var yaw = DegreesToRadians(-mouseDeltaPixels.X * FlySensitivityDegrees);
        var pitch = DegreesToRadians(-mouseDeltaPixels.Y * FlySensitivityDegrees);
        if (Math.Abs(yaw) > 0.000001f || Math.Abs(pitch) > 0.000001f)
        {
            var yawQuat = Quaternion.CreateFromAxisAngle(Vector3.Normalize(WorldUp), yaw);
            var rightDir = Vector3.Transform(LocalRight, rotation);
            var pitchQuat = Quaternion.CreateFromAxisAngle(Vector3.Normalize(rightDir), pitch);
            rotation = Quaternion.Normalize(yawQuat * pitchQuat * rotation);
        }

        var speed = deltaTimeSeconds * MoveSpeed;
        if (input.IsKeyDown(Key.LeftShift) || input.IsKeyDown(Key.RightShift))
            speed *= SpeedBoostMultiplier;

        var forward = Vector3.Normalize(Vector3.Transform(LocalForward, rotation));
        var up = Vector3.Normalize(Vector3.Transform(LocalUp, rotation));
        var right = Vector3.Normalize(Vector3.Cross(forward, up));

        if (input.IsKeyDown(Key.W))
            position += forward * speed;
        if (input.IsKeyDown(Key.S))
            position -= forward * speed;
        if (input.IsKeyDown(Key.A))
            position -= right * speed;
        if (input.IsKeyDown(Key.D))
            position += right * speed;
        if (input.IsKeyDown(Key.E))
            position += up * speed;
        if (input.IsKeyDown(Key.Q))
            position -= up * speed;

        return position != positionBefore || rotation != rotationBefore;
    }

    private static float DegreesToRadians(float degrees) => degrees * (MathF.PI / 180f);
}
