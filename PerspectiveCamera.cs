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

    public float MoveSpeed { get; private set; } = 5f;
    public Vector3 ArcballPivot { get; set; } = Vector3.Zero;

    public PerspectiveCamera(Input input)
    {
        this.input = input;
    }

    public void FocusOn(Matrix4x4 worldTransform, float distance = 500f)
    {
        var target = new Vector3(worldTransform.M41, worldTransform.M42, worldTransform.M43);
        var safeDistance = Math.Max(10f, distance);
        ArcballPivot = target;
        rotation = Quaternion.Identity;
        position = target - (LocalForward * safeDistance);
    }

    public bool Update(
        PixelSize renderSize,
        float deltaTimeSeconds,
        out CameraDataGpu cameraData)
    {
        var changed = false;
        var wheelDelta = input.ConsumeWheelDelta();
        var mouseDelta = input.ConsumeMouseDelta();
        var rightMouseDown = input.RightMouseDown;
        var hasMovementKeys = input.IsKeyDown(Key.W) || input.IsKeyDown(Key.A) || input.IsKeyDown(Key.S) ||
                              input.IsKeyDown(Key.D) || input.IsKeyDown(Key.Q) || input.IsKeyDown(Key.E);

        if (Math.Abs(wheelDelta) > 0.0001f)
        {
            MoveSpeed = Math.Clamp(MoveSpeed + wheelDelta, 0.25f, 50f);
            changed = true;
        }

        if (rightMouseDown)
        {
            var arcballMode = IsAltDown();
            if (arcballMode)
                changed |= UpdateArcball(mouseDelta, deltaTimeSeconds);
            else
                changed |= UpdateFly(mouseDelta, deltaTimeSeconds);
        }

        if ((Math.Abs(wheelDelta) > 0.0001f || mouseDelta.LengthSquared() > 0.0001f || hasMovementKeys || rightMouseDown) &&
            (Environment.TickCount64 / 1000.0 - lastInputLogSeconds) > 0.25)
        {
            lastInputLogSeconds = Environment.TickCount64 / 1000.0;
            Log.Debug(
                "Camera input frame: RMB={Rmb} mouseDelta=({Dx:0.00},{Dy:0.00}) wheel={Wheel:0.00} moveKeys={MoveKeys} pos=({Px:0.00},{Py:0.00},{Pz:0.00})",
                rightMouseDown,
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

    private bool UpdateArcball(Vector2 mouseDeltaPixels, float deltaTimeSeconds)
    {
        var changed = false;
        var positionBefore = position;
        var rotationBefore = rotation;
        var moveForward = input.IsKeyDown(Key.W);
        var moveBackward = input.IsKeyDown(Key.S);

        var moveSpeed = deltaTimeSeconds * MoveSpeed;
        if (IsShiftDown())
            moveSpeed *= SpeedBoostMultiplier;

        var toCamera = Vector3.Normalize(position - ArcballPivot);
        if (moveForward)
            position -= toCamera * moveSpeed;
        if (moveBackward)
            position += toCamera * moveSpeed;

        var yawAngle = -mouseDeltaPixels.X * ArcballSensitivity;
        var pitchAngle = -mouseDeltaPixels.Y * ArcballSensitivity;

        if (Math.Abs(yawAngle) > 0.000001f || Math.Abs(pitchAngle) > 0.000001f)
        {
            var yawQuat = Quaternion.CreateFromAxisAngle(Vector3.Normalize(WorldUp), yawAngle);
            var localRight = Vector3.Transform(LocalRight, rotation);
            var pitchQuat = Quaternion.CreateFromAxisAngle(Vector3.Normalize(localRight), pitchAngle);

            var offset = position - ArcballPivot;
            var rotationDelta = Quaternion.Normalize(yawQuat * pitchQuat);
            offset = Vector3.Transform(offset, rotationDelta);
            position = ArcballPivot + offset;
            rotation = Quaternion.Normalize(yawQuat * pitchQuat * rotation);
        }

        if (position != positionBefore || rotation != rotationBefore)
            changed = true;

        return changed;
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

    private bool IsAltDown() => input.IsKeyDown(Key.LeftAlt) || input.IsKeyDown(Key.RightAlt);

    private bool IsShiftDown() => input.IsKeyDown(Key.LeftShift) || input.IsKeyDown(Key.RightShift);

    private static float DegreesToRadians(float degrees) => degrees * (MathF.PI / 180f);
}
