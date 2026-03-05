using System;
using System.Numerics;
using Avalonia;
using Avalonia.Input;
using Serilog;

namespace UniversalUmap.Rendering;

internal sealed class PerspectiveCamera
{
    private static readonly Vector3 WorldUp = new(0f, -1f, 0f);
    private static readonly Vector3 LocalForward = new(0f, 0f, 1f);
    private static readonly Vector3 LocalUp = new(0f, -1f, 0f);
    private static readonly Vector3 LocalRight = new(1f, 0f, 0f);
    private const float ArcballSensitivity = 0.004f;
    private const float FlySensitivityDegrees = 0.1f;
    private const float SpeedBoostMultiplier = 10f;
    private const float CameraDataEpsilon = 0.0001f;
    private const float WheelDollyScale = 0.8f;

    private Vector3 position = new(0f, 0f, -2f);
    private Quaternion rotation = Quaternion.Identity;
    private readonly Input input;
    private double lastInputLogSeconds;

    public float SensorWidthMm { get; set; } = 36f;
    public float SensorHeightMm { get; set; } = 24f;
    public float FocalLengthMm { get; set; } = 25f;
    public float Aperture { get; set; }
    public float FocusDistance { get; set; } = 4f;
    public float BokehBias { get; set; } = 1f;

    public float MoveSpeed { get; private set; } = 12f;
    public Vector3 ArcballPivot { get; set; } = Vector3.Zero;
    public Vector3 Position => position;
    public Quaternion Rotation => rotation;

    public PerspectiveCamera(Input input)
    {
        this.input = input;
    }

    public void SetArcballPivot(Vector3 pivot)
    {
        ArcballPivot = pivot;
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
    }

    public void PanInViewPlane(float deltaX, float deltaY)
    {
        if (Math.Abs(deltaX) < 0.000001f && Math.Abs(deltaY) < 0.000001f)
            return;

        var right = Vector3.Normalize(Vector3.Transform(LocalRight, rotation));
        var up = Vector3.Normalize(Vector3.Transform(LocalUp, rotation));
        var distance = Math.Max(1f, Vector3.Distance(position, ArcballPivot));
        var sensitivity = 0.0015f * distance;
        var offset = right * (-deltaX * sensitivity) + up * (deltaY * sensitivity);

        position += offset;
    }

    public void Dolly(float amount)
    {
        if (Math.Abs(amount) < 0.000001f)
            return;

        var forward = Vector3.Normalize(Vector3.Transform(LocalForward, rotation));
        var distance = Math.Max(1f, Vector3.Distance(position, ArcballPivot));
        var step = amount * Math.Max(0.25f, distance * 0.05f);
        position += forward * step;
    }

    public void SetPosition(Vector3 value)
    {
        position = value;
    }

    public void SetRotation(Quaternion value)
    {
        rotation = Quaternion.Normalize(value);
    }

    public bool Update(
        PixelSize renderSize,
        float deltaTimeSeconds,
        out CameraDataGpu cameraData)
    {
        var changed = false;
        var wheelDelta = input.ConsumeWheelDelta();
        var mouseDelta = input.ConsumeMouseDelta();
        var leftMouseDown = input.LeftMouseDown;
        var rightMouseDown = input.RightMouseDown;
        var dragActive = leftMouseDown || rightMouseDown;
        var flyMode = dragActive;
        var hasMovementKeys = input.IsKeyDown(Key.W) || input.IsKeyDown(Key.A) || input.IsKeyDown(Key.S) ||
                              input.IsKeyDown(Key.D) || input.IsKeyDown(Key.Q) || input.IsKeyDown(Key.E);

        if (Math.Abs(wheelDelta) > 0.0001f)
        {
            Dolly(wheelDelta * WheelDollyScale);
            changed = true;
        }

        if (flyMode)
        {
            changed |= UpdateFly(mouseDelta, deltaTimeSeconds);
        }

        if ((Math.Abs(wheelDelta) > 0.0001f || mouseDelta.LengthSquared() > 0.0001f || hasMovementKeys || flyMode) &&
            (Environment.TickCount64 / 1000.0 - lastInputLogSeconds) > 0.25)
        {
            lastInputLogSeconds = Environment.TickCount64 / 1000.0;
            Log.Debug(
                "Camera input frame: LMB={Lmb} RMB={Rmb} Fly={Fly} mouseDelta=({Dx:0.00},{Dy:0.00}) wheel={Wheel:0.00} moveKeys={MoveKeys} pos=({Px:0.00},{Py:0.00},{Pz:0.00})",
                leftMouseDown,
                rightMouseDown,
                flyMode,
                mouseDelta.X,
                mouseDelta.Y,
                wheelDelta,
                hasMovementKeys,
                position.X,
                position.Y,
                position.Z);
        }

        cameraData = BuildCameraData(renderSize);
        return changed;
    }

