using System;
using System.Numerics;
using Veldrid;
using Veldrid.Sdl2;

namespace UniversalUmap.Rendering.Camera;

public class Camera
{
    public Camera(Vector3 position, Vector3 direction, float aspectRatio)
    {
        Position = position;
        Direction = direction;
        AspectRatio = aspectRatio;
    }
    
    private Vector3 Up => Vector3.UnitY;
    private Vector3 Position;
    private Vector3 PositionArc => Position - Direction;
    private Vector3 Direction;
    private Vector3 DirectionArc => Direction - Position;

    private float Fov = 90f;
    private float Far = 100000f;
    private float Near = 10f;
    private float MouseSpeed = 1f;
    private float FlySpeed = 1f;
    
    private float AspectRatio;

    private Vector4 FrontVector => new(Vector3.Normalize(DirectionArc), 0f);
    private Matrix4x4 ViewMatrix => Matrix4x4.CreateLookAt(Position, Direction, Up);
    private Matrix4x4 ProjectionMatrix => Matrix4x4.CreatePerspectiveFieldOfView(Fov * (float)Math.PI / 180f, AspectRatio, Near, Far);
    
    public CameraUniform Update(double deltaTime, Sdl2Window window)
    {
        InputTracker.UpdateFrameInput(window.PumpEvents(), window);
        Modify(deltaTime);
        return new CameraUniform(ProjectionMatrix, ViewMatrix, FrontVector);
    }
    
    
    private void Modify(double deltaTime)
    {
        //Mouse
        var mouseDelta = InputTracker.MouseDelta * MouseSpeed * 0.01f;

        var right = Vector3.Normalize(Vector3.Cross(DirectionArc, Up));

        //Combine rotations
        var rotation = Matrix4x4.CreateFromAxisAngle(right, -mouseDelta.Y) * Matrix4x4.CreateFromAxisAngle(-Up, mouseDelta.X);

        Direction = Vector3.Transform(DirectionArc, rotation) + Position;
        
        //Keyboard
        var multiplier = InputTracker.GetKey(Key.ShiftLeft) ? 3000f : 700f * FlySpeed;
        float moveSpeed = (float)(multiplier * deltaTime);
        var moveAxis = Vector3.Normalize(-PositionArc);
        var panAxis = Vector3.Normalize(Vector3.Cross(moveAxis, Up));
        
        if (InputTracker.GetKey(Key.W)) // forward
        {
            var d = moveSpeed * moveAxis;
            Position += d;
            Direction += d;
        }
        if (InputTracker.GetKey(Key.S)) // backward
        {
            var d = moveSpeed * moveAxis;
            Position -= d;
            Direction -= d;
        }
        if (InputTracker.GetKey(Key.A)) // left
        {
            var d = panAxis * moveSpeed;
            Position -= d;
            Direction -= d;
        }
        if (InputTracker.GetKey(Key.D)) // right
        {
            var d = panAxis * moveSpeed;
            Position += d;
            Direction += d;
        }
        if (InputTracker.GetKey(Key.Q)) // down
        {
            var d = moveSpeed * Up;
            Position -= d;
            Direction -= d;
        }
        if (InputTracker.GetKey(Key.E)) // up
        {
            var d = moveSpeed * Up;
            Position += d;
            Direction += d;
        }

        if (InputTracker.GetKey(Key.C)) // zoom in
            ZoomAmount(+0.5f);
        if (InputTracker.GetKey(Key.X)) // zoom out
            ZoomAmount(-0.5f);
    }

    private void ZoomAmount(float amount)
    {
        Fov = Math.Clamp(Fov - amount, 25f, 120f);
    }

    public void Resize(float aspectRatio)
    {
        AspectRatio = aspectRatio;
    }
}