    internal static bool IsCameraDataEquivalent(in CameraDataGpu a, in CameraDataGpu b)
    {
        var epsilonSq = CameraDataEpsilon * CameraDataEpsilon;
        return Vector3.DistanceSquared(a.Position, b.Position) <= epsilonSq &&
               Vector3.DistanceSquared(a.Direction, b.Direction) <= epsilonSq &&
               Vector3.DistanceSquared(a.Horizontal, b.Horizontal) <= epsilonSq &&
               Vector3.DistanceSquared(a.Vertical, b.Vertical) <= epsilonSq &&
               MathF.Abs(a.FocalLength - b.FocalLength) <= CameraDataEpsilon &&
               MathF.Abs(a.FocusDistance - b.FocusDistance) <= CameraDataEpsilon &&
               MathF.Abs(a.Aperture - b.Aperture) <= CameraDataEpsilon &&
               MathF.Abs(a.BokehBias - b.BokehBias) <= CameraDataEpsilon;
    }

    private bool UpdateFly(Vector2 mouseDeltaPixels, float deltaTimeSeconds)
    {
        var changed = false;
        var positionBefore = position;
        var rotationBefore = rotation;
        var moveForward = input.IsKeyDown(Key.W);
        var moveBackward = input.IsKeyDown(Key.S);
        var moveLeft = input.IsKeyDown(Key.A);
        var moveRight = input.IsKeyDown(Key.D);
        var moveUp = input.IsKeyDown(Key.E);
        var moveDown = input.IsKeyDown(Key.Q);

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
        if (IsShiftDown())
            speed *= SpeedBoostMultiplier;

        var forward = Vector3.Normalize(Vector3.Transform(LocalForward, rotation));
        var up = Vector3.Normalize(Vector3.Transform(LocalUp, rotation));
        var right = Vector3.Normalize(Vector3.Cross(forward, up));

        if (moveForward)
            position += forward * speed;
        if (moveBackward)
            position -= forward * speed;
        if (moveLeft)
            position -= right * speed;
        if (moveRight)
            position += right * speed;
        if (moveUp)
            position += up * speed;
        if (moveDown)
            position -= up * speed;

        if (position != positionBefore || rotation != rotationBefore)
            changed = true;

        return changed;
    }

    private CameraDataGpu BuildCameraData(PixelSize renderSize)
    {
        var width = Math.Max(1, renderSize.Width);
        var height = Math.Max(1, renderSize.Height);
        var aspectRatio = (float)width / height;

        var direction = Vector3.Normalize(Vector3.Transform(LocalForward, rotation));
        var right = Vector3.Normalize(Vector3.Transform(LocalRight, rotation));
        var up = Vector3.Normalize(Vector3.Transform(LocalUp, rotation));

        // Match NoorRay camera basis conversion (sensor in mm converted to meters).
        var horizontal = right * (SensorWidthMm * 0.001f);
        var vertical = up * ((SensorWidthMm / aspectRatio) * 0.001f);

        return new CameraDataGpu
        {
            Position = position,
            Direction = direction,
            Horizontal = horizontal,
            Vertical = vertical,
            FocalLength = FocalLengthMm,
            Aperture = Aperture,
            FocusDistance = FocusDistance,
            BokehBias = BokehBias
        };
    }

    private bool IsShiftDown() => input.IsKeyDown(Key.LeftShift) || input.IsKeyDown(Key.RightShift);

    private static float DegreesToRadians(float degrees) => degrees * (MathF.PI / 180f);
}
